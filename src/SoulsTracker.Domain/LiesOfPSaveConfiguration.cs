namespace SoulsTracker.Domain;

/// <summary>Validated, local-only selection for Lies of P's read-only Steam save reader.</summary>
public sealed record LiesOfPSaveConfiguration
{
    public static LiesOfPSaveConfiguration Default { get; } = new((string?)null);

    public LiesOfPSaveConfiguration(string? localPath)
    {
        if (!string.IsNullOrWhiteSpace(localPath) && !IsCharacterSaveFileName(Path.GetFileName(localPath)))
        {
            throw new ArgumentException("Choose a SaveData-X_Character_1.sav or SaveData-X_Character_2.sav Lies of P save file.", nameof(localPath));
        }

        LocalPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath;
    }

    /// <summary>Private, user-selected path. It is never logged or sent anywhere.</summary>
    public string? LocalPath { get; }

    public string? FileName => LocalPath is null ? null : Path.GetFileName(LocalPath);

    public static bool IsCharacterSaveFileName(string? fileName) =>
        fileName is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(
            fileName,
            @"^SaveData-[1-9][0-9]*_Character_[12]\.sav$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
