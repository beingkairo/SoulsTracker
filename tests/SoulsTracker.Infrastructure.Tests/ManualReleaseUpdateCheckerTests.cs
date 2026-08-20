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
    public async Task CheckerMapsOfflineFailureToSafeUnavailableState()
    {
        using var client = new HttpClient(new ThrowingHandler());
        Assert.Equal(ManualReleaseUpdateStatus.Unavailable, (await new GitHubLatestReleaseUpdateChecker(client).CheckAsync("1.3.0")).Status);
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
