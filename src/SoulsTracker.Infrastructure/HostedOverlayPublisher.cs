using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SoulsTracker.Application;

namespace SoulsTracker.Infrastructure;

public enum HostedPublisherStatus { Starting, Sending, Ready, Retrying, CredentialsRequired, Conflict, InvalidProtocol, CounterExhausted, Stopped }

/// <summary>One owned sender with two bounded immutable latest-value slots.</summary>
public sealed class HostedOverlayPublisher : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly HttpClient client;
    private readonly HostedPublisherConfiguration configuration;
    private readonly TimeProvider clock;
    private readonly Func<double> random;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;
    private Task? disposal;
    private bool closing;
    private HostedDeath? death;
    private HostedAppearance? appearance;
    private long deathGeneration, appearanceGeneration, deathAcknowledged, appearanceAcknowledged;
    private HostedPublisherStatus status = HostedPublisherStatus.Starting;
    public HostedPublisherStatus Status { get { lock (gate) return status; } }
    public event EventHandler? StatusChanged;

    private void NotifyStatus()
    {
        // Never call subscribers under the sender lock or let UI errors stop delivery.
        try { StatusChanged?.Invoke(this, EventArgs.Empty); }
        catch { }
    }

    public HostedOverlayPublisher(HostedPublisherConfiguration configuration)
        : this(configuration, CreateHandler()) { }

    internal HostedOverlayPublisher(HostedPublisherConfiguration configuration, HttpMessageHandler handler,
        TimeProvider? clock = null, Func<double>? random = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.configuration = configuration;
        this.clock = clock ?? TimeProvider.System;
        this.random = random ?? Random.Shared.NextDouble;
        this.delay = delay ?? ((duration, ct) => Task.Delay(duration, this.clock, ct));
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        worker = Task.Run(RunAsync);
    }

    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public bool Offer(HostedOverlayEnvelope offer)
    {
        try { return OfferCore(offer); }
        finally { NotifyStatus(); }
    }

    private bool OfferCore(HostedOverlayEnvelope offer)
    {
        lock (gate)
        {
            if (closing || IsPaused(status)) return false;
            try
            {
                var validated = HostedOverlayJson.Parse(HostedOverlayJson.Serialize(offer));
                if (validated.Death is { } d && (d with { Revision = "0" }) != death)
                {
                    death = d with { Revision = "0" };
                    deathGeneration = checked(deathGeneration + 1);
                }
                if (validated.Appearance is { } a && (a with { Revision = "0" }) != appearance)
                {
                    appearance = a with { Revision = "0" };
                    appearanceGeneration = checked(appearanceGeneration + 1);
                }
                if (status == HostedPublisherStatus.Ready &&
                    (deathGeneration != deathAcknowledged || appearanceGeneration != appearanceAcknowledged))
                    status = HostedPublisherStatus.Sending;
                Signal();
                return true;
            }
            catch (OverflowException) { status = HostedPublisherStatus.CounterExhausted; }
            catch (JsonException) { status = HostedPublisherStatus.InvalidProtocol; }
            Signal();
            return false;
        }
    }

    private static bool IsPaused(HostedPublisherStatus value) => value is HostedPublisherStatus.CredentialsRequired or
        HostedPublisherStatus.Conflict or HostedPublisherStatus.InvalidProtocol or HostedPublisherStatus.CounterExhausted or HostedPublisherStatus.Stopped;
    private void Signal() { if (wake.CurrentCount == 0) wake.Release(); }
    private void SetStatus(HostedPublisherStatus value)
    {
        lock (gate) { if (!IsPaused(status)) status = value; }
        NotifyStatus();
    }

    private async Task RunAsync()
    {
        try
        {
            var initial = await InitializeRequestAsync("publisher", null).ConfigureAwait(false);
            string session = Guid.NewGuid().ToString("N");
            string expected = checked(long.Parse(initial.Epoch, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
            string acquisition = HostedPublisherProtocol.Canonical(new { v = 1, expectedEpoch = initial.Epoch, sessionRequestId = session });
            var current = await InitializeRequestAsync("session", acquisition).ConfigureAwait(false);
            if (current.Epoch != expected || current.Session != session || current.Generation != initial.Generation) throw new JsonException();
            long sequence = 0;
            Candidate? retry = null;
            bool uncertainDeath = false, uncertainAppearance = false;
            int failures = 0;
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                Candidate? candidate;
                lock (gate)
                {
                    if (IsPaused(status)) return;
                    if (!uncertainDeath && death is not null && HostedPublisherProtocol.Digest(death) == current.Death.Digest) deathAcknowledged = deathGeneration;
                    if (!uncertainAppearance && appearance is not null && HostedPublisherProtocol.Digest(appearance) == current.Appearance.Digest) appearanceAcknowledged = appearanceGeneration;
                    var d = deathGeneration != deathAcknowledged ? death : null;
                    var a = appearanceGeneration != appearanceAcknowledged ? appearance : null;
                    candidate = d is null && a is null ? null : retry is not null && retry.Death == d && retry.Appearance == a
                        ? retry : new Candidate(d, a, deathGeneration, appearanceGeneration,
                            HostedPublisherProtocol.State(current.Epoch, session, checked(++sequence), d, a), sequence);
                    if (candidate is null && closing) return;
                    status = candidate is null ? HostedPublisherStatus.Ready : HostedPublisherStatus.Sending;
                }
                NotifyStatus();
                if (candidate is null) { await wake.WaitAsync(stop.Token).ConfigureAwait(false); continue; }
                bool deathWasUncertain = uncertainDeath, appearanceWasUncertain = uncertainAppearance;
                uncertainDeath |= candidate.Death is not null;
                uncertainAppearance |= candidate.Appearance is not null;
                try
                {
                    var ack = await SendAsync("state", candidate.Body).ConfigureAwait(false);
                    if (ack.Epoch != current.Epoch || ack.Generation != current.Generation || ack.Session != session ||
                        ack.Sequence != candidate.Sequence.ToString(CultureInfo.InvariantCulture) ||
                        (candidate.Death is not null && ack.Death.Digest != HostedPublisherProtocol.Digest(candidate.Death)) ||
                        (candidate.Appearance is not null && ack.Appearance.Digest != HostedPublisherProtocol.Digest(candidate.Appearance))) throw new JsonException();
                    ValidateChannelAck(current.Death, ack.Death, candidate.Death is not null, ack.Changed.Contains("death"), deathWasUncertain);
                    ValidateChannelAck(current.Appearance, ack.Appearance, candidate.Appearance is not null, ack.Changed.Contains("appearance"), appearanceWasUncertain);
                    lock (gate)
                    {
                        if (candidate.Death is not null) deathAcknowledged = candidate.DeathGeneration;
                        if (candidate.Appearance is not null) appearanceAcknowledged = candidate.AppearanceGeneration;
                    }
                    current = ack;
                    uncertainDeath = uncertainAppearance = false;
                    retry = null;
                    failures = 0;
                }
                catch (TransientFailure failure)
                {
                    ThrowIfPaused();
                    retry = candidate;
                    SetStatus(HostedPublisherStatus.Retrying);
                    await delay(RetryDelay(failures++, failure.RetryAfter), stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (PauseFailure failure) { SetStatus(failure.Status); }
        catch (OverflowException) { SetStatus(HostedPublisherStatus.CounterExhausted); }
        catch { SetStatus(HostedPublisherStatus.InvalidProtocol); }
    }

    private sealed record Candidate(HostedDeath? Death, HostedAppearance? Appearance, long DeathGeneration, long AppearanceGeneration, string Body, long Sequence);
    private sealed class PauseFailure(HostedPublisherStatus status) : Exception { internal HostedPublisherStatus Status { get; } = status; }
    private sealed class TransientFailure(TimeSpan? retryAfter = null) : Exception { internal TimeSpan? RetryAfter { get; } = retryAfter; }

    private void ThrowIfPaused()
    {
        stop.Token.ThrowIfCancellationRequested();
        lock (gate) if (IsPaused(status)) throw new PauseFailure(status);
    }

    private static void ValidateChannelAck(HostedPublisherProtocol.Channel previous, HostedPublisherProtocol.Channel acknowledged,
        bool included, bool changed, bool uncertain)
    {
        long before = long.Parse(previous.Revision, CultureInfo.InvariantCulture);
        long after = long.Parse(acknowledged.Revision, CultureInfo.InvariantCulture);
        bool different = previous.Digest != acknowledged.Digest;
        if (after < before || ((different || changed) && after == before) ||
            (!included && (previous != acknowledged || changed))) throw new JsonException();
        // A lost response may have committed intermediate content. Its revision and
        // changed-list cannot be reconstructed from the last acknowledged snapshot.
        if (included && !uncertain && (changed != different || after != checked(before + (different ? 1 : 0))))
            throw new JsonException();
    }

    private async Task<HostedPublisherProtocol.Ack> InitializeRequestAsync(string action, string? body)
    {
        int failures = 0;
        for (; ; )
        {
            try { return await SendAsync(action, body).ConfigureAwait(false); }
            catch (TransientFailure failure)
            {
                ThrowIfPaused();
                SetStatus(HostedPublisherStatus.Retrying);
                await delay(RetryDelay(failures++, failure.RetryAfter), stop.Token).ConfigureAwait(false);
            }
        }
    }

    internal TimeSpan RetryDelay(int failures, TimeSpan? retryAfter) => TimeSpan.FromSeconds(Math.Min(60,
        Math.Max(Math.Clamp(random(), 0, 1) * Math.Min(60, Math.Pow(2, Math.Min(failures, 6))), retryAfter?.TotalSeconds ?? 0)));

    private async Task<HostedPublisherProtocol.Ack> SendAsync(string action, string? body)
    {
        ThrowIfPaused();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, timeout.Token);
        var ct = linked.Token;
        try
        {
            using var request = new HttpRequestMessage(action == "publisher" ? HttpMethod.Get : action == "session" ? HttpMethod.Post : HttpMethod.Put,
                $"{configuration.Origin}/api/v1/overlays/{configuration.OverlayId}/{action}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.WriteCapability);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            if (body is not null)
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is 408 or 429 or >= 500 and <= 599)
            {
                var after = response.Headers.RetryAfter;
                throw new TransientFailure(after?.Delta ?? (after?.Date - clock.GetUtcNow()));
            }
            if (code is 401 or 403) throw new PauseFailure(HostedPublisherStatus.CredentialsRequired);
            if (code == 409) throw new PauseFailure(HostedPublisherStatus.Conflict);
            if (code != 200) throw new PauseFailure(HostedPublisherStatus.InvalidProtocol);
            if (response.Content.Headers.ContentLength > HostedOverlayJson.MaximumBytes ||
                response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentEncoding.Count != 0) throw new JsonException();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] bytes = new byte[HostedOverlayJson.MaximumBytes + 1];
            int used = 0;
            while (used < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(used), ct).ConfigureAwait(false);
                if (read == 0) break;
                used += read;
            }
            if (used > HostedOverlayJson.MaximumBytes) throw new JsonException();
            return HostedPublisherProtocol.Parse(bytes[..used], action);
        }
        catch (HttpRequestException) { throw new TransientFailure(); }
        catch (IOException) { throw new TransientFailure(); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { throw new TransientFailure(); }
    }

    /// <summary>Owner must stop/drain producers first. No synthetic closing publication.</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is null)
            {
                closing = true;
                Signal();
                disposal = FinishAsync();
            }
            return new(disposal);
        }
    }

    private async Task FinishAsync()
    {
        using var flush = new CancellationTokenSource();
        var deadline = Task.Delay(TimeSpan.FromSeconds(2), clock, flush.Token);
        await Task.WhenAny(worker, deadline).ConfigureAwait(false);
        await flush.CancelAsync().ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        client.Dispose();
        stop.Dispose();
        wake.Dispose();
        lock (gate) status = HostedPublisherStatus.Stopped;
        NotifyStatus();
    }
}
