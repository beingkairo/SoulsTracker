using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Overlay;

/// <summary>
/// Owns the optional sign-in endpoint. It never reads game/save data: until an
/// authenticated desktop process supplies a snapshot it sends a placeholder.
/// </summary>
public sealed class PersistentOverlayHost : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOverlayEndpointAccess endpoint;
    private readonly object synchronization = new();
    private WebApplication? application;
    private byte[] payload;
    private long sequence;

    public PersistentOverlayHost(IOverlayEndpointAccess endpoint)
    {
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (!endpoint.Configuration.IsAssigned) throw new ArgumentException("A persisted overlay endpoint is required.", nameof(endpoint));
        payload = CreatePlaceholderPayload(0);
    }

    public int Port => endpoint.Configuration.Port!.Value;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (application is not null) throw new InvalidOperationException("The persistent overlay host is already running.");
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Port));
        application = builder.Build();
        application.UseWebSockets();
        foreach (string route in new[] { "/overlay/total_deaths", "/overlay/boss_list", "/overlay/deaths", "/overlay/boss-progress" })
            application.MapGet(route, (HttpContext context) => IsAuthorized(context)
                ? Results.Content(CreateOverlayShell(context.Request.Query["token"].Single()!), "text/html; charset=utf-8")
                : Results.NotFound());
        application.MapGet("/overlay/assets/{assetName}", (HttpContext context, string assetName) =>
            IsAuthorized(context) && OverlayAssetCatalog.TryGet(assetName, out OverlayAssetCatalog.OverlayAsset asset)
                ? Results.File(asset.ReadBytes(), asset.ContentType)
                : Results.NotFound());
        application.Map("/overlay/ws", HandleWebSocketAsync);
        try { await application.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch { await application.DisposeAsync().ConfigureAwait(false); application = null; throw; }
    }

    /// <summary>Accepts a bounded browser-safe snapshot only after pipe authentication.</summary>
    public bool AcceptLivePayload(string proof, ReadOnlySpan<byte> candidate)
    {
        if (!endpoint.IsAuthorized(proof) || candidate.Length is 0 or > 1_048_576) return false;
        try
        {
            JsonObject document = JsonNode.Parse(candidate)?.AsObject() ?? throw new JsonException();
            if (document["SchemaVersion"]?.GetValue<int>() != OverlaySnapshot.CurrentSchemaVersion || document["Presentation"] is null) return false;
            document.Remove("HostStatus");
            document["SequenceNumber"] = NextSequence();
            byte[] accepted = Encoding.UTF8.GetBytes(document.ToJsonString());
            lock (synchronization) payload = accepted;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Clears all desktop state immediately; the existing browser socket receives the placeholder.</summary>
    public bool ShowPlaceholder(string proof)
    {
        if (!endpoint.IsAuthorized(proof)) return false;
        lock (synchronization) payload = CreatePlaceholderPayload(NextSequence());
        return true;
    }

    private bool IsAuthorized(HttpContext context) => endpoint.IsAuthorized(context.Request.Query["token"].SingleOrDefault());

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!IsAuthorized(context) || !context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        long delivered = -1;
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            byte[] current;
            long currentSequence;
            lock (synchronization) { current = payload; currentSequence = sequence; }
            if (currentSequence != delivered)
            {
                await socket.SendAsync(current, WebSocketMessageType.Text, true, context.RequestAborted).ConfigureAwait(false);
                delivered = currentSequence;
            }
            await Task.Delay(25, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private long NextSequence() => checked(++sequence);

    private static byte[] CreatePlaceholderPayload(long sequence)
    {
        OverlaySnapshot snapshot = new(OverlaySnapshot.CurrentSchemaVersion, sequence, DateTimeOffset.UtcNow, null, TotalDeathsDisplayValue.Unavailable, []);
        JsonObject result = JsonNode.Parse(JsonSerializer.Serialize(snapshot, SnapshotJsonOptions))!.AsObject();
        result["HostStatus"] = "Please open SoulsTracker";
        return Encoding.UTF8.GetBytes(result.ToJsonString());
    }

    private static string CreateOverlayShell(string token)
    {
        string query = $"?token={Uri.EscapeDataString(token)}";
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/overlay/assets/overlay-bootstrap.css{query}\"></head><body><div id=\"souls-tracker-overlay\"></div><script type=\"module\" src=\"/overlay/assets/overlay-bootstrap.js{query}\"></script></body></html>";
    }

    public async ValueTask DisposeAsync()
    {
        WebApplication? running = Interlocked.Exchange(ref application, null);
        if (running is null) return;
        using var cancellation = new CancellationTokenSource(ShutdownTimeout);
        try { await running.StopAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { await running.DisposeAsync().ConfigureAwait(false); }
    }
}
