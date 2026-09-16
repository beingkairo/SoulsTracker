using System.Net;
using static SoulsTracker.Infrastructure.Tests.HostedPublisherTests;
using static SoulsTracker.Infrastructure.Tests.HostedPublisherRaceTests;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedPublisherLifetimeTests
{
    [Fact]
    public async Task StatusNotificationsNeverRunUnderSenderLockEvenAfterWorkerStops()
    {
        var server = new Server { Intercept = (_, _, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Conflict)) };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Conflict);
        // Join the actual owned worker so disposal takes its synchronous-completion path.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        await (Task)typeof(HostedOverlayPublisher).GetField("worker", flags)!.GetValue(sender)!;
        object gate = typeof(HostedOverlayPublisher).GetField("gate", flags)!.GetValue(sender)!;
        bool notified = false, underLock = false;
        sender.StatusChanged += (_, _) => { notified = true; underLock |= Monitor.IsEntered(gate); };
        await sender.DisposeAsync();
        Assert.True(notified);
        Assert.False(underLock);
    }

    [Fact]
    public async Task TenSecondTimeoutCancelsRequestBeforeRetryAndDisposalCancelsDelay()
    {
        var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server
        {
            Intercept = async (_, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.TrySetResult(); }
            return null;
        }
        };
        var sender = new HostedOverlayPublisher(Configuration(), server, clock, () => 0.5, async (_, ct) =>
        {
            delaying.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(cancelled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delaying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposed = sender.DisposeAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));
        await disposed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostedPublisherStatus.Stopped, sender.Status);
        Assert.Single(server.Methods);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task HttpDateRetryAfterUsesInjectedClockAndJitterCapsAtSixtySeconds()
    {
        var clock = new Clock();
        var delays = new List<TimeSpan>();
        var server = new Server();
        int calls = 0;
        server.Intercept = (_, _, _) =>
        {
            if (++calls != 1) return Task.FromResult<HttpResponseMessage?>(null);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(clock.GetUtcNow().AddSeconds(17));
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server, clock, () => 0.5,
            (duration, _) => { delays.Add(duration); return Task.CompletedTask; });
        sender.Offer(Offer("1"));
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
        Assert.Equal(TimeSpan.FromSeconds(17), Assert.Single(delays));
        Assert.Equal(TimeSpan.FromMilliseconds(500), sender.RetryDelay(0, null));
        Assert.Equal(TimeSpan.FromSeconds(30), sender.RetryDelay(int.MaxValue, null));
        Assert.Equal(TimeSpan.FromSeconds(60), sender.RetryDelay(0, TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromMilliseconds(500), sender.RetryDelay(0, TimeSpan.FromSeconds(-1)));
    }

    internal sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private TimeSpan elapsed;
        public override DateTimeOffset GetUtcNow() { lock (gate) return DateTimeOffset.UnixEpoch + elapsed; }
        internal int ActiveTimers { get { lock (gate) return timers.Count(t => !t.Disposed && t.Due != Timeout.InfiniteTimeSpan); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                var timer = new Timer(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(TimeSpan time)
        {
            List<Timer> due;
            lock (gate)
            {
                elapsed += time;
                due = timers.Where(t => !t.Disposed && t.Due != Timeout.InfiniteTimeSpan && t.Due <= elapsed).ToList();
                foreach (var timer in due) timer.Due = Timeout.InfiniteTimeSpan;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal TimeSpan Due;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? dueTime : owner.elapsed + dueTime;
                    return true;
                }
            }
            public void Dispose() { lock (owner.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
