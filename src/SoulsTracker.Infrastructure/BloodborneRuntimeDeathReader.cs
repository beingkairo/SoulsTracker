using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>One Bloodborne entry point: the unchanged shadPS4 reader and the verified shared 1.09 PC layout.</summary>
public sealed class BloodborneRuntimeDeathReader : IRuntimeGameDeathReader
{
    private readonly IBloodborneProcessEnumerator legacyProcesses;
    private readonly IBloodborneProcessEnumerator gameWindows;
    private readonly IReadOnlyProcessAttachmentFactory attachments;

    public BloodborneRuntimeDeathReader() : this(new ExactNameBloodborneProcessEnumerator(),
        new BloodborneWindowProcessEnumerator(), new WindowsReadOnlyProcessAttachmentFactory())
    { }

    public BloodborneRuntimeDeathReader(IBloodborneProcessEnumerator legacyProcesses,
        IBloodborneProcessEnumerator gameWindows, IReadOnlyProcessAttachmentFactory attachments)
    {
        this.legacyProcesses = legacyProcesses ?? throw new ArgumentNullException(nameof(legacyProcesses));
        this.gameWindows = gameWindows ?? throw new ArgumentNullException(nameof(gameWindows));
        this.attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
    }

    public GameId GameId => GameId.Bloodborne;

    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !Environment.Is64BitProcess) return null;
        IReadOnlyList<IBloodborneProcessCandidate> legacy = [];
        IReadOnlyList<IBloodborneProcessCandidate> windows = [];
        try
        {
            legacy = await legacyProcesses.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false);
            // Preserve the released reader's exact-name ambiguity rule.
            if (legacy.Count > 1) return null;
            bool legacyActive = legacy.Count == 1 &&
                await legacy[0].IsExpectedGuestActiveAsync(cancellationToken).ConfigureAwait(false);
            windows = await gameWindows.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false);
            if (windows.Count > BloodborneWindowProcessEnumerator.MaximumCandidates) return null;
            HashSet<int> seen = legacyActive ? legacy.Select(candidate => candidate.ProcessId).ToHashSet() : [];
            int recognized = legacyActive ? 1 : 0;
            RuntimeGameReadResult? portResult = null;
            foreach (IBloodborneProcessCandidate candidate in windows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(candidate.ProcessId) ||
                    !await candidate.IsExpectedGuestActiveAsync(cancellationToken).ConfigureAwait(false)) continue;
                ReadOnlyProcessAttachmentResult opened = await attachments.AttachAsync(candidate.ProcessId, cancellationToken).ConfigureAwait(false);
                // A denied or incomplete candidate must not be mistaken for proof of a single instance.
                if (opened.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || opened.Attachment is null) return null;
                await using IReadOnlyProcessAttachment memory = opened.Attachment;
                BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.ReadPreparedDeathsAsync(memory, cancellationToken).ConfigureAwait(false);
                if (sample.Status == BloodborneCompatibilityStatus.UnmatchedImage) continue;
                if (sample.Status is not (BloodborneCompatibilityStatus.Sampled or BloodborneCompatibilityStatus.WaitingForCharacter or
                    BloodborneCompatibilityStatus.UnstableRead or BloodborneCompatibilityStatus.CounterUnavailable)) return null;
                if (++recognized > 1) return null;
                portResult = sample.Status == BloodborneCompatibilityStatus.Sampled && sample.Deaths is { } deaths
                    ? Observe(deaths) : RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            }
            if (cancellationToken.IsCancellationRequested) return null;
            if (legacyActive)
            {
                // Borrow this poll's exact process snapshot; its real objects are disposed once below.
                return await new BloodborneActiveCharacterDeathReader(new BorrowedProcesses(legacy), attachments)
                    .ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            return portResult;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or OverflowException ||
            exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            foreach (IBloodborneProcessCandidate candidate in windows) await candidate.DisposeAsync().ConfigureAwait(false);
            foreach (IBloodborneProcessCandidate candidate in legacy) await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }

    private RuntimeGameReadResult Observe(uint deaths)
    {
        RuntimeGameObservation observation = new(GameId, deaths, DateTimeOffset.UtcNow, GameId.Value);
        return deaths == 0 ? RuntimeGameReadResult.NoDeathsRecorded(observation) : RuntimeGameReadResult.Synced(observation);
    }

    private sealed class BorrowedProcesses(IReadOnlyList<IBloodborneProcessCandidate> candidates) : IBloodborneProcessEnumerator
    {
        public ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IBloodborneProcessCandidate>>(candidates.Select(candidate =>
                (IBloodborneProcessCandidate)new BorrowedCandidate(candidate)).ToArray());
    }

    private sealed class BorrowedCandidate(IBloodborneProcessCandidate candidate) : IBloodborneProcessCandidate
    {
        public int ProcessId => candidate.ProcessId;
        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken) => candidate.IsExpectedGuestActiveAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
