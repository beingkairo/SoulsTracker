using SoulsTracker.Application;

namespace SoulsTracker.Desktop;

/// <summary>Provides the desktop-only no-op notification sink until overlay transport is introduced.</summary>
internal sealed class NullTrackerStateChangePublisher : ITrackerStateChangePublisher
{
    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
