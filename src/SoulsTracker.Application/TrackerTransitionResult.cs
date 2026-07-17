using SoulsTracker.Domain;

namespace SoulsTracker.Application;

/// <summary>
/// Describes the pure outcome of evaluating a tracker-state command.
/// </summary>
public sealed class TrackerTransitionResult
{
    /// <summary>
    /// Initializes a transition result.
    /// </summary>
    public TrackerTransitionResult(
        PersistentTrackerState state,
        bool stateChanged,
        TrackerCommandType commandType)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(commandType))
        {
            throw new ArgumentOutOfRangeException(nameof(commandType), commandType, "The command type is not supported.");
        }

        State = state;
        StateChanged = stateChanged;
        CommandType = commandType;
    }

    /// <summary>
    /// Gets the resulting immutable persistent state.
    /// </summary>
    public PersistentTrackerState State { get; }

    /// <summary>
    /// Gets whether the command semantically changed the supplied state.
    /// </summary>
    public bool StateChanged { get; }

    /// <summary>
    /// Gets the type of command that was evaluated.
    /// </summary>
    public TrackerCommandType CommandType { get; }
}
