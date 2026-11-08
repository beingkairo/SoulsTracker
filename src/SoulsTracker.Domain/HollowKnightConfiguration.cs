namespace SoulsTracker.Domain;

/// <summary>Installation binding only. Hollow Knight totals belong to the game-side plugin.</summary>
public sealed record HollowKnightConfiguration(string? GameDirectory = null, Guid? InstallationId = null)
{
    public static HollowKnightConfiguration Default { get; } = new();
}
