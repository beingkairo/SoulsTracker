namespace SoulsTracker.Domain;

/// <summary>
/// Configures the Total Deaths overlay presentation.
/// </summary>
public sealed class TotalDeathsOverlayOptions
{
    /// <summary>
    /// Gets the stable default Total Deaths options.
    /// </summary>
    public static TotalDeathsOverlayOptions Default { get; } = new(isEnabled: true, showGameName: false, compactTitle: true, appearance: OverlayAppearance.Default, titleIconMode: OverlayTitleIconMode.PrefixSkull);

    /// <summary>
    /// Initializes immutable Total Deaths overlay options.
    /// </summary>
    public TotalDeathsOverlayOptions(bool isEnabled, bool showGameName, bool compactTitle = false, OverlayAppearance? appearance = null, OverlayTitleIconMode titleIconMode = OverlayTitleIconMode.Off)
    {
        IsEnabled = isEnabled;
        // These two legacy fields remain deserialize-only. V1 presentation is always inline and never shows a game name.
        ShowGameName = false;
        CompactTitle = true;
        Appearance = (appearance ?? OverlayAppearance.Default).WithAlignment(OverlayTextAlignment.Left);
        TitleIconMode = Enum.IsDefined(titleIconMode) ? titleIconMode : OverlayTitleIconMode.Off;
    }

    /// <summary>
    /// Gets whether the overlay is enabled.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Gets whether the selected game name is shown.
    /// </summary>
    public bool ShowGameName { get; }
    public bool CompactTitle { get; }
    public OverlayAppearance Appearance { get; }
    public OverlayTitleIconMode TitleIconMode { get; }
}

/// <summary>
/// Holds immutable, validated overlay presentation configuration.
/// </summary>
public sealed class OverlayConfiguration
{
    /// <summary>
    /// Gets the only schema version supported by this contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets the stable default configuration.
    /// </summary>
    public static OverlayConfiguration Default { get; } = new(
        CurrentSchemaVersion,
        TotalDeathsOverlayOptions.Default);

    /// <summary>
    /// Initializes validated overlay configuration.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the schema version is unsupported.</exception>
    public OverlayConfiguration(
        int schemaVersion,
        TotalDeathsOverlayOptions totalDeaths)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The overlay configuration schema version is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(totalDeaths);


        SchemaVersion = schemaVersion;
        TotalDeaths = totalDeaths;

    }

    /// <summary>
    /// Gets the configuration schema version.
    /// </summary>
    public int SchemaVersion { get; }


    /// <summary>
    /// Gets the Total Deaths overlay options.
    /// </summary>
    public TotalDeathsOverlayOptions TotalDeaths { get; }

}
