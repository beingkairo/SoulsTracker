namespace SoulsTracker.Domain;

/// <summary>Validated, explicitly selected local text-output settings.</summary>
public sealed record TextExportConfiguration
{
    public static TextExportConfiguration Default { get; } = new(null, false, null, false);

    public TextExportConfiguration(string? deathsPath, bool deathsEnabled, string? bossListPath, bool bossListEnabled)
    {
        DeathsPath = Validate(deathsPath, nameof(deathsPath));
        BossListPath = Validate(bossListPath, nameof(bossListPath));
        DeathsEnabled = deathsEnabled && DeathsPath is not null;
        BossListEnabled = bossListEnabled && BossListPath is not null;
    }
    public string? DeathsPath { get; }
    public bool DeathsEnabled { get; }
    public string? BossListPath { get; }
    public bool BossListEnabled { get; }
    private static string? Validate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Path.GetExtension(value).Equals(".txt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Text exports require a TXT file.", name);
        return value;
    }
}
