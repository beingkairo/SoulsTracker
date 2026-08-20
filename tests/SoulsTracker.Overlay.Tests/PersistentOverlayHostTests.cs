using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Overlay;

namespace SoulsTracker.Overlay.Tests;

public sealed class PersistentOverlayHostTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Fact]
    public async Task KeepsTheExistingTokenizedEndpointAndTransitionsBetweenPlaceholderAndLiveState()
    {
        int port = FindAvailablePort();
        var endpoint = new TestEndpointAccess(port);
        await using var host = new PersistentOverlayHost(endpoint);
        await host.StartAsync();

        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/overlay/total_deaths")).StatusCode);
        string shell = await http.GetStringAsync($"http://127.0.0.1:{port}/overlay/total_deaths?token={Token}");
        Assert.Contains("overlay-bootstrap.js?token=", shell, StringComparison.Ordinal);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/overlay/ws?token={Token}"), CancellationToken.None);
        Assert.Equal("Please open SoulsTracker", ReadStatus(await ReceiveAsync(socket)));

        OverlaySnapshot live = OverlaySnapshotFactory.Create(PersistentTrackerState.Default, null, 1);
        Assert.True(host.AcceptLivePayload(endpoint.BuildLocalHostProof(), SerializeSnapshot(live)));
        Assert.Null(ReadStatus(await ReceiveAsync(socket)));

        Assert.True(host.ShowPlaceholder(endpoint.BuildLocalHostProof()));
        Assert.Equal("Please open SoulsTracker", ReadStatus(await ReceiveAsync(socket)));
    }

    [Fact]
    public async Task RefusesInvalidPipeProofWithoutReplacingThePlaceholder()
    {
        await using var host = new PersistentOverlayHost(new TestEndpointAccess(FindAvailablePort()));
        OverlaySnapshot live = OverlaySnapshotFactory.Create(PersistentTrackerState.Default, null, 1);
        Assert.False(host.AcceptLivePayload("not-a-token", SerializeSnapshot(live)));
    }

    [Fact]
    public async Task CurrentUserPipeHandsOffLiveStateThenClearsAndStops()
    {
        var endpoint = new TestEndpointAccess(FindAvailablePort());
        await using var host = new PersistentOverlayHost(endpoint);
        await host.StartAsync();
        using var stopping = new CancellationTokenSource();
        Task server = OverlayHostPipe.RunServerAsync(host, stopping.Cancel, stopping.Token);
        OverlaySnapshot live = OverlaySnapshotFactory.Create(PersistentTrackerState.Default, null, 1);

        Assert.True(await OverlayHostPipe.SendSnapshotAsync(endpoint, live));
        Assert.True(await OverlayHostPipe.ClearAsync(endpoint));
        Assert.True(await OverlayHostPipe.StopAsync(endpoint));
        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HostClientForwardsRuntimeObservationsForAutomaticGames()
    {
        var endpoint = new TestEndpointAccess(FindAvailablePort());
        await using var host = new PersistentOverlayHost(endpoint);
        await host.StartAsync();
        using var stopping = new CancellationTokenSource();
        Task server = OverlayHostPipe.RunServerAsync(host, stopping.Cancel, stopping.Token);
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.Ds1,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default);
        await using var client = new OverlayHostClient(endpoint);
        Assert.True(await client.ConnectAsync(state));

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{endpoint.Configuration.Port}/overlay/ws?token={Token}"), CancellationToken.None);
        _ = await ReceiveAsync(socket); // initial safe state
        client.PublishRuntimeObservation(new RuntimeGameObservation(GameId.Ds1, 7, DateTimeOffset.UtcNow));

        using JsonDocument document = JsonDocument.Parse(await ReceiveAsync(socket));
        Assert.Equal("GameLifetimeReader", document.RootElement.GetProperty("TotalDeaths").GetProperty("Source").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("TotalDeaths").GetProperty("Value").GetInt32());

        Assert.True(await OverlayHostPipe.StopAsync(endpoint));
        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket)
    {
        byte[] buffer = new byte[64 * 1024];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static string? ReadStatus(string json) => JsonDocument.Parse(json).RootElement.TryGetProperty("HostStatus", out JsonElement status) ? status.GetString() : null;

    private static byte[] SerializeSnapshot(OverlaySnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonOptions);

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestEndpointAccess(int port) : IOverlayEndpointAccess
    {
        public OverlayEndpointConfiguration Configuration { get; } = new(port, OverlayAccessToken.Parse(Token));
        public bool IsAuthorized(string? suppliedToken) => string.Equals(Token, suppliedToken, StringComparison.Ordinal);
        public string BuildCanonicalUrl(string route) => $"http://127.0.0.1:{port}{route}?token={Token}";
        public string BuildLocalHostProof() => Token;
    }
}
