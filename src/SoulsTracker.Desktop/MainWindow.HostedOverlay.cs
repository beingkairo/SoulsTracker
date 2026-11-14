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


    private void CopyTotalDeathsOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopTrackerViewModel { HostedOverlay: { CanCopy: true } connection }) return;
        string previous = lastHostedCopyFeedback;
        bool success = connection.CopyReadUrl(copyDirectoryPath);
        // Repeated identical copies still reset the same notification's lifetime.
        if (previous == lastHostedCopyFeedback) ShowCopyFeedback(connection.CopyFeedbackText, success, hosted: true);
    }

    private async void RetryHostedOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { HostedOverlay: { } connection }) await connection.RetryAsync();
    }
    private async void HostedOverlayEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { HostedOverlay: { } connection } &&
            sender is System.Windows.Controls.CheckBox checkBox)
        {
            await connection.SetEnabledAsync(checkBox.IsChecked == true);
            checkBox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, connection.IsEnabled);
        }
    }

    private static void CopyOverlayUrl(System.Windows.Controls.Button? button, string? value)
    {
        if (button is null || string.IsNullOrWhiteSpace(value)) return;
        System.Windows.Clipboard.SetText(value);
        button.Content = "Copied";
        System.Windows.Automation.AutomationProperties.SetHelpText(button, "URL copied.");
        var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        reset.Tick += (_, _) => { reset.Stop(); button.Content = "Copy"; System.Windows.Automation.AutomationProperties.SetHelpText(button, "Copy URL."); };
        reset.Start();
    }
}
