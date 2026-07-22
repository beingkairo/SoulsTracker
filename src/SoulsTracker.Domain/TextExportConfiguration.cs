namespace SoulsTracker.Domain;

/// <summary>Validated, explicitly selected local text-output settings.</summary>
public sealed record TextExportConfiguration
{
    public static TextExportConfiguration Default { get; } = new(null, false, null, false);

    public TextExportConfiguration(string? deathsPath, bool deathsEnabled, string? bossListPath, bool bossListEnabled)
    {
        DeathsPath = Validate(deathsPath, nameof(deathsPath));
        BossListPath = Validate(bossListPath, nameof(bossListPath));
        // Enablement represents the user's intent, independently of whether they
        // have selected a destination yet. The desktop flow deliberately enables
        // the Choose button only after this intent is selected, so collapsing an
        // enabled/no-path draft back to false makes that flow impossible. Writers
        // still require a non-null path before performing I/O.
        DeathsEnabled = deathsEnabled;
        BossListEnabled = bossListEnabled;
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
