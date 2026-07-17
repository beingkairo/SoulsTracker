using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

/// <summary>Provides the user-facing labels for the bounded Total Deaths title icon values.</summary>
public sealed record OverlayTitleIconModeChoice(OverlayTitleIconMode Value, string Label)
{
    public static IReadOnlyList<OverlayTitleIconModeChoice> All { get; } =
    [
        new(OverlayTitleIconMode.Off, "Off"),
        new(OverlayTitleIconMode.PrefixSkull, "Prefix skull"),
        new(OverlayTitleIconMode.SkullOnly, "Skull only"),
    ];
}
