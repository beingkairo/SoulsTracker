namespace SoulsTracker.Domain;

/// <summary>
/// Provides immutable display metadata for a boss in one game's ordered catalog.
/// </summary>
public sealed record BossDefinition
{
    /// <summary>
    /// Initializes a boss definition.
    /// </summary>
    public BossDefinition(BossId id, string displayName, string? dlcLabel = null)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("The boss display name cannot be blank.", nameof(displayName));
        }

        if (dlcLabel is not null && string.IsNullOrWhiteSpace(dlcLabel))
        {
            throw new ArgumentException("A DLC label must be omitted or non-blank.", nameof(dlcLabel));
        }

        Id = id;
        DisplayName = displayName;
        DlcLabel = dlcLabel;
    }

    /// <summary>
    /// Gets the stable data identifier.
    /// </summary>
    public BossId Id { get; }

    /// <summary>
    /// Gets the user-facing name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the optional DLC grouping label.
    /// </summary>
    public string? DlcLabel { get; }
}
