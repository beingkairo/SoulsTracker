namespace SoulsTracker.Domain;

/// <summary>Validated, local-only selection for Black Myth: Wukong's read-only save reader.</summary>
public sealed record BlackMythWukongSaveConfiguration
{
    public static BlackMythWukongSaveConfiguration Default { get; } = new((string?)null);

    public BlackMythWukongSaveConfiguration(string? localPath)
    {
        if (!string.IsNullOrWhiteSpace(localPath) && !IsArchiveSaveFileName(Path.GetFileName(localPath)))
        {
            throw new ArgumentException("Choose an ArchiveSaveFile.<slot>.sav Black Myth: Wukong save file.", nameof(localPath));
        }

        LocalPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath;
    }

    /// <summary>Private, user-selected path. It must never be logged or shown outside the local picker.</summary>
    public string? LocalPath { get; }

    public string? FileName => LocalPath is null ? null : Path.GetFileName(LocalPath);

    public static bool IsArchiveSaveFileName(string? fileName) =>
        fileName is not null &&
        fileName.StartsWith("ArchiveSaveFile.", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) &&
        fileName.Length > "ArchiveSaveFile..sav".Length;
}
