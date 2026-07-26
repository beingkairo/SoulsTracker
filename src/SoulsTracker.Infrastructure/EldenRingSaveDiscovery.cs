using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Supplies the one approved local Elden Ring save root.</summary>
public interface IEldenRingSaveRootSource
{
    string? GetSaveRoot();
}

/// <summary>Discovers parser-valid ER0000.sl2 files one account level below the local AppData root.</summary>
public sealed class EldenRingSaveDiscovery(IEldenRingSaveRootSource? rootSource = null) : ILocalSaveDiscovery
{
    private const int MaximumAccounts = 64;
    private readonly IEldenRingSaveRootSource rootSource = rootSource ?? new LocalEldenRingSaveRootSource();

    public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? suppliedRoot = rootSource.GetSaveRoot();
        if (!TryDirectory(suppliedRoot, out string root)) return ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([]);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string account in EnumerateDirectories(root, MaximumAccounts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlackMythWukongSaveDiscovery.HasReparsePointBetween(root, account)) continue;
            string candidate = Path.Combine(account, "ER0000.sl2");
            if (BlackMythWukongSaveDiscovery.HasReparsePointBetween(account, candidate) || !IsParserValidSave(candidate)) continue;
            paths.Add(Path.GetFullPath(candidate));
        }

        DiscoveredLocalSave[] discovered = paths
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(static (path, index) => new DiscoveredLocalSave(path, $"Save {index + 1}"))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>(discovered);
    }

    public static bool IsParserValidSave(string path)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(path), "ER0000.sl2", StringComparison.OrdinalIgnoreCase)) return false;
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > EldenRingSaveParser.MaximumSupportedFileBytes) return false;
            byte[] contents = File.ReadAllBytes(path);
            return EldenRingSaveProfileParser.TryReadSlots(contents, out _);
        }
        catch
        {
            return false;
        }
    }

    private static string[] EnumerateDirectories(string root, int maximum)
    {
        try { return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(maximum).ToArray(); }
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
            return attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint)
                && !BlackMythWukongSaveDiscovery.HasReparsePointInPath(canonical);
        }
        catch { return false; }
    }
}

internal sealed class LocalEldenRingSaveRootSource : IEldenRingSaveRootSource
{
    public string? GetSaveRoot()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "EldenRing");
    }
}
