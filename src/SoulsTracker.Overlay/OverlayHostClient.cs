using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Overlay;

/// <summary>Desktop-side overlay state forwarder for the optional persistent host.</summary>
public sealed class OverlayHostClient : IOverlayStateSink, IAsyncDisposable
{
    private readonly IOverlayEndpointAccess endpoint;
    private readonly object synchronization = new();
    private PersistentTrackerState? state;
    private RuntimeGameObservation? observation;
    private long sequence;

    public OverlayHostClient(IOverlayEndpointAccess endpoint) => this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public void Publish(PersistentTrackerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (synchronization)
        {
            this.state = state;
            if (!OverlaySnapshotFactory.CanUseRuntimeObservation(state, observation)) observation = null;
            SendCurrent();
        }
    }

    public void PublishRuntimeObservation(RuntimeGameObservation? observation)
    {
        lock (synchronization)
        {
            if (state is null) return;
            this.observation = OverlaySnapshotFactory.CanUseRuntimeObservation(state, observation) ? observation : null;
            SendCurrent();
        }
    }

    public async Task<bool> ConnectAsync(PersistentTrackerState initialState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        lock (synchronization) state = initialState;
        OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(initialState, null, Interlocked.Increment(ref sequence));
        return await OverlayHostPipe.SendSnapshotAsync(endpoint, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private void SendCurrent()
    {
        if (state is null) return;
        OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, observation, Interlocked.Increment(ref sequence));
        _ = OverlayHostPipe.SendSnapshotAsync(endpoint, snapshot);
    }

    public async ValueTask DisposeAsync() => await OverlayHostPipe.ClearAsync(endpoint).ConfigureAwait(false);
}
