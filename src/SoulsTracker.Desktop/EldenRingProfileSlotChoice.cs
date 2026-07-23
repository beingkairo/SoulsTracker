using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>One local Elden Ring profile choice. The persisted index remains zero-based.</summary>
public sealed record EldenRingProfileSlotChoice(int Index, bool IsEmpty = false, string? Name = null, int? Level = null)
{
    public string Label => IsEmpty
        ? $"Character {Index + 1} \u00B7 Empty"
        : Name is not null && Level is not null
            ? $"{Name} \u00B7 Level {Level}"
            : Name is not null
                ? Name
                : Level is not null
                    ? $"Character {Index + 1} \u00B7 Level {Level}"
                    : $"Character {Index + 1}";

    public static EldenRingProfileSlotChoice FromMetadata(EldenRingCharacterSlotMetadata metadata) =>
        new(metadata.Index, metadata.IsEmpty, metadata.Name, metadata.Level);
}
