namespace SoulsTracker.Desktop.Tests;

/// <summary>One-shot clock for update presentation tests; exposes timer disposal without wall-clock waits.</summary>
internal sealed class UpdatePresentationClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<PresentationTimer> timers = [];
    private TimeSpan elapsed;
    internal int UndisposedTimers { get { lock (gate) return timers.Count(t => !t.Disposed); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, period);
        lock (gate)
        {
            var timer = new PresentationTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    internal void Advance(int milliseconds)
    {
        List<PresentationTimer> due;
        lock (gate)
        {
            elapsed += TimeSpan.FromMilliseconds(milliseconds);
            due = timers.Where(t => !t.Disposed && t.Due != Timeout.InfiniteTimeSpan && t.Due <= elapsed).ToList();
            foreach (var timer in due) timer.Due = Timeout.InfiniteTimeSpan;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }

    private sealed class PresentationTimer(UpdatePresentationClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal TimeSpan Due { get; set; }
        internal bool Disposed { get; private set; }
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
