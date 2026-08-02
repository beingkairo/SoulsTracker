using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Overlay;

/// <summary>Owns the loopback-only, read-only OBS transport. It deliberately has no browser mutation API.</summary>
public sealed class SecureOverlayService : IAsyncDisposable
{
    // OBS and the desktop preview hold WebSocket connections open by design.  The
    // generic host defaults to a multi-second graceful shutdown window for those
    // clients, which made closing the desktop feel stalled.  Give local clients a
    // short chance to observe the close, then let Kestrel cancel/abort them so all
    // owned server resources can still be disposed deterministically.
    private static readonly TimeSpan LocalOverlayShutdownTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly SerializedTrackerCoordinator coordinator;
    private readonly IOverlayEndpointAccessFactory endpointAccessFactory;
    private WebApplication? application;
    private IOverlayEndpointAccess? endpointAccess;
    private PersistentTrackerState? trackerState;
    private RuntimeGameObservation? runtimeObservation;
    private readonly object snapshotSynchronization = new();
    private int activeWebSocketConnectionCount;
    private long sequence;
    private OverlaySnapshot snapshot = EmptySnapshot();

    public SecureOverlayService(SerializedTrackerCoordinator coordinator, IOverlayEndpointAccessFactory endpointAccessFactory)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.endpointAccessFactory = endpointAccessFactory ?? throw new ArgumentNullException(nameof(endpointAccessFactory));
    }

    public int Port { get; private set; }

    /// <summary>Starts the service after explicitly loading and, only on first run, persistently assigning its endpoint.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (application is not null) throw new InvalidOperationException("The overlay service is already running.");
        TrackerStateLoadResult loaded = await coordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) throw new InvalidOperationException("The local tracker state could not be loaded.");

        OverlayEndpointConfiguration endpoint = loaded.State!.OverlayConfiguration.Endpoint;
        if (!endpoint.IsAssigned)
        {
            endpointAccess = endpointAccessFactory.Create(FindAvailablePort());
            endpoint = endpointAccess.Configuration;
            PersistentTrackerState saved = await coordinator.SetOverlayEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
            endpoint = saved.OverlayConfiguration.Endpoint;
        }

        endpointAccess ??= endpointAccessFactory.FromConfiguration(endpoint);

        Port = endpoint.Port!.Value;
        trackerState = loaded.State!;
        snapshot = CreateSnapshot(trackerState, runtimeObservation, sequence);
        application = BuildApplication(Port);
        try { await application.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            await application.DisposeAsync().ConfigureAwait(false); application = null;
            throw new InvalidOperationException("SoulsTracker could not bind its saved local overlay port. Close the application using that port or choose a new overlay endpoint in settings.", ex);
        }
    }

    /// <summary>Publishes the most recent secret-free snapshot. Slow clients fetch the newest state over the socket.</summary>
    public void Publish(PersistentTrackerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (snapshotSynchronization)
        {
            trackerState = state;
            if (!CanUseRuntimeObservation(state, runtimeObservation))
            {
                runtimeObservation = null;
            }
            PublishSnapshot(state, runtimeObservation);
        }
    }

    /// <summary>Publishes a runtime-only reader observation without persisting or retaining an unavailable value.</summary>
    public void PublishRuntimeObservation(RuntimeGameObservation? observation)
    {
        lock (snapshotSynchronization)
        {
            if (trackerState is null) return;
            runtimeObservation = CanUseRuntimeObservation(trackerState, observation) ? observation : null;
            PublishSnapshot(trackerState, runtimeObservation);
        }
    }

    public string TotalDeathsUrl => CanonicalUrl("/overlay/total_deaths");
    public string BossListUrl => CanonicalUrl("/overlay/boss_list");
    public int ActiveWebSocketConnectionCount => Volatile.Read(ref activeWebSocketConnectionCount);

    private string CanonicalUrl(string path)
    {
        if (endpointAccess is null || Port == 0) throw new InvalidOperationException("The overlay service has not started.");
        return endpointAccess.BuildCanonicalUrl(path);
    }

    private WebApplication BuildApplication(int port)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = LocalOverlayShutdownTimeout);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        WebApplication app = builder.Build();
        app.UseWebSockets();
        foreach (string route in new[] { "/overlay/total_deaths", "/overlay/boss_list", "/overlay/deaths", "/overlay/boss-progress" })
            app.MapGet(route, (HttpContext context) => TryGetAuthorizedToken(context, out string? token)
                ? Results.Content(CreateOverlayShell(token), "text/html; charset=utf-8")
                : Results.NotFound());
        app.MapGet("/overlay/assets/{assetName}", (HttpContext context, string assetName) =>
            TryGetAuthorizedToken(context, out _) && OverlayAssetCatalog.TryGet(assetName, out OverlayAssetCatalog.OverlayAsset asset)
                ? Results.File(asset.ReadBytes(), asset.ContentType)
                : Results.NotFound());
        app.Map("/overlay/ws", HandleWebSocketAsync);
        return app;
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!TryGetAuthorizedToken(context, out _) || !context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        Interlocked.Increment(ref activeWebSocketConnectionCount);
        try
        {
            long delivered = -1;
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                OverlaySnapshot? currentSnapshot = null;
                long current = delivered;
                lock (snapshotSynchronization)
                {
                    current = sequence;
                    if (current != delivered)
                    {
                        currentSnapshot = snapshot;
                    }
                }
                if (currentSnapshot is not null)
                {
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(currentSnapshot, SnapshotJsonOptions);
                    await socket.SendAsync(json, WebSocketMessageType.Text, true, context.RequestAborted).ConfigureAwait(false);
                    delivered = current;
                }
                await Task.Delay(25, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref activeWebSocketConnectionCount);
        }
    }

    private bool TryGetAuthorizedToken(HttpContext context, out string? token)
    {
        token = context.Request.Query["token"].SingleOrDefault();
        return endpointAccess is not null && endpointAccess.IsAuthorized(token);
    }

    private void PublishSnapshot(PersistentTrackerState state, RuntimeGameObservation? observation)
    {
        long nextSequence = checked(sequence + 1);
        snapshot = CreateSnapshot(state, observation, nextSequence);
        Volatile.Write(ref sequence, nextSequence);
    }

    private static OverlaySnapshot CreateSnapshot(PersistentTrackerState state, RuntimeGameObservation? observation, long sequenceNumber)
    {
        OverlayGameMetadata game = new(state.SelectedGameId);
        TotalDeathsDisplayValue deaths = CanUseRuntimeObservation(state, observation)
            ? TotalDeathsDisplayValue.FromRuntimeObservation(observation!)
            : GameCatalog.GetRequired(state.SelectedGameId).TrackingMode == GameTrackingMode.ManualOnly
                ? TotalDeathsDisplayValue.FromManualCounter(state.SelectedGameId, state.GetManualDeathCounter(state.SelectedGameId))
                : state.SelectedGameId == GameId.BlackMythWukong || state.SelectedGameId == GameId.LiesOfP
                    ? TotalDeathsDisplayValue.UnavailableForSelectedGame(state.SelectedGameId)
                : TotalDeathsDisplayValue.FromUnavailableSelectedGame(state.SelectedGameId);
        long? combinedTotal = TotalDeathsDisplayProjection.Combine(state, CanUseRuntimeObservation(state, observation) ? observation : null);
        if (combinedTotal.HasValue && deaths.GameId == GameId.EldenRing)
        {
            deaths = TotalDeathsDisplayValue.WithNumericValue(deaths, combinedTotal.Value);
        }
        IEnumerable<OverlayBossEntry> bosses = BossCatalogDisplayFilter.Apply(GameCatalog.GetRequired(state.SelectedGameId), state.BossListScope)
                .Select(b => new OverlayBossEntry(b, state.BossProgress.IsDefeated(state.SelectedGameId, b.Id)));
        return new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber,
            DateTimeOffset.UtcNow,
            game,
            deaths,
            bosses,
            OverlayPresentationConfiguration.From(state.OverlayConfiguration));
    }

    private static bool CanUseRuntimeObservation(PersistentTrackerState state, RuntimeGameObservation? observation) =>
        observation?.GameId == state.SelectedGameId &&
        (state.SelectedGameId != GameId.BlackMythWukong || state.BlackMythWukongSave.LocalPath is not null) &&
        (state.SelectedGameId != GameId.LiesOfP || state.LiesOfPSave.LocalPath is not null);

    private static OverlaySnapshot EmptySnapshot() => new(1, 0, DateTimeOffset.UtcNow, null, TotalDeathsDisplayValue.Unavailable, []);
    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static string CreateOverlayShell(string? token)
    {
        string query = $"?token={Uri.EscapeDataString(token!)}";
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/overlay/assets/overlay-bootstrap.css{query}\"></head><body><div id=\"souls-tracker-overlay\"></div><script type=\"module\" src=\"/overlay/assets/overlay-bootstrap.js{query}\"></script></body></html>";
    }

    public async ValueTask DisposeAsync()
    {
        WebApplication? runningApplication = Interlocked.Exchange(ref application, null);
        if (runningApplication is null)
        {
            return;
        }

        using var shutdownCancellation = new CancellationTokenSource(LocalOverlayShutdownTimeout);
        try
        {
            await runningApplication.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
        {
            // The bounded local grace window elapsed. Kestrel has been asked to
            // cancel outstanding requests; DisposeAsync below releases the host
            // and its sockets rather than leaving a server task behind.
        }
        finally
        {
            await runningApplication.DisposeAsync().ConfigureAwait(false);
        }
    }
}
