using System.Windows.Threading;
using SoulsTracker.Application;

namespace SoulsTracker.Desktop;

/// <summary>Serializes committed selections and runtime fan-out on the UI dispatcher.</summary>
internal sealed class DesktopStateChangePublisher(
    Dispatcher dispatcher,
    RuntimePublicationSession runtimePublication,
    ITrackerStateChangePublisher outputs) : ITrackerStateChangePublisher
{
    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(() =>
        {
            // Observe the commit before any output sees it, rather than waiting
            // for the command's later ViewModel continuation to apply that state.
            runtimePublication.SelectState(notification.State);
            return outputs.PublishAsync(notification, cancellationToken);
        }).Task.Unwrap();
}
