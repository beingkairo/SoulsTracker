using System.ComponentModel;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Composes the desktop surface with the approved local state command path.</summary>
public partial class App : System.Windows.Application, IDisposable
{
    private readonly DesktopShutdownCoordinator shutdownCoordinator;
    private readonly DesktopStartupController singleInstanceStartup;
    private bool mainWindowCloseRequested;
    private bool finalShutdownRequested;

    public App()
    {
        singleInstanceStartup = new DesktopStartupController(new WindowsSingleInstanceLeaseFactory(),
            () => DesktopActivationPipe.RequestRestoreAsync(DesktopActivationPipe.ProductionName, TimeSpan.FromSeconds(2)));
        shutdownCoordinator = new DesktopShutdownCoordinator(
            stopInputsAsync: DisposeGlobalHotkeysAsync,
            drainProducersAsync: DisposeCoordinatorAsync,
            drainOutputsAsync: DisposeOutputsAsync,
            new DispatcherBoundDisposable(Dispatcher, singleInstanceStartup),
            cancelPending: () => runtimeReaderCancellation?.Cancel());
    }
}
