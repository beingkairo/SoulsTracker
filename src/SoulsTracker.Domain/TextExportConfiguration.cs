namespace SoulsTracker.Domain;

/// <summary>Validated, explicitly selected local text-output settings.</summary>
public sealed record TextExportConfiguration
{
    public static TextExportConfiguration Default { get; } = new(null, false);

    public TextExportConfiguration(string? deathsPath, bool deathsEnabled)
    {
        DeathsPath = Validate(deathsPath, nameof(deathsPath));
        DeathsEnabled = deathsEnabled;
    }

    public string? DeathsPath { get; }
    public bool DeathsEnabled { get; }

    private static string? Validate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Path.GetExtension(value).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Text exports require a TXT file.", name);
        return value;
    }
}
