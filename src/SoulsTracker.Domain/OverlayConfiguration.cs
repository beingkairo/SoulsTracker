using System.Text.Json.Serialization;

namespace SoulsTracker.Domain;

/// <summary>
/// Identifies the valid visibility filters for the boss-list overlay.
/// </summary>
public enum BossListVisibilityMode
{
    All,
    Remaining,
    Defeated,
}

/// <summary>
/// Holds an opaque, validated overlay access token without exposing its value.
/// </summary>
public sealed class OverlayAccessToken : IEquatable<OverlayAccessToken>
{
    private const int TokenLength = 43;
    private const int DecodedByteLength = 32;
    private readonly string value;

    private OverlayAccessToken(string value)
    {
        this.value = value;
    }

    /// <summary>
    /// Parses a canonical unpadded base64url token for exactly 32 bytes.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not a valid token.</exception>
    public static OverlayAccessToken Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsCanonicalBase64UrlToken(value))
        {
            throw new ArgumentException(
                "The overlay access token must be a canonical 43-character base64url value.",
                nameof(value));
        }

        return new OverlayAccessToken(value);
    }

    /// <inheritdoc />
    public bool Equals(OverlayAccessToken? other) =>
        other is not null && string.Equals(value, other.value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as OverlayAccessToken);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(value);

    /// <summary>
    /// Returns a non-secret diagnostic representation.
    /// </summary>
    public override string ToString() => "[redacted]";

    // Persistence-only bridge. This assembly exposes it to Infrastructure, not public consumers.
    internal string PersistenceValue => value;

    private static bool IsCanonicalBase64UrlToken(string value)
    {
        if (value.Length != TokenLength || value.Any(static character => !IsBase64UrlCharacter(character)))
        {
            return false;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            string canonical = Convert.ToBase64String(decoded)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            return decoded.Length == DecodedByteLength &&
                string.Equals(canonical, value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsBase64UrlCharacter(char character) =>
        (character >= 'A' && character <= 'Z') ||
        (character >= 'a' && character <= 'z') ||
        (character >= '0' && character <= '9') ||
        character is '-' or '_';
}

/// <summary>
/// Configures the optional local overlay endpoint without binding a port or
/// generating a token.
/// </summary>
public sealed class OverlayEndpointConfiguration
{
    /// <summary>
    /// Gets the immutable unassigned endpoint configuration.
    /// </summary>
    public static OverlayEndpointConfiguration Unassigned { get; } = new(port: null, accessToken: null);

    /// <summary>
    /// Initializes an unassigned endpoint or a complete assigned endpoint.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when only one assignment value is supplied.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an assigned port is outside the approved range.</exception>
    public OverlayEndpointConfiguration(int? port, OverlayAccessToken? accessToken)
    {
        if (port.HasValue != (accessToken is not null))
        {
            throw new ArgumentException("An overlay endpoint must provide both a port and an access token, or neither.");
        }

        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                port,
                "An overlay endpoint port must be between 1024 and 65535.");
        }

        Port = port;
        AccessToken = accessToken;
    }

    /// <summary>
    /// Gets the assigned local port, or <see langword="null"/> when unassigned.
    /// </summary>
    public int? Port { get; }

    /// <summary>
    /// Gets whether both required endpoint values are assigned.
    /// </summary>
    public bool IsAssigned => Port.HasValue;

    /// <summary>
    /// Gets the modeled token without allowing standard JSON serialization to expose it.
    /// </summary>
    [JsonIgnore]
    public OverlayAccessToken? AccessToken { get; }
}

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
/// Configures the boss-list overlay presentation.
/// </summary>
public enum CenterMarkerAlignment { Left, Right }

public sealed class BossListOverlayOptions
{
    /// <summary>
    /// Gets the stable default boss-list options.
    /// </summary>
    // A new list must not imply a defeated treatment the streamer did not select.
    public static BossListOverlayOptions Default { get; } = new(isEnabled: true, BossListVisibilityMode.All, OverlayAppearance.BossListDefault, "#8C8C96", DefeatedBossTreatment.Nothing, true, "#A78BFA", 25);

    /// <summary>
    /// Initializes immutable boss-list overlay options.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="visibilityMode"/> is not defined.</exception>
    public BossListOverlayOptions(bool isEnabled, BossListVisibilityMode visibilityMode, OverlayAppearance? appearance = null, string defeatedColor = "#8C8C96", DefeatedBossTreatment defeatedTreatment = DefeatedBossTreatment.Nothing, bool showCheckmark = true, string checkmarkAccent = "#A78BFA", int maximumVisibleCount = 25, bool showDefeatedSkull = false, CenterMarkerAlignment centerMarkerAlignment = CenterMarkerAlignment.Left)
    {
        if (!Enum.IsDefined(visibilityMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibilityMode),
                visibilityMode,
                "The boss-list visibility mode is not supported.");
        }

        IsEnabled = isEnabled;
        VisibilityMode = visibilityMode;
        Appearance = appearance ?? OverlayAppearance.BossListDefault;
        if (!Enum.IsDefined(defeatedTreatment) || maximumVisibleCount is < 1 or > 100 || !IsColor(defeatedColor) || !IsColor(checkmarkAccent)) throw new ArgumentOutOfRangeException(nameof(maximumVisibleCount));
        DefeatedColor = defeatedColor;
        DefeatedTreatment = defeatedTreatment == DefeatedBossTreatment.Hidden ? DefeatedBossTreatment.Nothing : defeatedTreatment;
        // Centered lists intentionally have no marker column or inline marker.
        // Normalize here as well as in the desktop draft so persisted state, direct
        // command callers, and reloaded settings cannot reintroduce a center marker.
        bool centered = Appearance.Alignment == OverlayTextAlignment.Center;
        ShowCheckmark = !centered && showCheckmark;
        CheckmarkAccent = checkmarkAccent;
        MaximumVisibleCount = maximumVisibleCount;
        ShowDefeatedSkull = !centered && showDefeatedSkull;
        CenterMarkerAlignment = Enum.IsDefined(centerMarkerAlignment) ? centerMarkerAlignment : CenterMarkerAlignment.Left;
    }

    /// <summary>
    /// Gets whether the overlay is enabled.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Gets the selected visibility filter.
    /// </summary>
    public BossListVisibilityMode VisibilityMode { get; }
    public OverlayAppearance Appearance { get; }
    public string DefeatedColor { get; }
    public DefeatedBossTreatment DefeatedTreatment { get; }
    public bool ShowCheckmark { get; }
    public string CheckmarkAccent { get; }
    public int MaximumVisibleCount { get; }
    public bool ShowDefeatedSkull { get; }
    public CenterMarkerAlignment CenterMarkerAlignment { get; }
    private static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>
/// Holds immutable, validated overlay configuration. It models configuration
/// only; it does not generate tokens, bind ports, or perform I/O.
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
        OverlayEndpointConfiguration.Unassigned,
        TotalDeathsOverlayOptions.Default,
        BossListOverlayOptions.Default);

    /// <summary>
    /// Initializes validated overlay configuration.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the schema version is unsupported.</exception>
    public OverlayConfiguration(
        int schemaVersion,
        OverlayEndpointConfiguration endpoint,
        TotalDeathsOverlayOptions totalDeaths,
        BossListOverlayOptions bossList)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The overlay configuration schema version is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(totalDeaths);
        ArgumentNullException.ThrowIfNull(bossList);

        SchemaVersion = schemaVersion;
        Endpoint = endpoint;
        TotalDeaths = totalDeaths;
        BossList = bossList;
    }

    /// <summary>
    /// Gets the configuration schema version.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Gets the validated optional endpoint configuration.
    /// </summary>
    public OverlayEndpointConfiguration Endpoint { get; }

    /// <summary>
    /// Gets the Total Deaths overlay options.
    /// </summary>
    public TotalDeathsOverlayOptions TotalDeaths { get; }

    /// <summary>
    /// Gets the boss-list overlay options.
    /// </summary>
    public BossListOverlayOptions BossList { get; }
}
