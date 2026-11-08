namespace SoulsTracker.Domain;

/// <summary>A validated plugin-owned total for one save generation, never an app counter.</summary>
public sealed record PersistentPluginObservation(string SourceIdentity, Guid RunId, int Slot, long Total, long Revision, DateTimeOffset ObservedAtUtc);
