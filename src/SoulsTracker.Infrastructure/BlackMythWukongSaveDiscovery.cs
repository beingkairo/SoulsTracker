using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Read-only, bounded local save discovery contract shared by save-file games.</summary>
public interface ILocalSaveDiscovery
{
    ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken);
}

/// <summary>A non-sensitive locally discovered save candidate.</summary>
public sealed record DiscoveredLocalSave(string LocalPath, string Label);

/// <summary>Supplies verified game install roots without coupling local discovery to presentation or persistence.</summary>
public interface IBlackMythWukongInstallRootSource
{
    IEnumerable<string> GetInstallRoots(CancellationToken cancellationToken);
}

/// <summary>Discovers Black Myth: Wukong save slots only below verified Steam and Epic installations.</summary>
public sealed class BlackMythWukongSaveDiscovery(IBlackMythWukongInstallRootSource? installRoots = null) : ILocalSaveDiscovery
{
    private const int MaximumAccounts = 64;
    private const int MaximumSlotsPerAccount = 64;
    private const long MaximumSaveBytes = 8L * 1024 * 1024;
    private readonly IBlackMythWukongInstallRootSource installRoots = installRoots ?? new LocalBlackMythWukongInstallRootSource();

    public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovered = new Dictionary<string, DiscoveredLocalSave>(StringComparer.OrdinalIgnoreCase);
        foreach (string installRoot in installRoots.GetInstallRoots(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDirectory(installRoot, out string root)) continue;
            string saveGames = Path.Combine(root, "b1", "Saved", "SaveGames");
            if (!TryDirectory(saveGames, out string canonicalSaveGames) || HasReparsePointBetween(root, canonicalSaveGames)) continue;
            foreach (string account in EnumerateDirectories(canonicalSaveGames, MaximumAccounts))
            {
                if (HasReparsePointBetween(canonicalSaveGames, account)) continue;
                foreach (string save in EnumerateFiles(account, "ArchiveSaveFile.*.sav", MaximumSlotsPerAccount))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (HasReparsePointBetween(account, save) || !IsRegularBoundedSave(save) || !BlackMythWukongSaveConfiguration.IsArchiveSaveFileName(Path.GetFileName(save))) continue;
                    string canonical = Path.GetFullPath(save);
                    int slot = SlotNumber(canonical);
                    if (slot == int.MaxValue) continue;
                    discovered.TryAdd(canonical, new DiscoveredLocalSave(canonical, $"Save slot {slot}"));
                }
            }
        }

        DiscoveredLocalSave[] ordered = discovered.Values
            .OrderBy(static save => SlotNumber(save.LocalPath))
            .ThenBy(static save => save.LocalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<DiscoveredLocalSave> labeled = ordered
            .GroupBy(static save => SlotNumber(save.LocalPath))
            .SelectMany(static group => group.Select((save, index) => new DiscoveredLocalSave(
                save.LocalPath,
                group.Count() == 1 ? save.Label : $"{save.Label} ({index + 1})")))
            .ToArray();
        return ValueTask.FromResult(labeled);
    }

    private static string[] EnumerateDirectories(string root, int maximum)
    {
        try { return Directory.EnumerateDirectories(root).Take(maximum).ToArray(); }
        catch { return []; }
    }

    private static string[] EnumerateFiles(string root, string pattern, int maximum)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Take(maximum).ToArray(); }
        catch { return []; }
    }

    private static bool TryDirectory(string? path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        try
        {
            canonical = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(canonical);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    private static bool IsRegularBoundedSave(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > MaximumSaveBytes) return false;
            byte[] contents = File.ReadAllBytes(path);
            return BlackMythWukongSaveParser.TryReadTotalDeaths(contents, out _) == BlackMythWukongSaveParseOutcome.Success;
        }
        catch { return false; }
    }

    private static bool HasReparsePointBetween(string root, string path)
    {
        try
        {
            string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string canonicalPath = Path.GetFullPath(path);
            string rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
            if (!canonicalPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            string relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
            string current = canonicalRoot;
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            return false;
        }
        catch { return true; }
    }

    private static int SlotNumber(string path)
    {
        Match match = Regex.Match(Path.GetFileName(path), @"^ArchiveSaveFile\.(\d+)\.sav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : int.MaxValue;
    }
}

/// <summary>Resolves only local Steam and Epic launcher metadata; it never scans arbitrary drives.</summary>
public sealed class LocalBlackMythWukongInstallRootSource : IBlackMythWukongInstallRootSource
{
    private const int MaximumMetadataBytes = 1_048_576;

    public IEnumerable<string> GetInstallRoots(CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in SteamInstallRoots(cancellationToken)) roots.Add(root);
        foreach (string root in EpicInstallRoots(cancellationToken)) roots.Add(root);
        return roots;
    }

    private static IEnumerable<string> SteamInstallRoots(CancellationToken cancellationToken)
    {
        string? steamRoot = null;
        try { if (OperatingSystem.IsWindows()) steamRoot = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string; }
        catch { }
        steamRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (!TryLocalDirectory(steamRoot, out string root)) yield break;

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        string folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        foreach (string library in ReadVdfPaths(folders)) libraries.Add(library);
        foreach (string library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifest = Path.Combine(library, "steamapps", "appmanifest_2358720.acf");
            string? installDirectory = ReadVdfValue(manifest, "installdir");
            if (string.IsNullOrWhiteSpace(installDirectory) || installDirectory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            string candidate = Path.Combine(library, "steamapps", "common", installDirectory);
            if (TryLocalDirectory(candidate, out string canonical)) yield return canonical;
        }
    }

    private static List<string> EpicInstallRoots(CancellationToken cancellationToken)
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string manifests = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!TryLocalDirectory(manifests, out string root)) return [];
        var results = new List<string>();
        foreach (string manifest in SafeFiles(root, "*.item", 128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(manifest).Length > MaximumMetadataBytes) continue;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                JsonElement value = document.RootElement;
                string? appName = value.TryGetProperty("AppName", out JsonElement app) ? app.GetString() : null;
                string? install = value.TryGetProperty("InstallLocation", out JsonElement location) ? location.GetString() : null;
                if (appName is null || !appName.Contains("wukong", StringComparison.OrdinalIgnoreCase) && !appName.Contains("blackmyth", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryLocalDirectory(install, out string canonical)) results.Add(canonical);
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return results;
    }

    private static IEnumerable<string> ReadVdfPaths(string path)
    {
        string? text = ReadBoundedText(path);
        if (text is null) yield break;
        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string candidate = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            if (TryLocalDirectory(candidate, out string canonical)) yield return canonical;
        }
    }

    private static string? ReadVdfValue(string path, string key)
    {
        string? text = ReadBoundedText(path);
        if (text is null) return null;
        Match match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ReadBoundedText(string path)
    {
        try { return new FileInfo(path).Length <= MaximumMetadataBytes ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static string[] SafeFiles(string root, string pattern, int maximum)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Take(maximum).ToArray(); }
        catch { return []; }
    }

    private static bool TryLocalDirectory(string? path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        try
        {
            canonical = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(canonical);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }
}
