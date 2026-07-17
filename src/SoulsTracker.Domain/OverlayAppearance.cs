namespace SoulsTracker.Domain;

public enum OverlayTextAlignment { Left, Center, Right }
/// <summary>Hidden is retained only to deserialize older local settings and is normalized to Nothing.</summary>
public enum DefeatedBossTreatment { Nothing, Dimmed, Strikethrough, Both, Hidden }
public enum OverlayTitleIconMode { Off, PrefixSkull, SkullOnly }

/// <summary>Validated, bounded browser-safe appearance values. No arbitrary CSS is accepted.</summary>
public sealed class OverlayAppearance
{
    public static IReadOnlyList<string> AllowedFonts { get; } = ["Segoe UI", "Arial", "Verdana"];
    // Sanitized product defaults captured from the approved local visual setup.
    // This deliberately excludes stream state, endpoints, paths, tokens, and boss progress.
    public static OverlayAppearance Default { get; } = new("Total Deaths", "Arial", 24, "#F7F6FF", "#A78BFA", "#15171B", 0, 0, 0, OverlayTextAlignment.Left, outlineEnabled: true, outlineColor: "#000000", outlineWidth: 0);
    public static OverlayAppearance BossListDefault { get; } = new("Boss List", "Arial", 24, "#FFFFFF", "#A78BFA", "#15171B", 0, 0, 8, OverlayTextAlignment.Left, outlineEnabled: true, outlineColor: "#000000", outlineWidth: 1, shadowEnabled: true, shadowColor: "#000000", shadowOffsetX: 2, shadowOffsetY: 2, shadowBlur: 6);

    public OverlayAppearance(string title, string fontFamily, int fontSize, string textColor, string accentColor, string backgroundColor, int backgroundOpacity, int padding, int cornerRadius, OverlayTextAlignment alignment,
        bool outlineEnabled = false, string outlineColor = "#000000", int outlineWidth = 0,
        bool shadowEnabled = false, string shadowColor = "#000000", int shadowOffsetX = 2, int shadowOffsetY = 2, int shadowBlur = 4,
        int textOpacity = 100, string iconColor = "#FFFFFF")
    {
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        // A blank title is intentional: streamers can use a number-only death
        // counter or a title-less boss list.  Whitespace-only input normalizes to
        // that same compact representation rather than creating an empty row.
        title = title?.Trim() ?? throw new ArgumentNullException(nameof(title));
        if (title.Length > 40) throw new ArgumentException("An overlay title can be at most 40 characters.", nameof(title));
        if (!IsSafeFontFamily(fontFamily)) throw new ArgumentException("The font family must be a safe local font name.", nameof(fontFamily));
        if (fontSize is < 12 or > 96) throw new ArgumentOutOfRangeException(nameof(fontSize), "Font size must be between 12 and 96.");
        if (backgroundOpacity is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(backgroundOpacity), "Background opacity must be between 0 and 100.");
        if (padding is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(padding), "Boss row spacing must be between 0 and 64.");
        if (cornerRadius is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(cornerRadius), "Corner radius must be between 0 and 32.");
        if (outlineWidth is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(outlineWidth), "Outline width must be between 0 and 8 pixels.");
        if (shadowOffsetX is < -20 or > 20) throw new ArgumentOutOfRangeException(nameof(shadowOffsetX), "Shadow X offset must be between -20 and 20 pixels.");
        if (shadowOffsetY is < -20 or > 20) throw new ArgumentOutOfRangeException(nameof(shadowOffsetY), "Shadow Y offset must be between -20 and 20 pixels.");
        if (shadowBlur is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(shadowBlur), "Shadow blur must be between 0 and 20 pixels.");
        if (textOpacity is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(textOpacity), "Text opacity must be between 0 and 100.");
        if (!IsColor(textColor) || !IsColor(accentColor) || !IsColor(backgroundColor) || !IsColor(outlineColor) || !IsColor(shadowColor) || !IsColor(iconColor)) throw new ArgumentException("Colors must be #RRGGBB.");
        Title = title; FontFamily = fontFamily; FontSize = fontSize; TextColor = textColor; AccentColor = accentColor; BackgroundColor = backgroundColor; BackgroundOpacity = backgroundOpacity; Padding = padding; CornerRadius = cornerRadius; Alignment = alignment;
        OutlineEnabled = outlineEnabled; OutlineColor = outlineColor; OutlineWidth = outlineWidth;
        ShadowEnabled = shadowEnabled; ShadowColor = shadowColor; ShadowOffsetX = shadowOffsetX; ShadowOffsetY = shadowOffsetY; ShadowBlur = shadowBlur;
        TextOpacity = textOpacity; IconColor = iconColor;
    }
    public string Title { get; }
    public string FontFamily { get; }
    public int FontSize { get; }
    public string TextColor { get; }
    public string AccentColor { get; }
    public string BackgroundColor { get; }
    public int BackgroundOpacity { get; }
    public int Padding { get; }
    public int CornerRadius { get; }
    public OverlayTextAlignment Alignment { get; }
    public bool OutlineEnabled { get; }
    public string OutlineColor { get; }
    public int OutlineWidth { get; }
    public bool ShadowEnabled { get; }
    public string ShadowColor { get; }
    public int ShadowOffsetX { get; }
    public int ShadowOffsetY { get; }
    public int ShadowBlur { get; }
    public int TextOpacity { get; }
    /// <summary>Safe tint used for the Total Deaths title skull and Boss List marker.</summary>
    public string IconColor { get; }
    public OverlayAppearance WithAlignment(OverlayTextAlignment alignment) => new(Title, FontFamily, FontSize, TextColor, AccentColor, BackgroundColor, BackgroundOpacity, Padding, CornerRadius, alignment, OutlineEnabled, OutlineColor, OutlineWidth, ShadowEnabled, ShadowColor, ShadowOffsetX, ShadowOffsetY, ShadowBlur, TextOpacity, IconColor);
    private static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    private static bool IsSafeFontFamily(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(static c => char.IsControl(c) || c is ';' or '{' or '}' or '<' or '>' or '\'' or '"' or '\\');
}
