namespace SoulsTracker.Domain;

/// <summary>One publication-boundary result shared by all Total Deaths consumers.</summary>
public sealed record EffectiveDeathTotalResult
{
    public required string SourceIdentity { get; init; }
    public required EffectiveDeathTotalStatus Status { get; init; }
    public long? SavedOrManualBase { get; init; }
    public long? GameSpecificAdjustment { get; init; }
    public long? EffectiveDisplayedTotal { get; init; }

    public static EffectiveDeathTotalResult Unavailable(GameId gameId, string? sourceIdentity = null) => new()
    {
        SourceIdentity = sourceIdentity ?? gameId.Value,
        Status = EffectiveDeathTotalStatus.Unavailable
    };
}

public enum EffectiveDeathTotalStatus
{
    Synced,
    Zero,
    Unavailable,
    Unreadable,
    Waiting,
    SourceMismatch
}
