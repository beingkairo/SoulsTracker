namespace SoulsTracker.Domain;

/// <summary>
/// Holds streamer-confirmed Elden Ring deaths which are not included in the
/// game's saved lifetime total. Values are local, non-negative, and isolated by
/// canonical save path and character slot.
/// </summary>
public sealed class EldenRingMissedDeathAdjustments
{
    private readonly Dictionary<EldenRingMissedDeathAdjustmentKey, long> values;

    public static EldenRingMissedDeathAdjustments Empty { get; } = new([]);

    public EldenRingMissedDeathAdjustments(IEnumerable<EldenRingMissedDeathAdjustment> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var collected = new Dictionary<EldenRingMissedDeathAdjustmentKey, long>();
        foreach (EldenRingMissedDeathAdjustment entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Value < 0) throw new ArgumentOutOfRangeException(nameof(entries), "A missed-death adjustment cannot be negative.");
            if (entry.Value == 0) continue;
            collected.Add(EldenRingMissedDeathAdjustmentKey.Create(entry.LocalSavePath, entry.SlotIndex), entry.Value);
        }

        values = collected;
    }

    public long Get(EldenRingSaveConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !IsConfiguredCharacter(configuration)
            ? 0
            : values.TryGetValue(EldenRingMissedDeathAdjustmentKey.Create(configuration.LocalPath!, configuration.SlotIndex), out long value)
                ? value
                : 0;
    }

    public EldenRingMissedDeathAdjustments Increment(EldenRingSaveConfiguration configuration) => Change(configuration, 1);

    public EldenRingMissedDeathAdjustments Decrement(EldenRingSaveConfiguration configuration) => Change(configuration, -1);

    public IReadOnlyList<EldenRingMissedDeathAdjustment> ToEntries() => values
        .OrderBy(static pair => pair.Key.LocalSavePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static pair => pair.Key.SlotIndex)
        .Select(static pair => new EldenRingMissedDeathAdjustment(pair.Key.LocalSavePath, pair.Key.SlotIndex, pair.Value))
        .ToArray();

    public static bool IsConfiguredCharacter(EldenRingSaveConfiguration configuration) =>
        configuration.LocalPath is not null && configuration.SlotIndex != EldenRingSaveConfiguration.NoSlotIndex;

    private EldenRingMissedDeathAdjustments Change(EldenRingSaveConfiguration configuration, int delta)
    {
        if (!IsConfiguredCharacter(configuration)) throw new InvalidOperationException("A selected Elden Ring save and character are required.");
        EldenRingMissedDeathAdjustmentKey key = EldenRingMissedDeathAdjustmentKey.Create(configuration.LocalPath!, configuration.SlotIndex);
        long current = values.TryGetValue(key, out long value) ? value : 0;
        long updated = delta > 0 ? checked(current + delta) : Math.Max(0, current + delta);
        if (updated == current) return this;

        return new EldenRingMissedDeathAdjustments(values
            .Where(pair => pair.Key != key)
            .Select(static pair => new EldenRingMissedDeathAdjustment(pair.Key.LocalSavePath, pair.Key.SlotIndex, pair.Value))
            .Append(new EldenRingMissedDeathAdjustment(key.LocalSavePath, key.SlotIndex, updated)));
    }

    private readonly record struct EldenRingMissedDeathAdjustmentKey(string LocalSavePath, int SlotIndex)
    {
        public static EldenRingMissedDeathAdjustmentKey Create(string localSavePath, int slotIndex)
        {
            if (string.IsNullOrWhiteSpace(localSavePath)) throw new ArgumentException("A local save path is required.", nameof(localSavePath));
            ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
            return new(Path.GetFullPath(localSavePath).ToUpperInvariant(), slotIndex);
        }
    }
}

/// <summary>One persisted local Elden Ring missed-death value.</summary>
public sealed record EldenRingMissedDeathAdjustment(string LocalSavePath, int SlotIndex, long Value);

/// <summary>Applies the Elden Ring manual adjustment without changing reader data.</summary>
public static class TotalDeathsDisplayProjection
{
    public static long? Combine(PersistentTrackerState state, RuntimeGameObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (observation?.GameId == state.SelectedGameId)
        {
            return AddEldenRingAdjustment(state, observation.TotalDeaths.Value);
        }

        if (state.SelectedGameId == GameId.EldenRing && EldenRingMissedDeathAdjustments.IsConfiguredCharacter(state.EldenRingSave))
        {
            return state.EldenRingMissedDeathAdjustments.Get(state.EldenRingSave);
        }

        return null;
    }

    public static long AddEldenRingAdjustment(PersistentTrackerState state, long savedTotal)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SelectedGameId == GameId.EldenRing
            ? checked(savedTotal + state.EldenRingMissedDeathAdjustments.Get(state.EldenRingSave))
            : savedTotal;
    }
}
