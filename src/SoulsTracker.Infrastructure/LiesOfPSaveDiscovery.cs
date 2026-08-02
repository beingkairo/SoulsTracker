using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SoulsTracker.Infrastructure;

/// <summary>Supplies verified Steam Lies of P install roots without scanning arbitrary drives.</summary>
public interface ILiesOfPSteamInstallRootSource
{
    IEnumerable<string> GetInstallRoots(CancellationToken cancellationToken);
}

/// <summary>Discovers one logical Lies of P character per paired Steam save files.</summary>
public sealed class LiesOfPSaveDiscovery(ILiesOfPSteamInstallRootSource? installRoots = null) : ILocalSaveDiscovery
{
    private const int MaximumAccounts = 64;
    private const int MaximumFilesPerAccount = 128;
    private const long MaximumSaveBytes = LiesOfPSaveParser.MaximumSupportedFileBytes;
    private readonly ILiesOfPSteamInstallRootSource installRoots = installRoots ?? new LocalLiesOfPSteamInstallRootSource();

    public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var members = new List<(string path, int character, DateTime lastWriteUtc)>();
        foreach (string suppliedRoot in installRoots.GetInstallRoots(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDirectory(suppliedRoot, out string root)) continue;
            string savesRoot = Path.Combine(root, "LiesofP", "Saved", "SaveGames");
            if (!TryDirectory(savesRoot, out string canonicalSavesRoot) || BlackMythWukongSaveDiscovery.HasReparsePointBetween(root, canonicalSavesRoot)) continue;
            foreach (string account in SafeDirectories(canonicalSavesRoot, MaximumAccounts))
            {
                if (BlackMythWukongSaveDiscovery.HasReparsePointBetween(canonicalSavesRoot, account)) continue;
                foreach (string path in SafeFiles(account, "SaveData-*_Character_*.sav", MaximumFilesPerAccount))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (BlackMythWukongSaveDiscovery.HasReparsePointBetween(account, path) || !IsRegularBoundedSave(path)) continue;
                    int character = LiesOfPSaveMembers.CharacterNumber(path);
                    if (character == int.MaxValue) continue;
                    members.Add((Path.GetFullPath(path), character, new FileInfo(path).LastWriteTimeUtc));
                }
            }
        }

        var candidates = members
            .GroupBy(static candidate => CharacterKey(candidate.path, candidate.character), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(candidate => candidate.lastWriteUtc).ThenBy(candidate => candidate.path, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(static candidate => candidate.character)
            .ThenBy(static candidate => candidate.path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IReadOnlyList<DiscoveredLocalSave> labeled = candidates
            .GroupBy(static candidate => candidate.character)
            .SelectMany(static group => group.Select((candidate, index) => new DiscoveredLocalSave(
                candidate.path,
                group.Count() == 1 ? $"Character {candidate.character}" : $"Character {candidate.character} ({index + 1})")))
            .ToArray();
        return ValueTask.FromResult(labeled);
    }

    public static bool IsRegularBoundedSave(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length is <= 0 or > MaximumSaveBytes) return false;
            byte[] contents = File.ReadAllBytes(path);
            return LiesOfPSaveParser.TryReadTotalDeaths(contents, out _) == LiesOfPSaveParseOutcome.Success;
        }
        catch { return false; }
    }

    private static string CharacterKey(string path, int character) => $"{Path.GetDirectoryName(path)}|{character}";

    private static bool TryDirectory(string? path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        try
        {
            canonical = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(canonical);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint) && !BlackMythWukongSaveDiscovery.HasReparsePointInPath(canonical);
        }
        catch { return false; }
    }

    private static string[] SafeDirectories(string root, int maximum)
    {
        try { return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(maximum).ToArray(); }
        catch { return []; }
    }

    private static string[] SafeFiles(string root, string pattern, int maximum)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Take(maximum).ToArray(); }
        catch { return []; }
    }
}

/// <summary>Resolves only Steam's own launcher metadata for the Lies of P Steam app.</summary>
public sealed class LocalLiesOfPSteamInstallRootSource(ILiesOfPSteamLauncherEnvironment? environment = null) : ILiesOfPSteamInstallRootSource
{
    private const int MaximumMetadataBytes = 1_048_576;
    private readonly ILiesOfPSteamLauncherEnvironment environment = environment ?? new WindowsLiesOfPSteamLauncherEnvironment();

    public IEnumerable<string> GetInstallRoots(CancellationToken cancellationToken)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidate in new[] { environment.CurrentUserSteamRoot, environment.ConventionalSteamRoot })
        {
            if (TryDirectory(candidate, out string root)) libraries.Add(root);
        }
        foreach (string root in libraries.ToArray())
        {
            string folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!BlackMythWukongSaveDiscovery.HasReparsePointBetween(root, folders))
            {
                foreach (string library in ReadVdfPaths(folders)) libraries.Add(library);
            }
        }
        foreach (string library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifest = Path.Combine(library, "steamapps", "appmanifest_1627720.acf");
            if (BlackMythWukongSaveDiscovery.HasReparsePointBetween(library, manifest)) continue;
            string? installDirectory = ReadVdfValue(manifest, "installdir");
            if (!IsSingleDirectoryName(installDirectory)) continue;
            string candidate = Path.Combine(library, "steamapps", "common", installDirectory!);
            if (TryDirectory(candidate, out string canonical)) yield return canonical;
        }
    }

    private static IEnumerable<string> ReadVdfPaths(string path)
    {
        string? text = ReadBoundedText(path);
        if (text is null) yield break;
        foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string candidate = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            if (TryDirectory(candidate, out string canonical)) yield return canonical;
        }
    }

    private static string? ReadVdfValue(string path, string key)
    {
        string? text = ReadBoundedText(path);
        if (text is null) return null;
        Match match = Regex.Match(text, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsSingleDirectoryName(string? value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." && !Path.IsPathRooted(value) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.Contains(Path.DirectorySeparatorChar) && !value.Contains(Path.AltDirectorySeparatorChar);

    private static string? ReadBoundedText(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint) && new FileInfo(path).Length <= MaximumMetadataBytes ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    private static bool TryDirectory(string? path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        try
        {
            canonical = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(canonical);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint) && !BlackMythWukongSaveDiscovery.HasReparsePointInPath(canonical);
        }
        catch { return false; }
    }
}

public interface ILiesOfPSteamLauncherEnvironment
{
    string? CurrentUserSteamRoot { get; }
    string? ConventionalSteamRoot { get; }
}

internal sealed class WindowsLiesOfPSteamLauncherEnvironment : ILiesOfPSteamLauncherEnvironment
{
    public string? CurrentUserSteamRoot { get { try { return OperatingSystem.IsWindows() ? Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string : null; } catch { return null; } } }
    public string? ConventionalSteamRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
}
