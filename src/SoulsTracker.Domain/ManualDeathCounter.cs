namespace SoulsTracker.Domain;

/// <summary>
/// Represents the streamer-controlled Demon Souls manual death counter.
/// As a sealed reference type, its default value is <see langword="null"/>. Its private
/// constructor ensures usable values can originate only from <see cref="CreateFor(GameId, long)"/>.
/// </summary>
public sealed class ManualDeathCounter
{
    private ManualDeathCounter(long value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the current non-negative manual value.
    /// </summary>
    public long Value { get; }

    /// <summary>
    /// Creates a manual counter only for Demon Souls.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown for every automatic game.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="initialValue"/> is negative.</exception>
    public static ManualDeathCounter CreateFor(GameId gameId, long initialValue = 0)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        if (gameId != GameId.DemonsSouls)
        {
            throw new InvalidOperationException("A manual death counter is available only for approved manual profiles.");
        }

        if (initialValue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialValue), initialValue, "A manual death counter cannot be negative.");
        }

        return new ManualDeathCounter(initialValue);
    }

    /// <summary>
    /// Returns a new counter with one streamer-controlled death added.
    /// </summary>
    public ManualDeathCounter Increment() => new(checked(Value + 1));

    /// <summary>
    /// Returns a new counter with one streamer-controlled death removed, or this
    /// instance when it is already zero.
    /// </summary>
    public ManualDeathCounter Decrement() => Value == 0 ? this : new(Value - 1);
}
