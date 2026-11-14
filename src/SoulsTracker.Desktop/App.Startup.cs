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
    private DesktopActivationPipe? activationPipe;
    private Task? startupTask;
    private readonly CancellationTokenSource startupCancellation = new();
    private DesktopDataRootSelection? dataRootSelection;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            dataRootSelection = DesktopDataRootResolver.Resolve(
                e.Args,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            DesktopStartupDecision decision = await singleInstanceStartup.StartAsync(StartTrackerAsync);
            if (decision.CanStart)
            {
                return;
            }
            if (decision.Status == DesktopStartupStatus.ActivatedExisting)
            {
                Shutdown();
                return;
            }

            System.Windows.MessageBox.Show(decision.UserMessage!, "SoulsTracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
        catch (ArgumentException)
        {
            System.Windows.MessageBox.Show(
                "SoulsTracker could not start with the requested development data folder.",
                "SoulsTracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
        catch
        {
            mainWindowCloseRequested = true;
            await shutdownCoordinator.RequestApplicationShutdownAsync(FinalShutdown);
        }
    }

    private Task StartTrackerAsync()
    {
        try
        {
            activationPipe = new DesktopActivationPipe(DesktopActivationPipe.ProductionName,
                () => _ = Dispatcher.BeginInvoke(() =>
                {
                    pendingRestore = true;
                    RestoreMainWindow();
                }));
            activationPipe.Start();
        }
        catch
        {
            activationPipe?.Dispose();
            activationPipe = null;
            // The mutex still prevents duplicate trackers. The window remains usable.
        }
        return startupTask = StartTrackerCoreAsync();
    }
}
