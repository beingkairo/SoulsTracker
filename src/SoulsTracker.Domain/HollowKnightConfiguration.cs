namespace SoulsTracker.Domain;

/// <summary>Installation binding and local additions. Recorded deaths belong to the game-side plugin.</summary>
public sealed record HollowKnightConfiguration(string? GameDirectory = null, Guid? InstallationId = null)
{
    public static HollowKnightConfiguration Default { get; } = new();
    public HollowKnightDeathAdjustments DeathAdjustments { get; init; } = HollowKnightDeathAdjustments.Empty;
}
