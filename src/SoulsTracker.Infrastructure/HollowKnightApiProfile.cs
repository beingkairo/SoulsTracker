using System.Text.Json;

namespace SoulsTracker.Infrastructure;

internal sealed record HollowKnightApiProfile(string GameVersion, string ApiVersion, Dictionary<string, string> RuntimeFiles,
    Dictionary<string, string> VanillaFiles, string? ArchiveHash)
{
    internal static readonly HollowKnightApiProfile V77 = new("1.5.78.11833", HollowKnightPluginInstaller.ApiTag,
        Read<Dictionary<string, string>>("HollowKnightApiV77.json"), [], HollowKnightPluginInstaller.ArchiveHash);
    private static readonly ProfileData Modern = Read<ProfileData>("HollowKnightApi12620.json");
    internal static readonly HollowKnightApiProfile V78 = new(Modern.GameVersion, Modern.ApiVersion, Modern.RuntimeFiles, Modern.VanillaFiles, null);
    internal static HollowKnightApiProfile? ForVersion(string version) => version == V77.GameVersion ? V77 : version == V78.GameVersion ? V78 : null;
    internal static HollowKnightApiProfile ForIdentity(string identity) => identity == V77.ApiVersion ? V77 : identity == V78.ApiVersion ? V78 : throw new IOException("Unknown API profile.");
    private static T Read<T>(string name)
    {
        using Stream stream = typeof(HollowKnightApiProfile).Assembly.GetManifestResourceStream("SoulsTracker.Infrastructure." + name)!;
        return JsonSerializer.Deserialize<T>(stream) ?? throw new IOException("Missing API profile.");
    }
    private sealed record ProfileData(string GameVersion, string ApiVersion, Dictionary<string, string> RuntimeFiles, Dictionary<string, string> VanillaFiles);
}
