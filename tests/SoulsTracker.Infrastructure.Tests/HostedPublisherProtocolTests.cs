using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoulsTracker.Application;
using static SoulsTracker.Infrastructure.Tests.HostedPublisherTests;
using static SoulsTracker.Infrastructure.Tests.HostedPublisherRaceTests;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedPublisherProtocolTests
{
    [Theory]
    [InlineData(8192, HostedPublisherStatus.Ready)]
    [InlineData(8193, HostedPublisherStatus.InvalidProtocol)]
    public async Task ResponseByteLimitAppliesWithoutContentLength(int size, HostedPublisherStatus expected)
    {
        var server = new Server();
        server.AfterCommit = (action, response) =>
        {
            if (action != "state") return response;
            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json.PadRight(size));
            response.Content.Dispose();
            response.Content = new StreamContent(new NonSeekable(bytes));
            response.Content.Headers.ContentType = new("application/json");
            Assert.Null(response.Content.Headers.ContentLength);
            return response;
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        sender.Offer(Offer("4"));
        await WaitUntil(() => sender.Status == expected);
        Assert.Equal(1, server.Methods.Count(m => m == "PUT"));
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("generation")]
    [InlineData("sessionRequestId")]
    [InlineData("sequence")]
    [InlineData("digest")]
    [InlineData("untouched")]
    [InlineData("revision")]
    [InlineData("changed")]
    public async Task RejectsWellFormedButMismatchedAcknowledgements(string mismatch)
    {
        var server = new Server();
        server.AfterCommit = (action, response) =>
        {
            if (action != "state") return response;
            var json = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            if (mismatch == "digest") json["death"]!["digest"] = new string('f', 64);
            else if (mismatch == "untouched") json["appearance"]!["digest"] = new string('f', 64);
            else if (mismatch == "revision") json["death"]!["revision"] = "0";
            else if (mismatch == "changed") json["changed"] = new JsonArray();
            else json[mismatch] = mismatch == "sessionRequestId" ? new string('f', 32) : "42";
            response.Dispose();
            return Server.Reply(json);
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        sender.Offer(new("update", true, Offer("4").Death));
        await WaitUntil(() => sender.Status is HostedPublisherStatus.InvalidProtocol or HostedPublisherStatus.Ready);
        Assert.Equal(HostedPublisherStatus.InvalidProtocol, sender.Status);
    }

    [Fact]
    public async Task StartupMatchingDigestsSuppressAllStateWrites()
    {
        var offer = Offer("4");
        var server = new Server { DeathDigest = HostedPublisherProtocol.Digest(offer.Death), AppearanceDigest = HostedPublisherProtocol.Digest(offer.Appearance) };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        sender.Offer(offer);
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
        await sender.DisposeAsync();
        Assert.Equal(["GET", "POST"], server.Methods);
    }

    [Fact]
    public void DigestsMatchJavaScriptCanonicalEncodingAndInt64RemainsExact()
    {
        Assert.Equal("74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b", HostedPublisherProtocol.Digest(null));
        Assert.Equal("b20dc4425f41fd3996ed3f0ebd6ca52ff20510565bfafa188495b4277ffcd632", HostedPublisherProtocol.Digest(Offer("9007199254740993").Death));
        var appearance = Offer("0").Appearance! with { Title = "é😀\u2028\t\"&" };
        string canonical = HostedPublisherProtocol.Canonical(appearance);
        Assert.Contains("é😀\u2028\\t\\\"&", canonical);
        string body = HostedPublisherProtocol.State("9223372036854775807", new('a', 32), long.MaxValue, Offer("9223372036854775807").Death, appearance);
        Assert.Contains("\"sequence\":\"9223372036854775807\"", body);
        Assert.DoesNotContain("revision", body);
    }

    [Fact]
    public async Task ExhaustedEpochPausesWithoutAcquisition()
    {
        var server = new Server
        {
            Intercept = (_, _, _) => Task.FromResult<HttpResponseMessage?>(Server.Reply(new
            {
                v = 1,
                epoch = "9223372036854775807",
                generation = "0",
                death = Server.Channel(new('0', 64)),
                appearance = Server.Channel(new('0', 64)),
            }))
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        await WaitUntil(() => sender.Status == HostedPublisherStatus.CounterExhausted);
        await sender.DisposeAsync();
        Assert.Single(server.Methods);
    }

    [Fact]
    public async Task InvalidOfferedInt64OrOversizedAppearancePausesWithoutSendingIt()
    {
        foreach (var offer in new[] { Offer("9223372036854775808"), Offer("0") with { Appearance = Offer("0").Appearance! with { Title = new('a', 8193) } } })
        {
            var server = new Server();
            await using var sender = new HostedOverlayPublisher(Configuration(), server);
            Assert.False(sender.Offer(offer));
            Assert.Equal(HostedPublisherStatus.InvalidProtocol, sender.Status);
            await sender.DisposeAsync();
            Assert.DoesNotContain("PUT", server.Methods);
        }
    }
}
