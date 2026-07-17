namespace SoulsTracker.Desktop;

/// <summary>One mutually exclusive defeated-boss marker choice.</summary>
public sealed record BossMarkerChoice(string Value, string Label)
{
    public static IReadOnlyList<BossMarkerChoice> All { get; } =
    [
        new("None", "None"),
        new("Checkmark", "Checkmark"),
        new("Skull", "Skull"),
    ];
}
