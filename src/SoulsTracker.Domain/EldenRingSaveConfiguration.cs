namespace SoulsTracker.Domain;

/// <summary>Chooses the locally remembered Elden Ring boss-list view.</summary>
public enum EldenRingBossListScope
{
    AllBosses,
    BaseGame,
    ShadowOfTheErdtree,
}

/// <summary>Validated, local-only selection for Elden Ring's read-only save reader.</summary>
public sealed record EldenRingSaveConfiguration
{
    public const int MinimumSlotIndex = 0;
    public const int MaximumSlotIndex = 9;

    public static EldenRingSaveConfiguration Default { get; } = new(null, MinimumSlotIndex);

    public EldenRingSaveConfiguration(
        string? localPath,
        int slotIndex,
        EldenRingBossListScope bossListScope = EldenRingBossListScope.AllBosses,
        bool requiredBossesOnly = false)
    {
        if (slotIndex is < MinimumSlotIndex or > MaximumSlotIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), "Elden Ring save slots must be between 1 and 10.");
        }

        if (!string.IsNullOrWhiteSpace(localPath) &&
            !string.Equals(Path.GetFileName(localPath), "ER0000.sl2", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose the ER0000.sl2 Elden Ring save file.", nameof(localPath));
        }

        if (!Enum.IsDefined(bossListScope))
        {
            throw new ArgumentOutOfRangeException(nameof(bossListScope));
        }

        LocalPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath;
        SlotIndex = slotIndex;
        BossListScope = bossListScope;
        RequiredBossesOnly = requiredBossesOnly;
    }

    /// <summary>Private, user-selected path. It must never be logged or shown outside the local picker.</summary>
    public string? LocalPath { get; }

    /// <summary>Zero-based Elden Ring profile slot.</summary>
    public int SlotIndex { get; }

    /// <summary>Locally persisted scope shared by the checklist, overlay, preview, and TXT export.</summary>
    public EldenRingBossListScope BossListScope { get; }

    /// <summary>Gets whether only the documented progression-gate entries are displayed.</summary>
    public bool RequiredBossesOnly { get; }

    public string? FileName => LocalPath is null ? null : Path.GetFileName(LocalPath);
}
