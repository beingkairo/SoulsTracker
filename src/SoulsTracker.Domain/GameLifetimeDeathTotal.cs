namespace SoulsTracker.Domain;

/// <summary>
/// Represents a non-negative game-provided lifetime Total Deaths observation.
/// It is not an application-owned session, run, or manually adjusted counter.
/// </summary>
public readonly record struct GameLifetimeDeathTotal
{
    /// <summary>
    /// Initializes a lifetime Total Deaths observation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is negative.</exception>
    public GameLifetimeDeathTotal(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A lifetime death total cannot be negative.");
        }

        Value = value;
    }

    /// <summary>
    /// Gets the game-provided lifetime observation.
    /// </summary>
    public long Value { get; }
}
