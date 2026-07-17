using SoulsTracker.Domain;

namespace SoulsTracker.Application;

public enum TrackerStateLoadFailureKind { None, Corrupt, UnsupportedVersion, Integrity, Unreadable }

public sealed record TrackerStateLoadResult(PersistentTrackerState? State, TrackerStateLoadFailureKind FailureKind, string? ActionableMessage)
{
    public bool IsSuccess => State is not null && FailureKind == TrackerStateLoadFailureKind.None;
    public static TrackerStateLoadResult Loaded(PersistentTrackerState state) => new(state ?? throw new ArgumentNullException(nameof(state)), TrackerStateLoadFailureKind.None, null);
    public static TrackerStateLoadResult Failed(TrackerStateLoadFailureKind kind, string message) => new(null, kind, message ?? throw new ArgumentNullException(nameof(message)));
}

public interface ITrackerStateRepository : IAsyncDisposable
{
    Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default);
}

public sealed record TrackerStateChanged(PersistentTrackerState State, TrackerCommandType CommandType);

public interface ITrackerStateChangePublisher
{
    Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default);
}

public enum TrackerCommandExecutionStatus { Applied, NoChange, SaveFailed, DeliveryFailed, NotInitialized }

public sealed record TrackerCommandExecutionResult(TrackerCommandExecutionStatus Status, PersistentTrackerState? CommittedState, string? FailureMessage);
