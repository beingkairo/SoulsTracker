namespace SoulsTracker.Domain;

public enum GameUiAvailability { Selectable, DisabledSoon }
public enum GameTrackingMode { GameLifetimeReadOnly, ManualOnly, Unavailable }
public enum ReaderBindingState { PendingVerification, IntentionallyUnavailable }

public sealed class GameDefinition
{
    public GameDefinition(GameId id, string displayName, GameUiAvailability uiAvailability, GameTrackingMode trackingMode, ReaderBindingState readerBindingState)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("The game display name cannot be blank.", nameof(displayName));
        if (uiAvailability == GameUiAvailability.DisabledSoon && (trackingMode != GameTrackingMode.Unavailable || readerBindingState != ReaderBindingState.IntentionallyUnavailable))
            throw new ArgumentException("A disabled SOON game must be unavailable and intentionally unbound.");
        if (id == GameId.DemonsSouls && (trackingMode != GameTrackingMode.ManualOnly || readerBindingState != ReaderBindingState.IntentionallyUnavailable))
            throw new ArgumentException("The manual console profile must remain manual-only.");
        Id = id; DisplayName = displayName; UiAvailability = uiAvailability; TrackingMode = trackingMode; ReaderBindingState = readerBindingState;
    }
    public GameId Id { get; }
    public string DisplayName { get; }
    public GameUiAvailability UiAvailability { get; }
    public GameTrackingMode TrackingMode { get; }
    public ReaderBindingState ReaderBindingState { get; }
    public bool IsSelectable => UiAvailability == GameUiAvailability.Selectable;
}
