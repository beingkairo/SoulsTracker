using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>
/// Test-composed hosted output. Committed notifications run behind DesktopStateChangePublisher;
/// runtime calls run only at RuntimePublicationSession.publishOutputs, on the same dispatcher.
/// </summary>
internal sealed class HostedDesktopPublisher : ITrackerStateChangePublisher, IAsyncDisposable
{
    private readonly HostedOverlayPublisher sender;
    private readonly HostedOverlayProjection projection = new();
    private PersistentTrackerState state;
    private RuntimeGameReadResult? acceptedPresentation;
    private bool stopped;

    internal HostedDesktopPublisher(HostedOverlayPublisher sender, PersistentTrackerState initial)
    {
        this.sender = sender;
        state = initial;
        sender.Offer(projection.Initialize(initial));
    }

    internal void PublishAccepted(PersistentTrackerState current, RuntimeGameReadResult? accepted)
    {
        if (stopped) return;
        state = current;
        acceptedPresentation = accepted;
        sender.Offer(projection.FromAcceptedPublication(current, accepted));
    }

    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    {
        if (stopped || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        var next = notification.State;
        bool sourceChanged = EffectiveDeathTotalResult.SourceIdentityFor(state) != EffectiveDeathTotalResult.SourceIdentityFor(next);
        state = next;
        if (sourceChanged)
        {
            acceptedPresentation = null;
            sender.Offer(projection.FromExplicitSourceChange(next));
        }
        else sender.Offer(projection.FromAcceptedPublication(next, acceptedPresentation));
        return Task.CompletedTask;
    }

    // Owner stops and drains runtime/commit producers before disposing the sender.
    internal void StopOffering() => stopped = true;
    public ValueTask DisposeAsync()
    {
        StopOffering();
        return sender.DisposeAsync();
    }
}
