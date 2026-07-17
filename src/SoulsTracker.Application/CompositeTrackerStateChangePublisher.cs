namespace SoulsTracker.Application;

public sealed class CompositeTrackerStateChangePublisher(params ITrackerStateChangePublisher[] publishers) : ITrackerStateChangePublisher
{
    private readonly ITrackerStateChangePublisher[] publishers = publishers ?? throw new ArgumentNullException(nameof(publishers));
    public async Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    {
        foreach (ITrackerStateChangePublisher publisher in publishers) await publisher.PublishAsync(notification, cancellationToken).ConfigureAwait(false);
    }
}
