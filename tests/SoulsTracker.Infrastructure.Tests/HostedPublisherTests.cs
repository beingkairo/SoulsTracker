using System.Net;
using System.Text;
using System.Text.Json;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedPublisherTests
{
    internal static HostedPublisherConfiguration Configuration() => HostedPublisherConfiguration.Create(
        "https://publisher.example.test", new('a', 32), new('b', 64), new('c', 64), ["https://publisher.example.test"]);
    internal static HostedOverlayEnvelope Offer(string value) => new("update", true,
        new HostedDeath { Revision = "0", Value = value, Availability = "available" },
        HostedAppearance.From(OverlayPresentationConfiguration.From(OverlayConfiguration.Default), "0"));

    [Fact]
    public async Task AcquiresOnceAndPublishesExactIndependentChannelsWithoutRevisions()
    {
        var handler = new Server();
        await using var sender = new HostedOverlayPublisher(Configuration(), handler);
        Assert.True(sender.Offer(Offer("9007199254740993")));
        await handler.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sender.DisposeAsync();
        Assert.Equal(["GET", "POST", "PUT"], handler.Methods);
        using var body = JsonDocument.Parse(handler.Bodies.Last());
        Assert.Equal("9007199254740993", body.RootElement.GetProperty("death").GetProperty("value").GetString());
        Assert.False(body.RootElement.GetProperty("death").TryGetProperty("revision", out _));
        Assert.False(body.RootElement.GetProperty("appearance").TryGetProperty("revision", out _));
        Assert.Equal(1, handler.MaximumConcurrent);
        Assert.False(sender.Offer(Offer("4")));
    }

    internal sealed class Server : HttpMessageHandler
    {
        internal readonly List<string> Methods = [];
        internal readonly List<string> Bodies = [];
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int MaximumConcurrent;
        private int concurrent;
        private string session = "";
        internal Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }
        internal Func<string, HttpResponseMessage, HttpResponseMessage>? AfterCommit { get; set; }
        internal string DeathDigest = HostedPublisherProtocol.Digest(null);
        internal string AppearanceDigest = new('0', 64);
        private long deathRevision, appearanceRevision;
        private string? lastBody;
        private object? lastAck;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref concurrent));
            try
            {
                string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                Methods.Add(request.Method.Method);
                Bodies.Add(body);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(new string('c', 64), request.Headers.Authorization?.Parameter);
                Assert.Empty(request.RequestUri!.Query);
                Assert.Empty(request.RequestUri.Fragment);
                if (Intercept is not null && await Intercept(request, body, cancellationToken) is { } intercepted) return intercepted;
                if (request.Method == HttpMethod.Get) return Reply(new { v = 1, epoch = "0", generation = "0", death = Channel(DeathDigest, deathRevision), appearance = Channel(AppearanceDigest, appearanceRevision) });
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                session = root.GetProperty("sessionRequestId").GetString()!;
                if (request.Method == HttpMethod.Post)
                {
                    var acquired = Reply(new { v = 1, epoch = "1", generation = "0", sessionRequestId = session, death = Channel(DeathDigest, deathRevision), appearance = Channel(AppearanceDigest, appearanceRevision) });
                    return AfterCommit?.Invoke("session", acquired) ?? acquired;
                }
                if (body == lastBody)
                {
                    var replay = Reply(lastAck!);
                    return AfterCommit?.Invoke("state", replay) ?? replay;
                }
                string previousDeath = DeathDigest, previousAppearance = AppearanceDigest;
                if (root.TryGetProperty("death", out var death)) DeathDigest = HostedPublisherProtocol.Digest(HostedOverlayJson.Parse("{\"v\":1,\"type\":\"update\",\"death\":{\"revision\":\"0\"," + death.GetRawText()[1..] + "}").Death);
                if (root.TryGetProperty("appearance", out var appearance)) AppearanceDigest = HostedPublisherProtocol.Digest(HostedOverlayJson.Parse("{\"v\":1,\"type\":\"update\",\"appearance\":{\"revision\":\"0\"," + appearance.GetRawText()[1..] + "}").Appearance);
                List<string> changed = [];
                if (previousDeath != DeathDigest) { deathRevision++; changed.Add("death"); }
                if (previousAppearance != AppearanceDigest) { appearanceRevision++; changed.Add("appearance"); }
                lastBody = body;
                lastAck = new { v = 1, epoch = "1", generation = "0", sessionRequestId = session, sequence = root.GetProperty("sequence").GetString(), changed, death = Channel(DeathDigest, deathRevision), appearance = Channel(AppearanceDigest, appearanceRevision) };
                var response = Reply(lastAck);
                Written.TrySetResult();
                return AfterCommit?.Invoke("state", response) ?? response;
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        internal static object Channel(string digest, long revision = 0) => new { revision = revision.ToString(System.Globalization.CultureInfo.InvariantCulture), digest };
        internal static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
