using System.IO;

namespace SoulsTracker.Desktop;

/// <summary>
/// Resolves the local state root for the desktop process. The override is deliberately
/// opt-in so published builds continue using the normal per-user application-data path.
/// </summary>
public static class DesktopDataRootResolver
{
    public const string DataRootOption = "--data-root";

    public static DesktopDataRootSelection Resolve(IReadOnlyList<string> arguments, string localApplicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);

        string defaultRoot = Normalize(Path.Combine(localApplicationDataPath, "SoulsTracker"));
        if (arguments.Count == 0)
        {
            return new DesktopDataRootSelection(defaultRoot, IsDevelopmentOverride: false);
        }

        if (arguments.Count != 2 || !string.Equals(arguments[0], DataRootOption, StringComparison.Ordinal))
        {
            throw new ArgumentException("Use --data-root followed by a separate absolute folder path.", nameof(arguments));
        }

        if (string.IsNullOrWhiteSpace(arguments[1]) || !Path.IsPathFullyQualified(arguments[1]))
        {
            throw new ArgumentException("The development data root must be an absolute folder path.", nameof(arguments));
        }

        string overrideRoot = Normalize(arguments[1]);
        if (string.Equals(overrideRoot, defaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The development data root must be different from the normal SoulsTracker data folder.", nameof(arguments));
        }

        string root = Path.GetPathRoot(overrideRoot) ?? string.Empty;
        if (string.Equals(overrideRoot, Normalize(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The development data root must be a folder below a drive root.", nameof(arguments));
        }

        return new DesktopDataRootSelection(overrideRoot, IsDevelopmentOverride: true);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

public sealed record DesktopDataRootSelection(string RootPath, bool IsDevelopmentOverride);
