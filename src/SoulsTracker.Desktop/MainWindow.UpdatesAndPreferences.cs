using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Navigation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.Win32;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Hosts the P3-01 manual tracking surface.</summary>
public partial class MainWindow : Window
{
    private static Microsoft.Win32.SaveFileDialog CreateTextExportDialog() => new() { Filter = "Text files (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true, OverwritePrompt = false };
    private async void ChooseDeathsExport_Click(object sender, RoutedEventArgs e) { var dialog = CreateTextExportDialog(); if (dialog.ShowDialog(this) == true && DataContext is DesktopTrackerViewModel viewModel) await viewModel.SetDeathsExportPathAsync(dialog.FileName); }
    private async void ClearDeathsExport_Click(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ClearDeathsExportAsync(); }
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.CheckForUpdatesAsync(); }
    private async void CheckForUpdatesOnStartup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && sender is System.Windows.Controls.CheckBox checkBox)
        {
            if (viewModel.IsSavingUpdatePreference)
            {
                checkBox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, viewModel.PendingUpdatePreferenceValue);
                return;
            }
            await viewModel.SetCheckForUpdatesOnStartupAsync(checkBox.IsChecked == true);
            if (!string.IsNullOrEmpty(viewModel.UpdatePreferenceStatus))
            {
                // Let the failure binding expand before asking the Settings scroller
                // to reveal it. Keep keyboard focus on the restored checkbox.
                await Dispatcher.InvokeAsync(() =>
                {
                    if (UpdatePreferenceStatusTextBlock.IsVisible)
                    {
                        UpdatePreferenceStatusTextBlock.UpdateLayout();
                        UpdatePreferenceStatusTextBlock.BringIntoView();
                    }
                }, DispatcherPriority.Loaded);
            }
        }
    }

    private async void MinimizeToTrayOnClose_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && sender is System.Windows.Controls.CheckBox checkBox)
        {
            if (viewModel.IsSavingTrayClosePreference)
            {
                checkBox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, viewModel.MinimizeToTrayOnClose);
                return;
            }
            await viewModel.SetMinimizeToTrayOnCloseAsync(checkBox.IsChecked == true);
        }
    }

    private void OpenUpdateReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.OpenAvailableUpdateReleasePage();
    }
    // These controls intentionally use one-way bindings: committed state remains
    // the source of truth after asynchronous persistence completes. Do not gate a
    // routed toggle event on the currently committed value, though. A second user
    // interaction can arrive before the first save returns, and stale state would
    // otherwise drop that interaction (and make the checkbox visibly flicker).
    private async void DeathsExportEnabled_Checked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetDeathsExportEnabledAsync(true); }
    private async void DeathsExportEnabled_Unchecked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetDeathsExportEnabledAsync(false); }

    private async void ReviewLegacyImport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { LegacyImport: not null } viewModel && sender is System.Windows.Controls.Button { Tag: SoulsTracker.Infrastructure.LegacyImportCandidate candidate })
        {
            await viewModel.LegacyImport.ReviewAsync(candidate);
        }
    }

    private async void ImportReviewedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { LegacyImport: not null } viewModel) await viewModel.LegacyImport.ImportReviewedSettingsAsync();
    }

    private void CancelLegacyImport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { LegacyImport: not null } viewModel) viewModel.LegacyImport.Cancel();
    }

    private void BeingKairoAttributionHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Attribution is optional and must never affect local tracking when no browser can launch.
        }

        e.Handled = true;
    }
}
