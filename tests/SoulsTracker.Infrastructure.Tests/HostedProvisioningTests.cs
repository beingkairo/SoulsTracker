using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedProvisioningTests
{
    private const string Origin = "https://publisher.example.test";
    private const string Code = "st1.11111111111111111111111111111111.2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public async Task PendingStateIsProtectedBoundedAtomicAndPreservesExactRequestAcrossPause()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "pending.private");
        try
        {
            var store = new HostedProvisioningStateStore(path, new CurrentUserDpapiSecretProtector(), [Origin]);
            HostedProvisioningState state = HostedProvisioningState.Create(Origin, Code, [Origin]);
            byte[] original = state.Encode();
            await store.SavePendingAsync(state);
            string protectedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain("2222222222222222", protectedText);
            Assert.DoesNotContain(Origin, protectedText);
            HostedProvisioningState loaded = Assert.IsType<HostedProvisioningState>(await store.LoadPendingAsync());
            Assert.Equal(original, loaded.Encode());
            await store.SavePendingAsync(loaded.WithPaused(true));
            HostedProvisioningState paused = Assert.IsType<HostedProvisioningState>(await store.LoadPendingAsync());
            Assert.True(paused.Paused);
            Assert.Equal(loaded.Configuration.BuildReadUrl(), paused.Configuration.BuildReadUrl());
            Assert.Equal(loaded.RequestId, paused.RequestId);
            await store.RemovePendingAsync();
            Assert.Null(await store.LoadPendingAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("st1.short.value")]
    [InlineData("st2.11111111111111111111111111111111.2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData("st1.11111111111111111111111111111111.222222222222222222222222222222222222222222222222222222222222222A")]
    public void SetupCodeShapeIsStrict(string value)
    {
        Assert.False(HostedProvisioningState.IsSetupCodeShape(value));
        Assert.Throws<ArgumentException>(() => HostedProvisioningState.Create(Origin, value, [Origin]));
        Assert.True(HostedProvisioningState.IsSetupCodeShape(Code));
    }

    [Fact]
    public async Task CorruptOrUnapprovedPendingStateFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "pending.private");
        try
        {
            Directory.CreateDirectory(root);
            var approved = new HostedProvisioningStateStore(path, new CurrentUserDpapiSecretProtector(), [Origin]);
            await File.WriteAllTextAsync(path, "broken");
            await Assert.ThrowsAsync<InvalidOperationException>(() => approved.LoadPendingAsync());
            File.Delete(path);
            var unapproved = new HostedProvisioningStateStore(path, new CurrentUserDpapiSecretProtector(), ["https://other.example.test"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                unapproved.SavePendingAsync(HostedProvisioningState.Create(Origin, Code, [Origin])));
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClientSendsOnlyExactVerifiersAndAcceptsStrictAcknowledgement()
    {
        HostedProvisioningState state = HostedProvisioningState.Create(Origin, Code, [Origin]);
        string? authorization = null, body = null, uri = null;
        using var client = new HostedOverlayProvisioningClient(Origin, new Handler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            uri = request.RequestUri?.AbsoluteUri;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"v\":1,\"status\":\"provisioned\"}");
        }));
        HostedProvisioningResult result = await client.ProvisionAsync(state);
        Assert.Equal(HostedProvisioningResultKind.Provisioned, result.Kind);
        Assert.Equal($"{Origin}/api/v1/overlays/{state.SlotId}/provision", uri);
        Assert.Equal("Setup " + state.SetupGrant, authorization);
        Assert.Contains(state.RequestId, body);
        Assert.Contains(state.ReadVerifier, body);
        Assert.Contains(state.WriteVerifier, body);
        Assert.DoesNotContain(state.Configuration.ReadCapability, body);
        Assert.DoesNotContain(state.Configuration.WriteCapability, body);
        Assert.DoesNotContain(state.SetupGrant, body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, HostedProvisioningResultKind.Denied)]
    [InlineData(HttpStatusCode.Conflict, HostedProvisioningResultKind.Used)]
    [InlineData(HttpStatusCode.TooManyRequests, HostedProvisioningResultKind.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, HostedProvisioningResultKind.Retry)]
    [InlineData(HttpStatusCode.BadRequest, HostedProvisioningResultKind.Protocol)]
    public async Task ClientClassifiesBoundedResponses(HttpStatusCode status, HostedProvisioningResultKind expected)
    {
        using var client = new HostedOverlayProvisioningClient(Origin, new Handler(_ => Task.FromResult(Json(status, "{}"))));
        Assert.Equal(expected, (await client.ProvisionAsync(HostedProvisioningState.Create(Origin, Code, [Origin]))).Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"x\":1,\"y\":\"provisioned\"}")]
    [InlineData("{\"v\":\"1\",\"status\":\"provisioned\"}")]
    [InlineData("{\"v\":1,\"status\":1}")]
    [InlineData("{\"v\":1,\"status\":\"wrong\"}")]
    [InlineData("{\"v\":1,\"status\":\"provisioned\",\"extra\":true}")]
    public async Task ClientRejectsMalformedSuccessWithoutExposingResponse(string response)
    {
        using var client = new HostedOverlayProvisioningClient(Origin, new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, response))));
        Assert.Equal(HostedProvisioningResultKind.Protocol,
            (await client.ProvisionAsync(HostedProvisioningState.Create(Origin, Code, [Origin]))).Kind);
    }

    [Fact]
    public async Task ClientTreatsLostResponseAsRetry()
    {
        using var client = new HostedOverlayProvisioningClient(Origin, new Handler(_ => throw new HttpRequestException("private")));
        Assert.Equal(HostedProvisioningResultKind.Retry,
            (await client.ProvisionAsync(HostedProvisioningState.Create(Origin, Code, [Origin]))).Kind);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
