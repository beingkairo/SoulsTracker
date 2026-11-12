namespace SoulsTracker.Domain;

/// <summary>Local added deaths, isolated by installation and persistent save-run identity.</summary>
public sealed class HollowKnightDeathAdjustments : IEquatable<HollowKnightDeathAdjustments>
{
    private readonly Dictionary<(Guid Installation, Guid Run), long> values;
    public static HollowKnightDeathAdjustments Empty { get; } = new([]);

    public HollowKnightDeathAdjustments(IEnumerable<HollowKnightDeathAdjustment> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        values = [];
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Validate(entry.Installation, entry.Run);
            ArgumentOutOfRangeException.ThrowIfNegative(entry.Value);
            if (entry.Value != 0) values.Add((entry.Installation, entry.Run), entry.Value);
        }
    }

    public long Get(Guid installation, Guid run) => values.GetValueOrDefault((installation, run));

    public HollowKnightDeathAdjustments Change(Guid installation, Guid run, bool increment)
    {
        Validate(installation, run);
        long current = Get(installation, run);
        long next = increment ? checked(current + 1) : Math.Max(0, current - 1);
        if (next == current) return this;
        return new(values.Where(pair => pair.Key != (installation, run))
            .Select(pair => new HollowKnightDeathAdjustment(pair.Key.Installation, pair.Key.Run, pair.Value))
            .Append(new(installation, run, next)));
    }

    public IReadOnlyList<HollowKnightDeathAdjustment> ToEntries() => values
        .OrderBy(pair => pair.Key.Installation).ThenBy(pair => pair.Key.Run)
        .Select(pair => new HollowKnightDeathAdjustment(pair.Key.Installation, pair.Key.Run, pair.Value)).ToArray();

    public bool Equals(HollowKnightDeathAdjustments? other) => other is not null && values.Count == other.values.Count &&
        values.All(pair => other.values.TryGetValue(pair.Key, out long value) && value == pair.Value);
    public override bool Equals(object? obj) => obj is HollowKnightDeathAdjustments other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in ToEntries()) hash.Add(entry);
        return hash.ToHashCode();
    }

    private static void Validate(Guid installation, Guid run)
    {
        if (installation == Guid.Empty || run == Guid.Empty) throw new ArgumentException("A tracking installation and save identity are required.");
    }
}

public sealed record HollowKnightDeathAdjustment(Guid Installation, Guid Run, long Value);
