using SoulsTracker.Application;

namespace SoulsTracker.Overlay;

/// <summary>Forwards already-persisted state notifications to the local overlay without blocking tracker commits.</summary>
public sealed class OverlayStateChangePublisher : ITrackerStateChangePublisher
{
    private SecureOverlayService? service;

    public void Attach(SecureOverlayService overlayService) => service = overlayService ?? throw new ArgumentNullException(nameof(overlayService));

    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    {
        service?.Publish(notification.State);
        return Task.CompletedTask;
    }
}
