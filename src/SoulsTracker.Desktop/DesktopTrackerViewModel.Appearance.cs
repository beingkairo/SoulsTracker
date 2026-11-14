using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Net.Http;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{

    private bool isTotalDeathsOverlayEnabled;
    private bool showTotalDeathsGameName;
    private OverlayTitleIconModeChoice draftTitleIconModeChoice = OverlayTitleIconModeChoice.All[0];

    private string? totalDeathsAppearanceStatus;


    private bool legacyDraftShowGameName;
    private bool legacyDraftCompactTitle = true;
    public IReadOnlyList<OverlayTextAlignment> OverlayAlignments { get; } = Enum.GetValues<OverlayTextAlignment>();
    public IReadOnlyList<string> LocalFontFamilies { get; } = GetLocalFontFamilies();

    private static string[] GetLocalFontFamilies()
    {
        try { return Fonts.SystemFontFamilies.Select(static font => font.Source).Where(static name => !string.IsNullOrWhiteSpace(name)).Append("Segoe UI").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return ["Segoe UI"]; }
    }
    public OverlayAppearanceDraft TotalDeathsAppearanceDraft { get; } = new();

    // Legacy test/binding compatibility; V1 no longer exposes editable controls for these choices.
    public bool DraftShowGameName { get => legacyDraftShowGameName; set => SetField(ref legacyDraftShowGameName, value); }
    public bool DraftCompactTitle { get => legacyDraftCompactTitle; set => SetField(ref legacyDraftCompactTitle, value); }
    public IReadOnlyList<OverlayTitleIconModeChoice> TitleIconModes { get; } = OverlayTitleIconModeChoice.All;
    public OverlayTitleIconModeChoice DraftTitleIconModeChoice { get => draftTitleIconModeChoice; set { if (SetField(ref draftTitleIconModeChoice, value)) { OnPropertyChanged(nameof(IsTitleIconSelected)); OnPropertyChanged(nameof(IsAppearanceTitleVisible)); } } }

    public bool IsAppearanceTitleVisible => DraftTitleIconModeChoice.Value != OverlayTitleIconMode.SkullOnly;

    public bool IsTitleIconSelected => DraftTitleIconModeChoice.Value != OverlayTitleIconMode.Off;
    public string DraftCheckmarkAccent { get; set; } = "#A78BFA";
    public string DraftMaximumVisibleCount { get; set; } = "25";

    public bool IsTotalDeathsOverlayEnabled { get => isTotalDeathsOverlayEnabled; private set => SetField(ref isTotalDeathsOverlayEnabled, value); }
    public bool ShowTotalDeathsGameName { get => showTotalDeathsGameName; private set => SetField(ref showTotalDeathsGameName, value); }
    public string? TotalDeathsAppearanceStatus { get => totalDeathsAppearanceStatus; private set => SetField(ref totalDeathsAppearanceStatus, value); }
    private bool isAppearanceApplySuccessful;
    public bool IsAppearanceApplySuccessful { get => isAppearanceApplySuccessful; private set => SetField(ref isAppearanceApplySuccessful, value); }


    public Task SetTotalDeathsOverlayEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled || isEnabled == IsTotalDeathsOverlayEnabled
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(isEnabled, ShowTotalDeathsGameName, cancellationToken);

    public Task SetShowTotalDeathsGameNameAsync(bool showGameName, CancellationToken cancellationToken = default) =>
        !CanConfigureTotalDeathsGameName || showGameName == ShowTotalDeathsGameName
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(IsTotalDeathsOverlayEnabled, showGameName, cancellationToken);


    public Task ResetOverlayAppearanceAsync(bool totalDeaths, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled ? Task.CompletedTask : SubmitAsync(new ResetOverlayAppearanceCommand(totalDeaths), cancellationToken);

    public async Task ApplyOverlayAppearanceAsync(bool totalDeaths, CancellationToken cancellationToken = default)
    {
        if (!PresentationControlsEnabled || state is null) return;
        IsAppearanceApplySuccessful = false;
        try
        {
            OverlayAppearance appearance = totalDeaths
                ? TotalDeathsAppearanceDraft.ToDomain(OverlayTextAlignment.Left)
                : TotalDeathsAppearanceDraft.ToDomain(OverlayTextAlignment.Left);
            // The selector is the sole source of truth. Legacy booleans are projected only for compatibility.

            // Appearance drafts are isolated from Main-tab operational errors.
            // A failed Apply must leave the last applied style/URL/preview intact.
            await SubmitAsync(new UpdateOverlayAppearanceCommand(totalDeaths, appearance, false, false, DraftTitleIconModeChoice.Value), cancellationToken);
            IsAppearanceApplySuccessful = ErrorMessage is null;
            SetAppearanceFeedback(totalDeaths, ErrorMessage is null
                ? "Total Deaths appearance applied."
                : "Total Deaths appearance could not be applied.");
        }
        catch (ArgumentException exception)
        {
            OverlayAppearanceDraft draft = TotalDeathsAppearanceDraft;
            string detail = draft.ValidationMessage ?? exception.Message.Split(Environment.NewLine)[0];
            SetAppearanceFeedback(totalDeaths, detail);
            return;
        }
    }

    private string ResolveLocalFont(string fontFamily) => LocalFontFamilies.Contains(fontFamily, StringComparer.OrdinalIgnoreCase) ? fontFamily : "Segoe UI";


    private void SetAppearanceFeedback(bool totalDeaths, string message)
    {
        if (totalDeaths) TotalDeathsAppearanceStatus = message;
        _ = ClearAppearanceFeedbackAfterDelayAsync(totalDeaths, message);
    }

    private async Task ClearAppearanceFeedbackAfterDelayAsync(bool totalDeaths, string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (totalDeaths && TotalDeathsAppearanceStatus == message) TotalDeathsAppearanceStatus = null;

    }
}
