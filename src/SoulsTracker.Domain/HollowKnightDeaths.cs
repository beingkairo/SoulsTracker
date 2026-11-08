namespace SoulsTracker.Domain;

/// <summary>App-owned observed deaths across Hollow Knight saves, never a lifetime game total.</summary>
public sealed record HollowKnightDeaths
{
    public static HollowKnightDeaths Empty { get; } = new(0, null);

    public HollowKnightDeaths(long count, Guid? lastEventId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
        LastEventId = lastEventId;
    }

    public long Count { get; }
    public Guid? LastEventId { get; }

    public HollowKnightDeaths Record(Guid eventId)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("A death event requires an identity.", nameof(eventId));
        return LastEventId == eventId ? this : new(checked(Count + 1), eventId);
    }
}
