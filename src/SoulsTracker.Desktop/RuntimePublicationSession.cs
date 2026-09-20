using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Owns acceptance of runtime readings before consumer publication.</summary>
internal sealed class RuntimePublicationSession(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long pendingAt;
    private long sequence;
    private long completedSequence;
    private long pendingSequence;
    private RuntimeGameReadResult? accepted;
    private RuntimeGameReadResult? pending;
    private DateTimeOffset? newestObservation;
    private string? source;
    private long generation;
    private bool hasFreshPresentation;
    internal PersistentTrackerState? CurrentState { get; private set; }
    internal readonly record struct ReadTicket(long Sequence, long CompletedBeforeStart, long Generation);

    // Called for every committed selection, including switches between polls.
    internal void SelectState(PersistentTrackerState state)
    {
        CurrentState = state;
        string selectedSource = EffectiveDeathTotalResult.SourceIdentityFor(state);
        if (string.Equals(source, selectedSource, StringComparison.Ordinal)) return;
        source = selectedSource;
        generation++;
        accepted = null;
        pending = null;
        newestObservation = null;
        hasFreshPresentation = false;
    }

    internal ReadTicket BeginRead(PersistentTrackerState state)
    {
        SelectState(state);
        return new(++sequence, completedSequence, generation);
    }

    internal void CompleteRead(ReadTicket read, PersistentTrackerState state, RuntimeGameReadResult? result,
        Action<RuntimeGameReadResult?> applyDesktop,
        Action<RuntimeGameReadResult?> publishOutputs, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;
        if (CurrentState is null ||
            !string.Equals(EffectiveDeathTotalResult.SourceIdentityFor(state), source, StringComparison.Ordinal) ||
            (state.SelectedGameId == GameId.EldenRing && state.EldenRingMissedDeathAdjustments.Get(state.EldenRingSave) !=
                CurrentState.EldenRingMissedDeathAdjustments.Get(CurrentState.EldenRingSave)) || read.Generation != generation ||
            read.Sequence > sequence || read.Sequence <= completedSequence) return;
        RuntimeGameReadResult? publication = NormalizeRuntimePublication(state, result);
        if (result is not null && publication is null) return;
        completedSequence = read.Sequence;
        if (publication?.Status == RuntimeGameReaderStatus.Cached)
        {
            if (accepted is not null && hasFreshPresentation)
                applyDesktop(pending is null ? RuntimeGameReadResult.Cached(accepted, publication.IsCurrentSaveCache) : RuntimeGameReadResult.PendingLowerValue(accepted));
            return;
        }
        if (publication is { Status: RuntimeGameReaderStatus.Synced, Observation: { } observation })
        {
            if (newestObservation is { } newest && observation.ObservedAtUtc <= newest) return;
            newestObservation = observation.ObservedAtUtc;
            hasFreshPresentation = true;
            if (accepted?.Observation is { } previous && observation.TotalDeaths.Value < previous.TotalDeaths.Value &&
                (pending?.Observation?.TotalDeaths.Value != observation.TotalDeaths.Value ||
                 read.CompletedBeforeStart < pendingSequence ||
                 clock.GetElapsedTime(pendingAt) > TimeSpan.FromSeconds(30)))
            {
                pending = publication;
                pendingAt = clock.GetTimestamp();
                pendingSequence = read.Sequence;
                applyDesktop(RuntimeGameReadResult.PendingLowerValue(accepted));
                return;
            }
            accepted = publication;
            pending = null;
        }
        else
        {
            pending = null;
            hasFreshPresentation = false;
            // The reader coordinator retains raw observations. Never let its
            // loading-screen grace period expose an unconfirmed lower candidate.
            if (publication?.Status == RuntimeGameReaderStatus.Unavailable)
            {
                RuntimeGameObservation? retained = publication.Observation is not null &&
                    accepted?.Observation is { } previous &&
                    clock.GetUtcNow() - previous.ObservedAtUtc <= TimeSpan.FromSeconds(30)
                        ? previous : null;
                publication = RuntimeGameReadResult.Unavailable(publication.GameId, retained);
            }
        }
        applyDesktop(publication);
        publishOutputs(publication);
    }

    internal static RuntimeGameReadResult? NormalizeRuntimePublication(PersistentTrackerState state, RuntimeGameReadResult? result)
    {
        if (result is null || result.GameId != state.SelectedGameId) return null;
        if (state.SelectedGameId == GameId.LiesOfP && state.LiesOfPSave.LocalPath is null) return null;
        if (result.Observation is not null &&
            EffectiveDeathTotalResult.Resolve(state, result.Observation).Status == EffectiveDeathTotalStatus.SourceMismatch)
        {
            return null;
        }
        return result;
    }
}
