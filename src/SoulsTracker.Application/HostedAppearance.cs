using SoulsTracker.Domain;
using System.Text.Json;

namespace SoulsTracker.Application;

/// <summary>Complete active appearance only. Layout is always compact and left aligned.</summary>
public sealed record HostedAppearance
{
    public required string Revision { get; init; }
    public required bool Enabled { get; init; }
    public required string Title { get; init; }
    public required string FontFamily { get; init; }
    public required int FontSize { get; init; }
    public required string TextColor { get; init; }
    public required int TextOpacity { get; init; }
    public required string BackgroundColor { get; init; }
    public required int BackgroundOpacity { get; init; }
    public required int Padding { get; init; }
    public required int CornerRadius { get; init; }
    public required bool OutlineEnabled { get; init; }
    public required string OutlineColor { get; init; }
    public required int OutlineWidth { get; init; }
    public required bool ShadowEnabled { get; init; }
    public required string ShadowColor { get; init; }
    public required int ShadowOffsetX { get; init; }
    public required int ShadowOffsetY { get; init; }
    public required int ShadowBlur { get; init; }
    public required string TitleIconMode { get; init; }
    public required string IconColor { get; init; }

    public static HostedAppearance From(OverlayPresentationConfiguration presentation, string revision)
    {
        OverlayAppearance a = presentation.TotalDeathsAppearance;
        return new HostedAppearance
        {
            Revision = revision,
            // Hosted Total Deaths remains visible now that the visibility control is gone.
            Enabled = true,
            Title = a.Title,
            FontFamily = a.FontFamily,
            FontSize = a.FontSize,
            TextColor = a.TextColor,
            TextOpacity = a.TextOpacity,
            BackgroundColor = a.BackgroundColor,
            BackgroundOpacity = a.BackgroundOpacity,
            Padding = a.Padding,
            CornerRadius = a.CornerRadius,
            OutlineEnabled = a.OutlineEnabled,
            OutlineColor = a.OutlineColor,
            OutlineWidth = a.OutlineWidth,
            ShadowEnabled = a.ShadowEnabled,
            ShadowColor = a.ShadowColor,
            ShadowOffsetX = a.ShadowOffsetX,
            ShadowOffsetY = a.ShadowOffsetY,
            ShadowBlur = a.ShadowBlur,
            TitleIconMode = presentation.TotalDeathsTitleIconMode switch
            {
                OverlayTitleIconMode.Off => "off",
                OverlayTitleIconMode.PrefixSkull => "prefixSkull",
                OverlayTitleIconMode.SkullOnly => "skullOnly",
                _ => throw new JsonException("Unsupported icon mode."),
            },
            IconColor = a.IconColor,
        }.Normalize();
    }

    internal HostedAppearance Normalize()
    {
        HostedOverlayJson.ValidateDecimal(Revision);
        if (Title is null || Title.IndexOfAny(['<', '>']) >= 0 ||
            FontFamily is null || FontFamily.IndexOfAny([':', '(', ')']) >= 0 ||
            TitleIconMode is not ("off" or "prefixSkull" or "skullOnly")) throw new JsonException("Unsafe appearance.");
        // Reject before JSON serialization can silently replace malformed UTF-16.
        ValidateUnicode(Title);
        ValidateUnicode(FontFamily);
        try
        {
            // Domain is the range/default authority; the wire additionally excludes HTML and URLs.
            var appearance = new OverlayAppearance(Title, FontFamily, FontSize, TextColor, "#000000",
                BackgroundColor, BackgroundOpacity, Padding, CornerRadius, OverlayTextAlignment.Left,
                OutlineEnabled, OutlineColor, OutlineWidth, ShadowEnabled, ShadowColor,
                ShadowOffsetX, ShadowOffsetY, ShadowBlur, TextOpacity, IconColor);
            return this with
            {
                Title = appearance.Title,
                TextColor = TextColor.ToUpperInvariant(),
                BackgroundColor = BackgroundColor.ToUpperInvariant(),
                OutlineColor = OutlineColor.ToUpperInvariant(),
                ShadowColor = ShadowColor.ToUpperInvariant(),
                IconColor = IconColor.ToUpperInvariant(),
            };
        }
        catch (ArgumentException) { throw new JsonException("Invalid appearance."); }
    }

    private static void ValidateUnicode(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1]))
                throw new JsonException("Invalid Unicode appearance.");
            i++;
        }
    }
}
