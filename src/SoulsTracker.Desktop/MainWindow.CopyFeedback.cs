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

    private Action ScheduleCopyExpiry(TimeSpan delay, Action expire)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = delay };
        EventHandler tick = (_, _) => expire();
        timer.Tick += tick;
        timer.Start();
        return () => { timer.Stop(); timer.Tick -= tick; };
    }

    private void CopyDirectoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path } anchor || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            copyDirectoryPath(path);
            ShowCopyFeedback("Directory path copied", success: true);
        }
        catch { ShowCopyFeedback("The directory path could not be copied. Try again.", success: false); }
        if (!copyFeedbackClosed) directoryFeedback?.Show(anchor, MainContentScrollViewer);
    }

    private void ShowCopyFeedback(string message, bool success, bool hosted = false)
    {
        if (copyFeedbackClosed) return;
        ClearCopyFeedback();
        hostedCopyFeedbackVisible = hosted;
        var cue = (System.Windows.Media.Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        if (hosted)
        {
            HostedCopyStatus.Text = message;
            HostedCopyStatus.Foreground = cue;
            HostedCopyFeedbackOverlay.BorderBrush = cue;
            HostedCopyFeedbackOverlay.Visibility = Visibility.Visible;
            hostedFeedback?.Show(CopyTotalDeathsOverlayUrlButton, OverlayConfigurationScrollViewer);
        }
        else
        {
            DirectoryCopyStatus.Text = message;
            DirectoryCopyStatus.Foreground = cue;
            CopyFeedbackOverlay.BorderBrush = cue;
            CopyFeedbackOverlay.Visibility = Visibility.Visible;
        }
        if (success)
        {
            long version = copyFeedbackVersion;
            cancelCopyExpiry = scheduleCopyExpiry(TimeSpan.FromSeconds(5), () =>
            {
                if (!copyFeedbackClosed && version == copyFeedbackVersion) ClearCopyFeedback();
            });
        }
    }

    private void ClearCopyFeedback()
    {
        copyFeedbackVersion++;
        cancelCopyExpiry?.Invoke();
        cancelCopyExpiry = null;
        hostedCopyFeedbackVisible = false;
        DirectoryCopyStatus.Text = string.Empty;
        HostedCopyStatus.Text = string.Empty;
        directoryFeedback?.Hide();
        hostedFeedback?.Hide();
        CopyFeedbackOverlay.Visibility = Visibility.Collapsed;
        HostedCopyFeedbackOverlay.Visibility = Visibility.Collapsed;
    }

    private void ObserveHostedCopyFeedback()
    {
        var connection = directoryCopyViewModel?.HostedOverlay;
        if (ReferenceEquals(copyFeedbackConnection, connection)) return;
        if (copyFeedbackConnection is not null) copyFeedbackConnection.PropertyChanged -= HostedCopyFeedback_PropertyChanged;
        if (hostedCopyFeedbackVisible) ClearCopyFeedback();
        copyFeedbackConnection = connection;
        // Rebinding never revives an old copy notification.
        lastHostedCopyFeedback = connection?.CopyFeedbackText ?? string.Empty;
        if (connection is not null) connection.PropertyChanged += HostedCopyFeedback_PropertyChanged;
    }

    private void HostedCopyFeedback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (copyFeedbackClosed || sender is not HostedOverlayConnection connection || !ReferenceEquals(connection, copyFeedbackConnection)) return;
        string message = connection.CopyFeedbackText;
        if (message == lastHostedCopyFeedback) return;
        lastHostedCopyFeedback = message;
        if (message.Length == 0)
        {
            if (hostedCopyFeedbackVisible) ClearCopyFeedback();
        }
        else ShowCopyFeedback(message, message == "URL copied", hosted: true);
    }
}
