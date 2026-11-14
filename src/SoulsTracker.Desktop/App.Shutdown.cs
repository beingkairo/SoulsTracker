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
    private async Task ExitApplicationAsync()
    {
        if (mainWindowCloseRequested) return;
        mainWindowCloseRequested = true;
        try { await shutdownCoordinator.RequestApplicationShutdownAsync(FinalShutdown); }
        catch { /* Components have been awaited; final shutdown already ran. */ }
    }

    private void FinalShutdown()
    {
        finalShutdownRequested = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Dispose();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    public void Dispose()
    {
        shutdownCoordinator.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeCoordinatorAsync()
    {
        if (startupTask is not null)
        {
            try { await startupTask.ConfigureAwait(false); }
            catch { /* Failed startup still requires complete disposal of created owners. */ }
        }
        CancellationTokenSource? readerCancellation = runtimeReaderCancellation;
        Task? pollingTask = runtimeReaderPollingTask;
        try
        {
            // Stop polling before completing the publisher so its final callback
            // cannot enqueue work after the publisher has drained.
            readerCancellation?.Cancel();
            try
            {
                if (pollingTask is not null)
                {
                    await pollingTask.ConfigureAwait(false);
                }
            }
            catch
            {
                // A terminated reader must not skip draining committed producers.
            }
            finally
            {
                readerCancellation?.Dispose();
                runtimeReaderCancellation = null;
                runtimeReaderPollingTask = null;
                runtimeReaders = null;
                eldenRingSaveReader = null;
                blackMythWukongSaveReader = null;
                liesOfPSaveReader = null;
            }

            if (coordinator is not null)
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            coordinator = null;
        }
    }

    private async ValueTask DisposeOutputsAsync()
    {
        try
        {
            Task hosted = Dispatcher.CheckAccess()
                ? hostedConnection?.DisposeAsync().AsTask() ?? Task.CompletedTask
                : Dispatcher.InvokeAsync(() => hostedConnection?.DisposeAsync().AsTask() ?? Task.CompletedTask).Task.Unwrap();
            Task text = textExportPublisher?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            await Task.WhenAll(hosted, text).ConfigureAwait(false);
        }
        finally
        {
            hostedConnection = null;
            textExportPublisher = null;
            startupCancellation.Dispose();
        }
    }

    /// <summary>
    /// A named mutex may only be released by the thread that acquired it. Startup
    /// acquires the single-instance lease on the WPF dispatcher, while awaited
    /// shutdown work resumes on worker threads. Marshal just the final lease
    /// release back to that owning dispatcher after all components have stopped.
    /// </summary>
    private sealed class DispatcherBoundDisposable(Dispatcher dispatcher, IDisposable inner) : IDisposable
    {
        private readonly Dispatcher dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        private readonly IDisposable inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public void Dispose()
        {
            if (dispatcher.CheckAccess())
            {
                inner.Dispose();
                return;
            }

            dispatcher.Invoke(inner.Dispose);
        }
    }
}
