namespace SoulsTracker.Domain;

/// <summary>Validated, local-only selection for Black Myth: Wukong's read-only save reader.</summary>
public sealed record BlackMythWukongSaveConfiguration
{
    public static BlackMythWukongSaveConfiguration Default { get; } = new((string?)null);

    public BlackMythWukongSaveConfiguration(string? localPath, string? selectedDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(localPath) && !IsArchiveSaveFileName(Path.GetFileName(localPath)))
        {
            throw new ArgumentException("Choose an ArchiveSaveFile.<slot>.sav Black Myth: Wukong save file.", nameof(localPath));
        }

        LocalPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath;
        SelectedDirectory = string.IsNullOrWhiteSpace(selectedDirectory) ? null : selectedDirectory;
    }

    /// <summary>Private resolved save path. It must never be logged or published.</summary>
    public string? LocalPath { get; }

    /// <summary>Optional local discovery root, independent of the resolved reader source.</summary>
    public string? SelectedDirectory { get; }

    public string? FileName => LocalPath is null ? null : Path.GetFileName(LocalPath);

    public static bool IsArchiveSaveFileName(string? fileName) =>
        fileName is not null &&
        fileName.StartsWith("ArchiveSaveFile.", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) &&
        fileName.Length > "ArchiveSaveFile..sav".Length;
}
