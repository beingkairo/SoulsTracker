namespace SoulsTracker.Domain;

/// <summary>
/// Represents one immutable, game-provided lifetime Total Deaths observation.
/// It is runtime-only and must not be persisted as app-owned tracker state.
/// </summary>
public sealed class RuntimeGameObservation
{
    /// <summary>
    /// Initializes a runtime observation from a validated lifetime total.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the game is not a selectable automatic profile or the timestamp is not UTC.</exception>
    public RuntimeGameObservation(
        GameId gameId,
        GameLifetimeDeathTotal totalDeaths,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        GameDefinition definition = GameCatalog.GetRequired(gameId);
        if (!definition.IsSelectable || definition.TrackingMode != GameTrackingMode.GameLifetimeReadOnly)
        {
            throw new ArgumentException(
                "Runtime observations are available only for selectable automatic game profiles.",
                nameof(gameId));
        }

        if (totalDeaths.Value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalDeaths),
                "A runtime lifetime death total cannot be negative.");
        }

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The runtime observation timestamp must be UTC.", nameof(observedAtUtc));
        }

        GameId = gameId;
        TotalDeaths = totalDeaths;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>
    /// Initializes a runtime observation from a raw reader value while retaining
    /// the domain lifetime-total type in the resulting contract.
    /// </summary>
    public RuntimeGameObservation(GameId gameId, long totalDeaths, DateTimeOffset observedAtUtc)
        : this(gameId, new GameLifetimeDeathTotal(totalDeaths), observedAtUtc)
    {
    }

    /// <summary>
    /// Gets the canonical automatic game ID.
    /// </summary>
    public GameId GameId { get; }

    /// <summary>
    /// Gets the non-negative game-provided lifetime total.
    /// </summary>
    public GameLifetimeDeathTotal TotalDeaths { get; }

    /// <summary>
    /// Gets the UTC timestamp at which the reader observed the total.
    /// </summary>
    public DateTimeOffset ObservedAtUtc { get; }
}
