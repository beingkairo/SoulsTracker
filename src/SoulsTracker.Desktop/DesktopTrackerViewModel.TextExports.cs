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
    private string? textExportStatus;

    public string? TextExportStatus { get => textExportStatus; private set => SetField(ref textExportStatus, value); }
    public string? DeathsExportFileName => state?.TextExports.DeathsPath is { } path ? Path.GetFileName(path) : null;

    public bool IsDeathsExportEnabled => state?.TextExports.DeathsEnabled ?? false;

    public bool CanChooseDeathsExport => ControlsEnabled && IsDeathsExportEnabled;
    public bool CanClearDeathsExport => CanChooseDeathsExport && state?.TextExports.DeathsPath is not null;
    internal void SetTextExportStatus(bool succeeded) => TextExportStatus = succeeded ? "Text exports ready." : "Text export is unavailable.";
    public Task SetDeathsExportPathAsync(string path, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(path, IsDeathsExportEnabled), cancellationToken);
    public Task SetDeathsExportEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(state?.TextExports.DeathsPath, enabled), cancellationToken);
    public Task ClearDeathsExportAsync(CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(null, IsDeathsExportEnabled), cancellationToken);

    private void NotifyTextExportControlAvailability()
    {
        OnPropertyChanged(nameof(CanChooseDeathsExport));
        OnPropertyChanged(nameof(CanClearDeathsExport));

    }


    private async Task SaveExportsAsync(TextExportConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!ControlsEnabled) return;
        try { ApplyCommittedState(await coordinator.SetTextExportConfigurationAsync(configuration, cancellationToken)); }
        catch { ErrorMessage = "Text export settings could not be saved."; }
    }
}
