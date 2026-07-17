using System.Net;
using System.Net.Sockets;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Overlay;

const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
int port = FindAvailablePort();
OverlayConfiguration configuration = new(
    OverlayConfiguration.CurrentSchemaVersion,
    new OverlayEndpointConfiguration(port, OverlayAccessToken.Parse(Token)),
    new TotalDeathsOverlayOptions(isEnabled: true, showGameName: false),
    new BossListOverlayOptions(isEnabled: true, BossListVisibilityMode.Defeated));
PersistentTrackerState state = new(
    PersistentTrackerState.CurrentSchemaVersion,
    GameId.Bloodborne,
    ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, initialValue: 6),
    BossProgress.Empty.MarkDefeated(GameId.Bloodborne, GameCatalog.GetRequired(GameId.Bloodborne).BossCatalog[0].Id),
    configuration);
await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(state), new NullPublisher());
await using var service = new SecureOverlayService(coordinator, new EndpointAccessFactory());
await service.StartAsync();
Console.WriteLine($"READY {service.TotalDeathsUrl} {service.BossListUrl}");
await Task.Delay(Timeout.InfiniteTimeSpan);

static int FindAvailablePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

sealed class MemoryRepository(PersistentTrackerState state) : ITrackerStateRepository
{
    public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
    public Task SaveAsync(PersistentTrackerState saved, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class NullPublisher : ITrackerStateChangePublisher
{
    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class EndpointAccessFactory : IOverlayEndpointAccessFactory
{
    public IOverlayEndpointAccess Create(int port) => new EndpointAccess(port);
    public IOverlayEndpointAccess FromConfiguration(OverlayEndpointConfiguration configuration) => new EndpointAccess(configuration.Port!.Value);
}

sealed class EndpointAccess(int port) : IOverlayEndpointAccess
{
    private const string AccessToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public OverlayEndpointConfiguration Configuration { get; } = new(port, OverlayAccessToken.Parse(AccessToken));
    public bool IsAuthorized(string? suppliedToken) => string.Equals(AccessToken, suppliedToken, StringComparison.Ordinal);
    public string BuildCanonicalUrl(string route) => $"http://127.0.0.1:{port}{route}?token={AccessToken}";
}
