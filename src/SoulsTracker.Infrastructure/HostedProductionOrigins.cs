namespace SoulsTracker.Infrastructure;

/// <summary>Exact approved HTTPS origins. No host is authorized in this build.</summary>
public static class HostedProductionOrigins
{
    public static IReadOnlyList<string> Approved { get; } = Array.AsReadOnly(Array.Empty<string>());
}
