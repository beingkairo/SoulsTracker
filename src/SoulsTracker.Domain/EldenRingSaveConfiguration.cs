namespace SoulsTracker.Domain;

/// <summary>Validated, local-only selection for Elden Ring's read-only save reader.</summary>
public sealed record EldenRingSaveConfiguration
{
    public const int MinimumSlotIndex = 0;
    public const int MaximumSlotIndex = 9;

    public static EldenRingSaveConfiguration Default { get; } = new(null, MinimumSlotIndex);

    public EldenRingSaveConfiguration(string? localPath, int slotIndex)
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

        LocalPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath;
        SlotIndex = slotIndex;
    }

    /// <summary>Private, user-selected path. It must never be logged or shown outside the local picker.</summary>
    public string? LocalPath { get; }

    /// <summary>Zero-based Elden Ring profile slot.</summary>
    public int SlotIndex { get; }

    public string? FileName => LocalPath is null ? null : Path.GetFileName(LocalPath);
}
