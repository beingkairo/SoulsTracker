using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Overlay;

namespace SoulsTracker.Overlay.Tests;

public sealed class SecureOverlayServiceTests
{
    [Fact]
    public async Task DisposeWithAnOpenWebSocketCompletesPromptlyAfterTheLocalGracePeriod()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory());
        await service.StartAsync();

        Uri socketUrl = new UriBuilder(service.TotalDeathsUrl) { Scheme = "ws", Path = "/overlay/ws" }.Uri;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUrl, CancellationToken.None);
        await socket.ReceiveAsync(new byte[16_384], CancellationToken.None);

        Stopwatch stopwatch = Stopwatch.StartNew();
        await service.DisposeAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Local overlay shutdown took {stopwatch.Elapsed}; an open browser socket must not make desktop close feel stalled.");
    }

    [Fact]
    public async Task FirstStartPersistsStableEndpointAndProtectsAllOverlayAliases()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory());
        await service.StartAsync();

        Assert.True(repository.State.OverlayConfiguration.Endpoint.IsAssigned);
        Assert.Contains("127.0.0.1", service.TotalDeathsUrl, StringComparison.Ordinal);
        Assert.Contains("token=", service.TotalDeathsUrl, StringComparison.Ordinal);
        using var client = new HttpClient();
        foreach (string route in new[] { "/overlay/total_deaths", "/overlay/boss_list", "/overlay/deaths", "/overlay/boss-progress" })
        {
            HttpResponseMessage allowed = await client.GetAsync($"http://127.0.0.1:{service.Port}{route}?{new Uri(service.TotalDeathsUrl).Query.TrimStart('?')}");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            HttpResponseMessage denied = await client.GetAsync($"http://127.0.0.1:{service.Port}{route}");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{service.Port}/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{service.Port}/diagnostics")).StatusCode);
    }

    [Fact]
    public async Task AuthorizedShellBootstrapsOnlyTheEmbeddedAllowlistedAssets()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory());
        await service.StartAsync();

        Uri page = new(service.TotalDeathsUrl);
        using var client = new HttpClient();
        string shell = await client.GetStringAsync(page);
        Assert.Contains("/overlay/assets/overlay-bootstrap.css?token=", shell, StringComparison.Ordinal);
        Assert.Contains("/overlay/assets/overlay-bootstrap.js?token=", shell, StringComparison.Ordinal);

        foreach ((string asset, string contentType) in new[]
                 {
                     ("overlay-bootstrap.js", "text/javascript"),
                     ("overlay-bootstrap.css", "text/css")
                 })
        {
            HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{service.Port}/overlay/assets/{asset}{page.Query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith(contentType, response.Content.Headers.ContentType!.MediaType!, StringComparison.Ordinal);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{service.Port}/overlay/assets/overlay-bootstrap.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{service.Port}/overlay/assets/not-allowlisted.js{page.Query}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync($"http://127.0.0.1:{service.Port}/overlay/assets/%2e%2e%2fSecureOverlayService.cs{page.Query}")).StatusCode);
    }

    [Fact]
    public async Task StoredPortCollisionFailsWithoutSilentlyChangingEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string rawToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        OverlayAccessToken token = OverlayAccessToken.Parse(rawToken);
        PersistentTrackerState state = WithEndpoint(port, token);
        var repository = new MemoryRepository(state);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory(rawToken));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
        Assert.DoesNotContain(rawToken, exception.Message, StringComparison.Ordinal);
        Assert.Equal(port, repository.State.OverlayConfiguration.Endpoint.Port);
    }

    [Fact]
    public async Task PublishProducesSecretFreeSnapshot()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory());
        await service.StartAsync();
        service.Publish(repository.State);
        Uri httpUrl = new(service.TotalDeathsUrl);
        var websocketUrl = new UriBuilder(httpUrl) { Scheme = "ws", Path = "/overlay/ws" }.Uri;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(websocketUrl, CancellationToken.None);
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        string payload = Encoding.UTF8.GetString(buffer, 0, received.Count);
        Assert.Contains("SequenceNumber", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(httpUrl.Query.TrimStart('?').Replace("token=", string.Empty, StringComparison.Ordinal), payload, StringComparison.Ordinal);
        using var client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{service.Port}/not-a-mutation-route");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(httpUrl.Query.TrimStart('?').Replace("token=", string.Empty, StringComparison.Ordinal), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DemonsSoulsProjectsItsIndependentManualTotalToTheOverlay()
    {
        PersistentTrackerState state = new(1, GameId.DemonsSouls, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 3), BossProgress.Empty, OverlayConfiguration.Default, manualDemonsSoulsDeathCounter: ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls, 8));
        var repository = new MemoryRepository(state);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory());
        await service.StartAsync();
        service.Publish(state);

        using JsonDocument document = JsonDocument.Parse(await ReceiveSnapshotAsync(service));
        JsonElement deaths = document.RootElement.GetProperty("TotalDeaths");
        Assert.Equal(8, deaths.GetProperty("Value").GetInt64());
        Assert.NotEqual("Unavailable", deaths.GetProperty("Source").GetString());
    }

    [Fact]
    public async Task EldenRingBossOverlayUsesTheSamePersistedScopeAndRequiredFilter()
    {
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true,
            eldenRingSave: new EldenRingSaveConfiguration(null, 0, EldenRingBossListScope.ShadowOfTheErdtree, requiredBossesOnly: true));
        var repository = new MemoryRepository(state);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory());
        await service.StartAsync();
        service.Publish(state);

        string snapshot = await ReceiveSnapshotAsync(service);
        Assert.Contains("Promised Consort Radahn", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Blackgaol Knight", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealLoopbackHostProjectsValidatedPresentationWithoutEndpointSecrets()
    {
        const string rawToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        OverlayConfiguration configuration = new(
            OverlayConfiguration.CurrentSchemaVersion,
            new OverlayEndpointConfiguration(FindAvailablePort(), OverlayAccessToken.Parse(rawToken)),
            new TotalDeathsOverlayOptions(isEnabled: true, showGameName: false),
            new BossListOverlayOptions(isEnabled: true, BossListVisibilityMode.Defeated));
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.Bloodborne,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, initialValue: 6),
            BossProgress.Empty.MarkDefeated(GameId.Bloodborne, GameCatalog.GetRequired(GameId.Bloodborne).BossCatalog[0].Id),
            configuration);
        var repository = new MemoryRepository(state);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory(rawToken));
        await service.StartAsync();

        var socketUrl = new UriBuilder(service.TotalDeathsUrl) { Scheme = "ws", Path = "/overlay/ws" }.Uri;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUrl, CancellationToken.None);
        byte[] buffer = new byte[16_384];
        WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        string payload = Encoding.UTF8.GetString(buffer, 0, received.Count);

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement presentation = document.RootElement.GetProperty("Presentation");
        Assert.True(presentation.GetProperty("IsTotalDeathsEnabled").GetBoolean());
        Assert.False(presentation.GetProperty("ShowGameName").GetBoolean());
        Assert.True(presentation.GetProperty("IsBossListEnabled").GetBoolean());
        Assert.Equal("Defeated", presentation.GetProperty("BossListVisibilityMode").GetString());
        Assert.DoesNotContain(rawToken, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Endpoint\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SerializedPresentationCommandPublishesUpdatedTypedSnapshotWithoutBrowserMutation()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        var publisher = new OverlayStateChangePublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory());
        publisher.Attach(service);
        await service.StartAsync();

        TrackerCommandExecutionResult result = await coordinator.SubmitAsync(
            new UpdateOverlayPresentationCommand(false, false, false, BossListVisibilityMode.Remaining));
        Assert.Equal(TrackerCommandExecutionStatus.Applied, result.Status);

        Uri socketUrl = new UriBuilder(service.TotalDeathsUrl) { Scheme = "ws", Path = "/overlay/ws" }.Uri;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUrl, CancellationToken.None);
        byte[] buffer = new byte[16_384];
        WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        string payload = Encoding.UTF8.GetString(buffer, 0, received.Count);

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement presentation = document.RootElement.GetProperty("Presentation");
        Assert.False(presentation.GetProperty("IsTotalDeathsEnabled").GetBoolean());
        Assert.False(presentation.GetProperty("ShowGameName").GetBoolean());
        Assert.False(presentation.GetProperty("IsBossListEnabled").GetBoolean());
        Assert.Equal("Remaining", presentation.GetProperty("BossListVisibilityMode").GetString());
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{service.Port}/presentation")).StatusCode);
    }

    [Fact]
    public async Task RuntimeObservationProjectsOnlyForMatchingSelectionAndClearsWithoutPersistence()
    {
        const string rawToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        PersistentTrackerState state = new(1, GameId.Ds1, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, new OverlayConfiguration(1, new OverlayEndpointConfiguration(FindAvailablePort(), OverlayAccessToken.Parse(rawToken)), TotalDeathsOverlayOptions.Default, BossListOverlayOptions.Default));
        var repository = new MemoryRepository(state);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var service = new SecureOverlayService(coordinator, new TestEndpointAccessFactory(rawToken));
        await service.StartAsync();

        service.PublishRuntimeObservation(new RuntimeGameObservation(GameId.Ds1, 12, DateTimeOffset.UtcNow));
        string available = await ReceiveSnapshotAsync(service);
        Assert.Contains("GameLifetimeReader", available, StringComparison.Ordinal);
        Assert.Contains("\"Value\":12", available, StringComparison.Ordinal);
        Assert.DoesNotContain("12", repository.State.ToString(), StringComparison.Ordinal);

        service.PublishRuntimeObservation(new RuntimeGameObservation(GameId.Ds2, 99, DateTimeOffset.UtcNow));
        string unavailable = await ReceiveSnapshotAsync(service);
        Assert.Contains("Unavailable", unavailable, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Value\":12", unavailable, StringComparison.Ordinal);

        service.PublishRuntimeObservation(null);
        string waiting = await ReceiveSnapshotAsync(service);
        Assert.Contains("Unavailable", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Value\":12", waiting, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenPersistenceBridgeIsVisibleOnlyToInfrastructure()
    {
        string[] friends = typeof(OverlayAccessToken).Assembly.CustomAttributes.Where(attribute => attribute.AttributeType == typeof(InternalsVisibleToAttribute)).Select(attribute => (string)attribute.ConstructorArguments[0].Value!).ToArray();
        Assert.Equal(["SoulsTracker.Infrastructure"], friends);
    }

    [Fact]
    public void OverlayDependsOnApplicationButNotInfrastructure()
    {
        string[] references = typeof(SecureOverlayService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        Assert.Contains("SoulsTracker.Application", references);
        Assert.DoesNotContain("SoulsTracker.Infrastructure", references);
    }

    private static PersistentTrackerState WithEndpoint(int port, OverlayAccessToken token) => new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, new OverlayConfiguration(1, new OverlayEndpointConfiguration(port, token), TotalDeathsOverlayOptions.Default, BossListOverlayOptions.Default));

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> ReceiveSnapshotAsync(SecureOverlayService service)
    {
        Uri socketUrl = new UriBuilder(service.TotalDeathsUrl) { Scheme = "ws", Path = "/overlay/ws" }.Uri;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUrl, CancellationToken.None);
        byte[] buffer = new byte[16_384];
        WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    private sealed class MemoryRepository(PersistentTrackerState state) : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = state;
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher { public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask; }

    private sealed class TestEndpointAccessFactory(string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") : IOverlayEndpointAccessFactory
    {
        public IOverlayEndpointAccess Create(int port) => new TestEndpointAccess(port, token);
        public IOverlayEndpointAccess FromConfiguration(OverlayEndpointConfiguration configuration) => new TestEndpointAccess(configuration.Port!.Value, token, configuration);
    }

    private sealed class TestEndpointAccess : IOverlayEndpointAccess
    {
        private readonly string token;
        public TestEndpointAccess(int port, string token, OverlayEndpointConfiguration? configuration = null)
        {
            this.token = token;
            Configuration = configuration ?? new OverlayEndpointConfiguration(port, OverlayAccessToken.Parse(token));
        }
        public OverlayEndpointConfiguration Configuration { get; }
        public bool IsAuthorized(string? suppliedToken) => string.Equals(token, suppliedToken, StringComparison.Ordinal);
        public string BuildCanonicalUrl(string route) => route is "/overlay/total_deaths" or "/overlay/boss_list" ? $"http://127.0.0.1:{Configuration.Port}{route}?token={token}" : throw new ArgumentException("Only canonical routes are valid.", nameof(route));
    }
}
