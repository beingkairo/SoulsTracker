using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

/// <summary>Presentation-only last-valid fields. Never submits or mutates the editor draft.</summary>
public sealed class AppearancePreviewModel
{
    private readonly OverlayAppearanceDraft draft;
    private readonly OverlayAppearanceDraft valid = new();
    private static readonly PropertyInfo[] Fields = typeof(OverlayAppearanceDraft).GetProperties()
        .Where(p => p.SetMethod?.IsPublic == true && p.Name != nameof(OverlayAppearanceDraft.Alignment)).ToArray();

    public AppearancePreviewModel(OverlayAppearanceDraft draft, OverlayAppearance applied)
    {
        this.draft = draft;
        valid.Load(OverlayAppearance.Default);
        var initial = new OverlayAppearanceDraft();
        initial.Load(applied);
        UpdateFields(initial, OverlayTitleIconMode.Off);
        Appearance = Project(OverlayTitleIconMode.Off);
    }

    public HostedAppearance Appearance { get; private set; }
    public string Value { get; private set; } = "123";
    public bool IsRepresentative { get; private set; } = true;

    public void Update(OverlayTitleIconMode mode, string displayValue)
    {
        UpdateFields(draft, mode);
        Appearance = Project(mode);
        IsRepresentative = !long.TryParse(displayValue, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number < 0;
        Value = IsRepresentative ? "123" : number.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateFields(OverlayAppearanceDraft candidate, OverlayTitleIconMode mode)
    {
        foreach (var field in Fields)
        {
            if (candidate[field.Name].Length != 0) continue;
            object? previous = field.GetValue(valid);
            object? next = field.GetValue(candidate);
            if (Equals(previous, next)) continue;
            string opacity = valid.BackgroundOpacity;
            field.SetValue(valid, next);
            try { _ = Project(mode); }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                field.SetValue(valid, previous);
                valid.BackgroundOpacity = opacity;
            }
        }
    }

    private HostedAppearance Project(OverlayTitleIconMode mode) => HostedAppearance.From(
        new OverlayPresentationConfiguration(true, false, true, mode, valid.ToDomain(OverlayTextAlignment.Left)), "0");
}
