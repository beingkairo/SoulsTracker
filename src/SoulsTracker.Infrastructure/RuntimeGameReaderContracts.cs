using System.Buffers.Binary;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Reads a game-provided active-character death total without changing the target process.</summary>
public interface IRuntimeGameDeathReader
{
    GameId GameId { get; }

    ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Describes the safe runtime state of the selected automated reader.</summary>
public enum RuntimeGameReaderStatus
{
    Unavailable,
    WaitingForSaveFile,
    SelectedSaveUnreadable,
    WaitingForActiveCharacter,
    Synced,
    Cached,
    PendingLowerValue,
}

/// <summary>Contains only a safe reader status and, when synced, its runtime-only observation.</summary>
public sealed record RuntimeGameReadResult
{
    private RuntimeGameReadResult(
        GameId gameId,
        RuntimeGameReaderStatus status,
        RuntimeGameObservation? observation,
        BlackMythWukongSaveMetadata? blackMythWukongSaveMetadata = null,
        string? blackMythWukongSavePath = null,
        bool hasNoRecordedDeaths = false,
        bool isCurrentSaveCache = false)
    {
        GameId = gameId ?? throw new ArgumentNullException(nameof(gameId));
        Status = status;
        Observation = observation;
        BlackMythWukongSaveMetadata = blackMythWukongSaveMetadata;
        BlackMythWukongSavePath = blackMythWukongSavePath;
        HasNoRecordedDeaths = hasNoRecordedDeaths;
        IsCurrentSaveCache = isCurrentSaveCache;
    }

    public GameId GameId { get; }

    public RuntimeGameReaderStatus Status { get; }

    public RuntimeGameObservation? Observation { get; }
    public PersistentPluginObservation? PluginObservation { get; private init; }
    public string? PluginStatus { get; private init; }

    public static RuntimeGameReadResult FromHollowKnight(PersistentPluginObservation? observation, string status) =>
        new(GameId.HollowKnight, observation is null ? RuntimeGameReaderStatus.Unavailable : RuntimeGameReaderStatus.Synced, null)
        { PluginObservation = observation, PluginStatus = status };

    /// <summary>Contains optional setup-only metadata for a validated Wukong save.</summary>
    public BlackMythWukongSaveMetadata? BlackMythWukongSaveMetadata { get; }

    /// <summary>Identifies the selected Wukong save that produced the optional metadata.</summary>
    public string? BlackMythWukongSavePath { get; }

    /// <summary>
    /// True only when the selected reader validated the save and its parsed
    /// death total is zero. This distinguishes a confirmed new character from
    /// unavailable, unsupported, or unreadable save data.
    /// </summary>
    public bool HasNoRecordedDeaths { get; }

    /// <summary>Presentation-only provenance for an unchanged, successfully fingerprinted save. Does not make a cached observation fresh.</summary>
    public bool IsCurrentSaveCache { get; }

    public static RuntimeGameReadResult WaitingForActiveCharacter(GameId gameId) =>
        new(gameId, RuntimeGameReaderStatus.WaitingForActiveCharacter, null);

    public static RuntimeGameReadResult WaitingForSaveFile(GameId gameId) =>
        new(gameId, RuntimeGameReaderStatus.WaitingForSaveFile, null);

    /// <summary>Reports that a configured selected local save could not be safely read this poll.</summary>
    public static RuntimeGameReadResult SelectedSaveUnreadable(GameId gameId) =>
        new(gameId, RuntimeGameReaderStatus.SelectedSaveUnreadable, null);

    public static RuntimeGameReadResult Synced(
        RuntimeGameObservation observation,
        BlackMythWukongSaveMetadata? blackMythWukongSaveMetadata = null,
        string? blackMythWukongSavePath = null) =>
        new(
            observation?.GameId ?? throw new ArgumentNullException(nameof(observation)),
            RuntimeGameReaderStatus.Synced,
            observation,
            blackMythWukongSaveMetadata,
            blackMythWukongSavePath);

    public static RuntimeGameReadResult Cached(RuntimeGameReadResult result, bool isCurrentSaveCache = false) =>
        new(result.GameId, RuntimeGameReaderStatus.Cached, result.Observation,
            result.BlackMythWukongSaveMetadata, result.BlackMythWukongSavePath, result.HasNoRecordedDeaths, isCurrentSaveCache);

    public static RuntimeGameReadResult PendingLowerValue(RuntimeGameReadResult accepted) =>
        new(accepted.GameId, RuntimeGameReaderStatus.PendingLowerValue, accepted.Observation,
            accepted.BlackMythWukongSaveMetadata, accepted.BlackMythWukongSavePath, accepted.HasNoRecordedDeaths);

    /// <summary>Reports a parser-validated zero-death save without treating an unavailable reader as zero.</summary>
    public static RuntimeGameReadResult NoDeathsRecorded(
        RuntimeGameObservation observation,
        BlackMythWukongSaveMetadata? blackMythWukongSaveMetadata = null,
        string? blackMythWukongSavePath = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.TotalDeaths.Value != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "A no-deaths result requires a zero total.");
        }

        return new(
            observation.GameId,
            RuntimeGameReaderStatus.Synced,
            observation,
            blackMythWukongSaveMetadata,
            blackMythWukongSavePath,
            hasNoRecordedDeaths: true);
    }

    /// <summary>
    /// Reports an unavailable reader while carrying a same-game, presentation-
    /// only value that was confirmed moments earlier.
    /// </summary>
    public static RuntimeGameReadResult Unavailable(GameId gameId, RuntimeGameObservation? lastObservation = null) =>
        new(gameId, RuntimeGameReaderStatus.Unavailable,
            lastObservation?.GameId == gameId ? lastObservation : null);
}

/// <summary>Optional character-identifying details decoded from one validated Wukong archive.</summary>
public sealed record BlackMythWukongSaveMetadata(
    int? Level,
    TimeSpan? TotalPlayTime,
    DateTimeOffset? LastSaved);

/// <summary>Coordinates the small set of approved runtime-only game readers.</summary>
public sealed class RuntimeGameReaderCoordinator
{
    private static readonly TimeSpan TransientUnavailableRetention = TimeSpan.FromSeconds(30);
    private readonly Dictionary<GameId, IRuntimeGameDeathReader> readers;

    public RuntimeGameReaderCoordinator(IEnumerable<IRuntimeGameDeathReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        IRuntimeGameDeathReader[] supplied = readers.ToArray();
        if (supplied.Any(static reader => reader is null) || supplied.GroupBy(static reader => reader.GameId).Any(static group => group.Count() != 1))
        {
            throw new ArgumentException("Readers must be non-null and unique per game.", nameof(readers));
        }

        this.readers = supplied.ToDictionary(static reader => reader.GameId);
    }

    /// <summary>Gets the latest observation for presentation only. It is never persisted.</summary>
    public RuntimeGameReadResult? CurrentResult { get; private set; }

    public RuntimeGameReaderStatus CurrentStatus => CurrentResult?.Status ?? RuntimeGameReaderStatus.Unavailable;

    public RuntimeGameObservation? CurrentObservation => CurrentResult?.Observation;

    /// <summary>
    /// Polls only the selected approved reader. A confirmed value remains
    /// available for a short loading-screen grace period, but never crosses a
    /// game selection boundary or survives after that bounded period.
    /// </summary>
    public async ValueTask<RuntimeGameReadResult?> PollAsync(GameId? selectedGameId, CancellationToken cancellationToken,
        string? expectedSourceIdentity = null)
    {
        RuntimeGameReadResult? result = null;
        if (selectedGameId is not null && readers.TryGetValue(selectedGameId, out IRuntimeGameDeathReader? reader))
        {
            result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (result is null &&
            selectedGameId is not null &&
            CurrentResult is { GameId: var previousGameId, Observation: { } previousObservation } &&
            previousGameId == selectedGameId &&
            previousObservation.GameId == selectedGameId &&
            (expectedSourceIdentity is null || string.Equals(previousObservation.SourceIdentity, expectedSourceIdentity, StringComparison.Ordinal)) &&
            DateTimeOffset.UtcNow - previousObservation.ObservedAtUtc <= TransientUnavailableRetention)
        {
            result = RuntimeGameReadResult.Unavailable(selectedGameId, previousObservation);
        }

        if (!Equals(CurrentResult, result))
        {
            CurrentResult = result;
        }

        return result;
    }
}

/// <summary>Enumerates only the exact Dark Souls Remastered executable name and disposes every process object.</summary>
