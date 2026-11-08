namespace SoulsTracker.Domain;

/// <summary>One publication-boundary result shared by all Total Deaths consumers.</summary>
public sealed record EffectiveDeathTotalResult
{
    public required string SourceIdentity { get; init; }
    public required EffectiveDeathTotalStatus Status { get; init; }
    public long? SavedOrManualBase { get; init; }
    public long? GameSpecificAdjustment { get; init; }
    public long? EffectiveDisplayedTotal { get; init; }

    public static EffectiveDeathTotalResult Resolve(PersistentTrackerState state, RuntimeGameObservation? observation, PersistentPluginObservation? plugin = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        string source = SourceIdentityFor(state);
        if (state.SelectedGameId == GameId.HollowKnight)
        {
            if (plugin is null) return Unavailable(state.SelectedGameId, source);
            if (plugin.SourceIdentity != source || state.HollowKnight.InstallationId is null ||
                plugin.RunId == Guid.Empty || plugin.Slot is < 1 or > 4 || plugin.Total < 0 || plugin.Revision < 1)
                return new() { SourceIdentity = source, Status = EffectiveDeathTotalStatus.SourceMismatch };
            return new()
            {
                SourceIdentity = source,
                Status = plugin.Total == 0 ? EffectiveDeathTotalStatus.Zero : EffectiveDeathTotalStatus.Synced,
                SavedOrManualBase = plugin.Total,
                GameSpecificAdjustment = 0,
                EffectiveDisplayedTotal = plugin.Total
            };
        }
        if (observation is null) return Unavailable(state.SelectedGameId, source);
        if ((state.SelectedGameId == GameId.BlackMythWukong || state.SelectedGameId == GameId.LiesOfP) && string.IsNullOrWhiteSpace(observation.SourceIdentity))
            return new() { SourceIdentity = source, Status = EffectiveDeathTotalStatus.SourceMismatch };
        if (string.IsNullOrWhiteSpace(observation.SourceIdentity) ||
            !string.Equals(source, observation.SourceIdentity, StringComparison.Ordinal))
            return new() { SourceIdentity = source, Status = EffectiveDeathTotalStatus.SourceMismatch };

        long baseValue = observation.TotalDeaths.Value;
        long adjustment = state.SelectedGameId == GameId.EldenRing
            ? state.EldenRingMissedDeathAdjustments.Get(state.EldenRingSave)
            : 0;
        return new()
        {
            SourceIdentity = source,
            Status = baseValue == 0 && adjustment == 0 ? EffectiveDeathTotalStatus.Zero : EffectiveDeathTotalStatus.Synced,
            SavedOrManualBase = baseValue,
            GameSpecificAdjustment = adjustment,
            EffectiveDisplayedTotal = checked(baseValue + adjustment)
        };
    }

    public static string SourceIdentityFor(PersistentTrackerState state) =>
        state.SelectedGameId == GameId.EldenRing
            ? $"{state.SelectedGameId.Value}:{state.EldenRingSave.LocalPath}:{state.EldenRingSave.SlotIndex}"
            : state.SelectedGameId == GameId.BlackMythWukong
                ? $"{state.SelectedGameId.Value}:{state.BlackMythWukongSave.LocalPath}"
                : state.SelectedGameId == GameId.LiesOfP
                    ? $"{state.SelectedGameId.Value}:{state.LiesOfPSave.LocalPath}"
                    : state.SelectedGameId == GameId.HollowKnight
                        ? HollowKnightSourceIdentityFor(state.HollowKnight)
                        : state.SelectedGameId.Value;

    public static string HollowKnightSourceIdentityFor(HollowKnightConfiguration configuration) =>
        $"{GameId.HollowKnight.Value}:{configuration.GameDirectory}:{configuration.InstallationId}";

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
