using System.Net;
using System.Text;
using System.Text.Json;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedProvisioningTests
{
    private const string Origin = "https://publisher.example.test";
    private static readonly string OverlayId = "a1" + new string('0', 62);

    [Fact]
    public void GenerationUsesThreeIndependentExactThirtyTwoByteValuesAndFixedV2Domains()
    {
        var calls = new List<int>();
        int value = 1;
        HostedProvisioningState state = HostedProvisioningState.Create(Origin, [Origin], size =>
        {
            calls.Add(size);
            return Enumerable.Repeat((byte)value++, size).ToArray();
        });
        Assert.Equal([32, 32, 32], calls);
        Assert.Equal(string.Concat(Enumerable.Repeat("01", 32)), state.RequestId);
        Assert.NotEqual(state.RequestId, state.ReadCapability);
        Assert.NotEqual(state.RequestId, state.WriteCapability);
        Assert.NotEqual(state.ReadCapability, state.WriteCapability);
        Assert.Equal("b09c8426cad8efb6635c84c9c74f9fdfe248a13858de0840e3333c909bb20f47", state.ReadVerifier);
        Assert.Equal("6bac3794925d1c3f9154ac61bb7a3cb1244babaa11a6b49dd61e4ba9151fc48e", state.WriteVerifier);
    }

    [Fact]
    public async Task PendingStateIsProtectedAtomicAndRestartsWithExactBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), path = Path.Combine(root, "pending.private");
        try
        {
            var store = new HostedProvisioningStateStore(path, new CurrentUserDpapiSecretProtector(), [Origin]);
            HostedProvisioningState state = HostedProvisioningState.Create(Origin, [Origin]);
            byte[] original = state.Encode();
            await store.SavePendingAsync(state);
            string protectedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain(Origin, protectedText);
            Assert.DoesNotContain(state.RequestId, protectedText);
            HostedProvisioningState loaded = Assert.IsType<HostedProvisioningState>(await store.LoadPendingAsync());
            Assert.Equal(original, loaded.Encode());
            HostedProvisioningState acknowledged = loaded.Acknowledged(OverlayId);
            await store.SavePendingAsync(acknowledged);
            Assert.Equal(OverlayId, Assert.IsType<HostedProvisioningState>(await store.LoadPendingAsync()).OverlayId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CorruptOrUnapprovedPendingStateFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), path = Path.Combine(root, "pending.private");
        try
        {
            Directory.CreateDirectory(root);
            var approved = new HostedProvisioningStateStore(path, new CurrentUserDpapiSecretProtector(), [Origin]);
            await File.WriteAllTextAsync(path, "broken");
            await Assert.ThrowsAsync<InvalidOperationException>(() => approved.LoadPendingAsync());
            File.Delete(path);
            var unapproved = new HostedProvisioningStateStore(path, new CurrentUserDpapiSecretProtector(), ["https://other.example.test"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unapproved.SavePendingAsync(HostedProvisioningState.Create(Origin, [Origin])));
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClientSendsVerifierOnlyBodyToFixedAnonymousRoute()
    {
        HostedProvisioningState state = HostedProvisioningState.Create(Origin, [Origin]);
        string? authorization = null, cookie = null, body = null, uri = null;
        using var client = new HostedOverlayProvisioningClient(Origin, new Handler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            cookie = request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
            uri = request.RequestUri?.AbsoluteUri; body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, $"{{\"v\":1,\"status\":\"provisioned\",\"overlayId\":\"{OverlayId}\"}}");
        }));
        HostedProvisioningResult result = await client.ProvisionAsync(state);
        Assert.Equal(HostedProvisioningResultKind.Provisioned, result.Kind);
        Assert.Equal(OverlayId, result.OverlayId);
        Assert.Equal($"{Origin}/api/v1/overlays", uri);
        Assert.Null(authorization); Assert.Null(cookie);
        Assert.Contains(state.RequestId, body); Assert.Contains(state.ReadVerifier, body); Assert.Contains(state.WriteVerifier, body);
        Assert.DoesNotContain(state.ReadCapability, body); Assert.DoesNotContain(state.WriteCapability, body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, HostedProvisioningResultKind.Retry)]
    [InlineData(HttpStatusCode.TooManyRequests, HostedProvisioningResultKind.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, HostedProvisioningResultKind.Retry)]
    [InlineData(HttpStatusCode.Forbidden, HostedProvisioningResultKind.Protocol)]
    [InlineData(HttpStatusCode.BadRequest, HostedProvisioningResultKind.Protocol)]
    public async Task ClientClassifiesBoundedResponses(HttpStatusCode status, HostedProvisioningResultKind expected)
    {
        using var client = new HostedOverlayProvisioningClient(Origin, new Handler(_ => Task.FromResult(Json(status, "{}"))));
        Assert.Equal(expected, (await client.ProvisionAsync(HostedProvisioningState.Create(Origin, [Origin]))).Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"v\":1,\"status\":\"provisioned\"}")]
    [InlineData("{\"v\":1,\"status\":\"provisioned\",\"overlayId\":\"bad\"}")]
    [InlineData("{\"v\":1,\"status\":\"provisioned\",\"overlayId\":\"a100000000000000000000000000000000000000000000000000000000000000\",\"extra\":true}")]
    public async Task ClientRejectsMalformedSuccess(string response)
    {
        using var client = new HostedOverlayProvisioningClient(Origin, new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, response))));
        Assert.Equal(HostedProvisioningResultKind.Protocol,
            (await client.ProvisionAsync(HostedProvisioningState.Create(Origin, [Origin]))).Kind);
    }

    [Fact]
    public void PreReleasePendingDecodesOnlyForBoundedLocalMigration()
    {
        string id = new('1', 32), read = new('2', 64), write = new('3', 64);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            origin = Origin,
            slotId = id,
            setupGrant = new string('4', 64),
            requestId = new string('5', 32),
            readCapability = read,
            writeCapability = write,
            readVerifier = HostedPublisherConfiguration.Verifier(id, "read", read),
            writeVerifier = HostedPublisherConfiguration.Verifier(id, "write", write),
            phase = "claiming",
            paused = false
        });
        HostedProvisioningState state = HostedProvisioningState.Decode(bytes, [Origin]);
        Assert.True(state.IsPreReleaseVersion1);
        Assert.Equal(HostedProvisioningPhase.Claiming, state.Phase);
        Assert.Equal(id, state.PriorConfiguration!.OverlayId);
        HostedProvisioningState acknowledged = state.Acknowledged(id);
        Assert.True(acknowledged.IsPreReleaseVersion1);
        Assert.Equal(1, acknowledged.Configuration([Origin]).Version);
        Assert.Equal(id, acknowledged.Configuration([Origin]).OverlayId);
        Assert.Equal(1, JsonDocument.Parse(state.Encode()).RootElement.GetProperty("version").GetInt32());
        Assert.Equal(HostedProvisioningPhase.Acknowledged,
            HostedProvisioningState.Decode(acknowledged.Encode(), [Origin]).Phase);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
