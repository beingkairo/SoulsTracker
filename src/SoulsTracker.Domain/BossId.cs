namespace SoulsTracker.Domain;

/// <summary>
/// Identifies a boss by its stable catalog identifier rather than a display label.
/// </summary>
public sealed record BossId
{
    private BossId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the stable catalog identifier.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates an identifier without normalizing or translating its value.
    /// Catalog membership is validated separately by <see cref="GameCatalog"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is blank or padded.</exception>
    public static BossId Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The boss ID must be a non-blank, unpadded identifier.", nameof(value));
        }

        return new BossId(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
