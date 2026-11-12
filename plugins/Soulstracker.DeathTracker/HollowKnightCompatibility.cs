namespace Soulstracker.DeathTracking;

/// <summary>Explicit runtime pairs; API updates never implicitly enable a game build.</summary>
public static class HollowKnightCompatibility
{
    public static bool SupportsGame(string version) => version == "1.5.78.11833" || version == "1.5.12620";
    public static bool SupportsRuntime(string? game, string api) =>
        game == "1.5.78.11833" && api == "1.5.78.11833-77" || game == "1.5.12620" && api == "1.5.12620.0-78";
}
