using System.ComponentModel;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public partial class App
{
    private DesktopTrayLifecycle? trayLifecycle;
    private WindowsTrayIcon? trayIcon;
    private bool pendingRestore;

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (trayLifecycle is { Exiting: false } lifecycle && !mainWindowCloseRequested)
        {
            lifecycle.Closing(e);
            return;
        }
        if (mainWindowCloseRequested)
        {
            e.Cancel = !finalShutdownRequested;
            return;
        }

        e.Cancel = true;
        await ExitApplicationAsync();
    }

    private void RestoreMainWindow()
    {
        if (mainWindowCloseRequested || MainWindow is not MainWindow window) return;
        pendingRestore = false;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Activate();
        window.RestoreTrayFocus();
    }

    private void TrayFailed(MainWindow window)
    {
        if (trayLifecycle is not null) trayLifecycle.TrayAvailable = false;
        RestoreMainWindow();
    }
}
