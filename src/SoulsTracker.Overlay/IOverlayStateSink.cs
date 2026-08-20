using SoulsTracker.Domain;

namespace SoulsTracker.Overlay;

/// <summary>Receives secret-free overlay state from the desktop process.</summary>
public interface IOverlayStateSink
{
    void Publish(PersistentTrackerState state);
    void PublishRuntimeObservation(RuntimeGameObservation? observation);
}
