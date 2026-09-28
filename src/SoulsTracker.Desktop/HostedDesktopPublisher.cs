using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>
/// Hosted output. Committed notifications run behind DesktopStateChangePublisher;
/// runtime calls run only at RuntimePublicationSession.publishOutputs, on the same dispatcher.
/// </summary>
internal sealed class HostedDesktopPublisher : ITrackerStateChangePublisher, IAsyncDisposable
{
    private HostedOverlayPublisher? sender;
    private HostedOverlayEnvelope latest;
    private readonly HostedOverlayProjection projection = new();
    private PersistentTrackerState state;
    private RuntimeGameReadResult? acceptedPresentation;
    private bool stopped;

    internal HostedDesktopPublisher(HostedOverlayPublisher? sender, PersistentTrackerState initial)
    {
        this.sender = sender;
        state = initial;
        latest = projection.Initialize(initial);
        sender?.Offer(latest);
    }

    internal void Attach(HostedOverlayPublisher? next)
    {
        if (!stopped) next?.Offer(latest);
        sender = next;
    }

    private void Offer(HostedOverlayEnvelope envelope)
    {
        latest = envelope;
        sender?.Offer(envelope);
    }

    internal void PublishAccepted(PersistentTrackerState current, RuntimeGameReadResult? accepted)
    {
        if (stopped) return;
        state = current;
        acceptedPresentation = accepted;
        Offer(projection.FromAcceptedPublication(current, accepted));
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
            Offer(projection.FromExplicitSourceChange(next));
        }
        else Offer(projection.FromAcceptedPublication(next, acceptedPresentation));
        return Task.CompletedTask;
    }

    // Owner stops and drains runtime/commit producers before disposing the sender.
    internal void StopOffering() => stopped = true;
    public ValueTask DisposeAsync()
    {
        StopOffering();
        return sender?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
