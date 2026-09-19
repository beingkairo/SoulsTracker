using System.Net;
using System.Text;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class ManualReleaseUpdateCheckerTests
{
    [Theory]
    [InlineData("v1.4.0", "1.3.0", true)]
    [InlineData("1.3.0", "1.3.0", false)]
    [InlineData("1.3.0-beta.2", "1.3.0-beta.1", true)]
    [InlineData("1.3.0-beta.1", "1.3.0", false)]
    public void SemanticVersionsAcceptLeadingVAndCompareSemantically(string released, string installed, bool newer)
    {
        Assert.True(ReleaseSemanticVersion.TryParse(released, out ReleaseSemanticVersion? release));
        Assert.True(ReleaseSemanticVersion.TryParse(installed, out ReleaseSemanticVersion? current));
        Assert.Equal(newer, release!.CompareTo(current) > 0);
    }

    [Theory]
    [InlineData("1.3", false)]
    [InlineData("v01.3.0", false)]
    [InlineData("v1.3.0-", false)]
    [InlineData("v1.3.0", true)]
    public void SemanticVersionParsingFailsClosed(string value, bool expected) => Assert.Equal(expected, ReleaseSemanticVersion.TryParse(value, out _));

    [Fact]
    public async Task CheckerReportsUpdateFromOfficialResponseAndUsesHttpsReleasePage()
    {
        using var client = new HttpClient(new FakeHandler(HttpStatusCode.OK, "{\"tag_name\":\"v1.4.0\",\"html_url\":\"https://github.com/beingkairo/SoulsTracker/releases/tag/v1.4.0\"}"));
        ManualReleaseUpdateResult result = await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0");
        Assert.Equal(ManualReleaseUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.4.0", result.AvailableVersion);
        Assert.Equal(Uri.UriSchemeHttps, result.ReleasePage!.Scheme);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"v1.3.0\"}", ManualReleaseUpdateStatus.UpToDate)]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"not-a-version\"}", ManualReleaseUpdateStatus.InvalidResponse)]
    [InlineData(HttpStatusCode.TooManyRequests, "", ManualReleaseUpdateStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, "", ManualReleaseUpdateStatus.Unavailable)]
    public async Task CheckerMapsResponseStatesWithoutDiagnostics(HttpStatusCode code, string body, ManualReleaseUpdateStatus expected)
    {
        using var client = new HttpClient(new FakeHandler(code, body));
        Assert.Equal(expected, (await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0")).Status);
    }

    [Fact]
    public async Task CheckerReturnsTheConfirmedLatestVersionWhenInstalledVersionIsCurrent()
    {
        using var client = new HttpClient(new FakeHandler(HttpStatusCode.OK, "{\"tag_name\":\"v1.3.0\"}"));
        ManualReleaseUpdateResult result = await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0");
        Assert.Equal(ManualReleaseUpdateStatus.UpToDate, result.Status);
        Assert.Equal("1.3.0", result.AvailableVersion);
    }

    [Fact]
    public async Task CheckerMapsOfflineFailureToSafeUnavailableState()
    {
        using var client = new HttpClient(new ThrowingHandler());
        Assert.Equal(ManualReleaseUpdateStatus.Unavailable, (await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0")).Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"release\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":null}")]
    [InlineData("{\"tag_name\":{}}")]
    [InlineData("{\"tag_name\":[]}")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("{\"tag_name\":true}")]
    [InlineData("{\"tag_name\":false}")]
    [InlineData("{\"tag_name\":\"invalid\"}")]
    [InlineData("{\"tag_name\":\"\"}")]
    [InlineData("{\"tag_name\":")]
    public async Task CheckerRejectsMalformedReleaseWithoutVersionOrPage(string body)
    {
        using var client = new HttpClient(new FakeHandler(HttpStatusCode.OK, body));
        ManualReleaseUpdateResult result = await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0");
        Assert.Equal(new ManualReleaseUpdateResult(ManualReleaseUpdateStatus.InvalidResponse), result);
    }

    public static TheoryData<string, string?, string?> ReleasePageCases
    {
        get
        {
            var cases = new TheoryData<string, string?, string?>();
            foreach (string tag in new[] { "v1.4.0", "v1.3.0", "v1.2.0" })
            {
                foreach (string? url in new[] { null, "null", "{}", "[]", "42", "true", "false", "\"\"", "\"not a URL\"", "\"/releases\"", "\"http://github.com/beingkairo/SoulsTracker/releases\"", "\"https://example.com/releases\"", "\"https://github.com.example.com/releases\"" })
                    cases.Add(tag, url, null);
                foreach (string url in new[] { "https://github.com/beingkairo/SoulsTracker/releases/tag/v1.4.0", "https://github.com/another/project/releases", "https://github.com:8443/another/project" })
                    cases.Add(tag, $"\"{url}\"", url);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ReleasePageCases))]
    public async Task CheckerPreservesVersionComparisonAndOptionalReleasePagePolicy(string tag, string? urlJson, string? acceptedUrl)
    {
        string body = $"{{\"tag_name\":\"{tag}\"" + (urlJson is null ? "}" : $",\"html_url\":{urlJson}}}");
        using var client = new HttpClient(new FakeHandler(HttpStatusCode.OK, body));
        ManualReleaseUpdateResult result = await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0");
        bool newer = tag == "v1.4.0";
        Assert.Equal(new ManualReleaseUpdateResult(
            newer ? ManualReleaseUpdateStatus.UpdateAvailable : ManualReleaseUpdateStatus.UpToDate,
            tag[1..],
            newer ? new Uri(acceptedUrl ?? "https://github.com/beingkairo/SoulsTracker/releases") : null), result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ManualReleaseUpdateStatus.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, ManualReleaseUpdateStatus.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, ManualReleaseUpdateStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ManualReleaseUpdateStatus.Unavailable)]
    public async Task CheckerReturnsNoVersionOrPageForHttpFailures(HttpStatusCode code, ManualReleaseUpdateStatus expected)
    {
        using var client = new HttpClient(new FakeHandler(code, "not JSON"));
        Assert.Equal(new ManualReleaseUpdateResult(expected), await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0"));
    }

    [Fact]
    public async Task CheckerMakesNoRequestForInvalidInstalledVersion()
    {
        using var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        Assert.Equal(new ManualReleaseUpdateResult(ManualReleaseUpdateStatus.InvalidInstalledVersion), await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("invalid"));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CheckerUsesOneExplicitRequestToFixedPublicEndpoint()
    {
        using var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var checker = new GitHubLatestReleaseUpdateChecker(client);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(new ManualReleaseUpdateResult(ManualReleaseUpdateStatus.UpToDate, "1.3.0"), await checker.CheckAsync("1.3.0"));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CheckerPropagatesCallerCancellation()
    {
        using var handler = new WaitingHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancellation = new CancellationTokenSource();
        Task<ManualReleaseUpdateResult> check = new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0", cancellation.Token).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task CheckerMapsItsBoundedTimeoutToUnavailable()
    {
        using var handler = new WaitingHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Task<ManualReleaseUpdateResult> check = new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0").AsTask();
        Assert.Equal(new ManualReleaseUpdateResult(ManualReleaseUpdateStatus.Unavailable), await check.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task CheckerReturnsNoVersionOrPageWhenOffline()
    {
        using var client = new HttpClient(new ThrowingHandler());
        Assert.Equal(new ManualReleaseUpdateResult(ManualReleaseUpdateStatus.Unavailable), await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0"));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri("https://api.github.com/repos/beingkairo/SoulsTracker/releases/latest"), request.RequestUri);
            Assert.Equal("SoulsTracker-manual-update-check", request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"v1.3.0\"}") });
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request must be cancelled.");
        }
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }
}
