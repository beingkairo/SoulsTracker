namespace SoulsTracker.Desktop;

/// <summary>One local Elden Ring profile choice. The persisted index remains zero-based.</summary>
public sealed record EldenRingProfileSlotChoice(int Index)
{
    public string Label => $"Character slot {Index + 1}";
}
