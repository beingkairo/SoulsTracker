using SoulsTracker.Application;

namespace SoulsTracker.Overlay;

/// <summary>Forwards already-persisted state notifications to the local overlay without blocking tracker commits.</summary>
public sealed class OverlayStateChangePublisher : ITrackerStateChangePublisher
{
    private IOverlayStateSink? service;

    public void Attach(IOverlayStateSink overlayService) => service = overlayService ?? throw new ArgumentNullException(nameof(overlayService));

    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    {
        service?.Publish(notification.State);
        if (notification.CommandType == TrackerCommandType.UpdateEldenRingSaveConfiguration)
        {
            service?.PublishRuntimeObservation(null);
        }
        return Task.CompletedTask;
    }
}
