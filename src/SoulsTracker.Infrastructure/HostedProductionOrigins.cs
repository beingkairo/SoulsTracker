namespace SoulsTracker.Infrastructure;

/// <summary>Exact approved HTTPS origins; all other hosts are denied.</summary>
public static class HostedProductionOrigins
{
    public static IReadOnlyList<string> Approved { get; } = Array.AsReadOnly(new[] { "https://overlay.beingkairo.com" });
}
