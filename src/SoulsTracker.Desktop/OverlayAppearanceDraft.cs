using System.ComponentModel;
using System.Runtime.CompilerServices;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

/// <summary>Editable, notifying presentation draft. Conversion remains bounded by the domain contract.</summary>
public sealed class OverlayAppearanceDraft : INotifyPropertyChanged
{
    private string title = "TOTAL DEATHS", fontFamily = "Segoe UI", fontSize = "42", textColor = "#FFFFFF", accentColor = "#A78BFA", backgroundColor = "#15171B", backgroundOpacity = "88", padding = "16", cornerRadius = "8", outlineColor = "#000000", outlineWidth = "0", shadowColor = "#000000", shadowOffsetX = "2", shadowOffsetY = "2", shadowBlur = "4", textOpacity = "100", iconColor = "#FFFFFF";
    private bool outlineEnabled, shadowEnabled;
    private string? validationMessage, invalidField;
    private OverlayTextAlignment alignment = OverlayTextAlignment.Left;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get => title; set => Set(ref title, value); }
    public string FontFamily { get => fontFamily; set => Set(ref fontFamily, value); }
    public string FontSize { get => fontSize; set => Set(ref fontSize, value); }
    public string TextColor { get => textColor; set => Set(ref textColor, value); }
    public string AccentColor { get => accentColor; set => Set(ref accentColor, value); }
    public string BackgroundColor { get => backgroundColor; set => Set(ref backgroundColor, value); }
    public string BackgroundOpacity { get => backgroundOpacity; set => Set(ref backgroundOpacity, value); }
    public string Padding { get => padding; set => Set(ref padding, value); }
    public string CornerRadius { get => cornerRadius; set => Set(ref cornerRadius, value); }
    public OverlayTextAlignment Alignment { get => alignment; set => Set(ref alignment, value); }
    public bool OutlineEnabled { get => outlineEnabled; set => Set(ref outlineEnabled, value); }
    public string OutlineColor { get => outlineColor; set => Set(ref outlineColor, value); }
    public string OutlineWidth { get => outlineWidth; set => Set(ref outlineWidth, value); }
    public bool ShadowEnabled { get => shadowEnabled; set => Set(ref shadowEnabled, value); }
    public string ShadowColor { get => shadowColor; set => Set(ref shadowColor, value); }
    public string ShadowOffsetX { get => shadowOffsetX; set => Set(ref shadowOffsetX, value); }
    public string ShadowOffsetY { get => shadowOffsetY; set => Set(ref shadowOffsetY, value); }
    public string ShadowBlur { get => shadowBlur; set => Set(ref shadowBlur, value); }
    public string TextOpacity { get => textOpacity; set => Set(ref textOpacity, value); }
    public string IconColor { get => iconColor; set => Set(ref iconColor, value); }
    public string? ValidationMessage { get => validationMessage; private set => Set(ref validationMessage, value); }
    public string? InvalidField
    {
        get => invalidField;
        private set
        {
            if (EqualityComparer<string?>.Default.Equals(invalidField, value)) return;
            invalidField = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InvalidField)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOutlineWidthInvalid)));
        }
    }
    public bool IsOutlineWidthInvalid => string.Equals(InvalidField, "outlineWidth", StringComparison.Ordinal);
    public void Load(OverlayAppearance appearance) { Title = appearance.Title; FontFamily = appearance.FontFamily; FontSize = appearance.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture); TextColor = appearance.TextColor; AccentColor = appearance.AccentColor; BackgroundColor = appearance.BackgroundColor; BackgroundOpacity = appearance.BackgroundOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture); Padding = appearance.Padding.ToString(System.Globalization.CultureInfo.InvariantCulture); CornerRadius = appearance.CornerRadius.ToString(System.Globalization.CultureInfo.InvariantCulture); Alignment = appearance.Alignment; OutlineEnabled = appearance.OutlineEnabled; OutlineColor = appearance.OutlineColor; OutlineWidth = appearance.OutlineWidth.ToString(System.Globalization.CultureInfo.InvariantCulture); ShadowEnabled = appearance.ShadowEnabled; ShadowColor = appearance.ShadowColor; ShadowOffsetX = appearance.ShadowOffsetX.ToString(System.Globalization.CultureInfo.InvariantCulture); ShadowOffsetY = appearance.ShadowOffsetY.ToString(System.Globalization.CultureInfo.InvariantCulture); ShadowBlur = appearance.ShadowBlur.ToString(System.Globalization.CultureInfo.InvariantCulture); TextOpacity = appearance.TextOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture); IconColor = appearance.IconColor; }
    public OverlayAppearance ToDomain(OverlayTextAlignment forcedAlignment)
    {
        ClearValidation();
        try
        {
            return new(Title, FontFamily, Parse(FontSize, "fontSize"), TextColor, AccentColor, BackgroundColor, Parse(BackgroundOpacity, "backgroundOpacity"), Parse(Padding, "padding"), Parse(CornerRadius, "cornerRadius"), forcedAlignment, OutlineEnabled, OutlineColor, Parse(OutlineWidth, "outlineWidth"), ShadowEnabled, ShadowColor, Parse(ShadowOffsetX, "shadowOffsetX"), Parse(ShadowOffsetY, "shadowOffsetY"), Parse(ShadowBlur, "shadowBlur"), Parse(TextOpacity, "textOpacity"), IconColor);
        }
        catch (ArgumentException exception)
        {
            InvalidField = exception.ParamName;
            ValidationMessage = exception.Message.Split(Environment.NewLine)[0];
            throw;
        }
    }
    public void ClearValidation() { InvalidField = null; ValidationMessage = null; }
    private static int Parse(string value, string field) => int.TryParse(value, out int parsed) ? parsed : throw new ArgumentException("Enter a whole number.", field);
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}
