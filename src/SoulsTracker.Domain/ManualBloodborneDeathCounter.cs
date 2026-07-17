namespace SoulsTracker.Domain;

/// <summary>
/// Represents the streamer-controlled manual death counter shared by manual PlayStation profiles.
/// As a sealed reference type, its default value is <see langword="null"/>. Its private
/// constructor ensures usable values can originate only from <see cref="CreateFor(GameId, long)"/>.
/// </summary>
public sealed class ManualBloodborneDeathCounter
{
    private ManualBloodborneDeathCounter(long value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the current non-negative manual value.
    /// </summary>
    public long Value { get; }

    /// <summary>
    /// Creates a manual counter only for the Bloodborne definition.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown for every non-Bloodborne game.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="initialValue"/> is negative.</exception>
    public static ManualBloodborneDeathCounter CreateFor(GameId gameId, long initialValue = 0)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        if (gameId != GameId.Bloodborne && gameId != GameId.DemonsSouls)
        {
            throw new InvalidOperationException("A manual death counter is available only for Bloodborne and Demon's Souls.");
        }

        if (initialValue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialValue), initialValue, "A manual death counter cannot be negative.");
        }

        return new ManualBloodborneDeathCounter(initialValue);
    }

    /// <summary>
    /// Returns a new counter with one streamer-controlled death added.
    /// </summary>
    public ManualBloodborneDeathCounter Increment() => new(checked(Value + 1));

    /// <summary>
    /// Returns a new counter with one streamer-controlled death removed, or this
    /// instance when it is already zero.
    /// </summary>
    public ManualBloodborneDeathCounter Decrement() => Value == 0 ? this : new(Value - 1);
}
