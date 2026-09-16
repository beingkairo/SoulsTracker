using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class RuntimePublicationSessionTests
{
    [Fact]
    public void CachedAndRepeatedObservationsCannotConfirmOrPublishACandidate()
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        RuntimeGameReadResult lower = h.Read(90);
        h.Deliver(lower);
        h.Deliver(RuntimeGameReadResult.Cached(lower));
        Assert.Equal(RuntimeGameReaderStatus.PendingLowerValue, h.Desktop?.Status);
        Assert.Equal(100, h.Output?.Observation?.TotalDeaths.Value);
        h.Deliver(lower);
        Assert.Equal(RuntimeGameReaderStatus.PendingLowerValue, h.Desktop?.Status);
        Assert.Equal(100, h.Output?.Observation?.TotalDeaths.Value);
        h.Deliver(h.Read(90));
        Assert.Equal(90, h.Output?.Observation?.TotalDeaths.Value);
    }

    [Theory]
    [InlineData(30000, true)]
    [InlineData(30001, false)]
    public void ConfirmationWindowIsInclusiveAndCachedReadsDoNotExtendIt(int elapsed, bool accepted)
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        RuntimeGameReadResult lower = h.Read(90);
        h.Deliver(lower);
        h.Clock.Advance(20000);
        h.Deliver(RuntimeGameReadResult.Cached(lower));
        h.Clock.Advance(elapsed - 20000);
        h.Deliver(h.Read(90));
        Assert.Equal(accepted ? 90 : 100, h.Output?.Observation?.TotalDeaths.Value);
        if (!accepted)
        {
            h.Deliver(h.Read(90));
            Assert.Equal(90, h.Output?.Observation?.TotalDeaths.Value);
        }
    }

    [Fact]
    public void OverlappingReadsCannotConfirmAndOlderCompletionsCannotReplaceNewerOnes()
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        var first = h.Session.BeginRead(h.State);
        var overlapping = h.Session.BeginRead(h.State);
        h.Complete(first, h.Read(90));
        h.Complete(overlapping, h.Read(90));
        Assert.Equal(100, h.Output?.Observation?.TotalDeaths.Value);
        h.Deliver(h.Read(90));
        Assert.Equal(90, h.Output?.Observation?.TotalDeaths.Value);
        var older = h.Session.BeginRead(h.State);
        var newer = h.Session.BeginRead(h.State);
        h.Complete(newer, h.Read(120));
        RuntimeGameReadResult? output = h.Output;
        h.Complete(older, h.Read(130));
        Assert.Same(output, h.Output);
        h.Complete(newer, h.Read(140));
        Assert.Same(output, h.Output);
    }

    [Fact]
    public void SelectionGenerationsRejectSwitchAwayAndBackCompletions()
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        h.Deliver(h.Read(90));
        var delayed = h.Session.BeginRead(h.State);
        PersistentTrackerState original = h.State;
        h.State = new(PersistentTrackerState.CurrentSchemaVersion, GameId.Bloodborne, OverlayConfiguration.Default);
        h.Deliver(h.Read(10));
        Assert.Equal(10, h.Output?.Observation?.TotalDeaths.Value);
        h.State = original;
        _ = h.Session.BeginRead(h.State);
        RuntimeGameReadResult? output = h.Output;
        h.Complete(delayed, h.Read(90));
        Assert.Same(output, h.Output);
        h.Deliver(h.Read(5));
        Assert.Equal(5, h.Output?.Observation?.TotalDeaths.Value);
    }

    [Fact]
    public void WrongIdentityDoesNotClearPendingEvidence()
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        h.Deliver(h.Read(90));
        RuntimeGameReadResult? desktop = h.Desktop;
        h.Deliver(RuntimeGameReadResult.Synced(new RuntimeGameObservation(h.State.SelectedGameId,
            2, DateTimeOffset.UnixEpoch.AddSeconds(50), "wrong")));
        Assert.Same(desktop, h.Desktop);
        h.Deliver(h.Read(90));
        Assert.Equal(90, h.Output?.Observation?.TotalDeaths.Value);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unavailable")]
    [InlineData("unreadable")]
    [InlineData("save")]
    [InlineData("character")]
    public void FailureClearsEvidenceButRetainsComparator(string failure)
    {
        var h = new Harness();
        RuntimeGameReadResult accepted = h.Read(100);
        h.Deliver(accepted);
        RuntimeGameReadResult lower = h.Read(90);
        h.Deliver(lower);
        RuntimeGameReadResult? unavailable = failure switch
        {
            "unavailable" => RuntimeGameReadResult.Unavailable(h.State.SelectedGameId, lower.Observation),
            "unreadable" => RuntimeGameReadResult.SelectedSaveUnreadable(h.State.SelectedGameId),
            "save" => RuntimeGameReadResult.WaitingForSaveFile(h.State.SelectedGameId),
            "character" => RuntimeGameReadResult.WaitingForActiveCharacter(h.State.SelectedGameId),
            _ => null,
        };
        h.Deliver(unavailable);
        Assert.NotEqual(RuntimeGameReaderStatus.PendingLowerValue, h.Desktop?.Status);
        Assert.NotEqual(90, h.Desktop?.Observation?.TotalDeaths.Value);
        if (failure == "unavailable") Assert.Same(accepted.Observation, h.Desktop?.Observation);
        RuntimeGameReadResult? failedOutput = h.Output;
        h.Deliver(h.Read(90));
        Assert.Equal(RuntimeGameReaderStatus.PendingLowerValue, h.Desktop?.Status);
        Assert.Same(failedOutput, h.Output);
        h.Deliver(h.Read(90));
        Assert.Equal(90, h.Output?.Observation?.TotalDeaths.Value);
    }

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    [InlineData("elden_ring")]
    [InlineData("black_myth_wukong")]
    [InlineData("lies_of_p")]
    public void EveryAutomaticGameUsesTheSameBaseAcceptanceMatrix(string game)
    {
        long[][] candidates = [[90, 90], [90, 100], [90, 80, 80], [0, 0], [90, 80, 90, 80], [90, 110]];
        long[][] expected = [[100, 90], [100, 100], [100, 100, 80], [100, 0], [100, 100, 100, 100], [100, 110]];
        for (int scenario = 0; scenario < candidates.Length; scenario++)
        {
            var h = new Harness { State = Selected(GameId.Parse(game)) };
            h.Deliver(h.Read(100));
            for (int step = 0; step < candidates[scenario].Length; step++)
            {
                h.Deliver(h.Read(candidates[scenario][step]));
                Assert.Equal(expected[scenario][step], h.Output?.Observation?.TotalDeaths.Value);
                Assert.Equal(expected[scenario][step], h.Desktop?.Observation?.TotalDeaths.Value);
            }
        }
    }

    [Theory]
    [InlineData("elden_ring", "other", 1, 1)]
    [InlineData("elden_ring", "test", 2, 1)]
    [InlineData("black_myth_wukong", "other", 1, 1)]
    [InlineData("black_myth_wukong", "TEST", 1, 1)]
    [InlineData("lies_of_p", "other", 1, 1)]
    [InlineData("lies_of_p", "test", 1, 2)]
    public void ExactSelectedPathSlotAndPairedMemberStartNewSessions(string game, string profile, int slot, int member)
    {
        var h = new Harness { State = Selected(GameId.Parse(game)) };
        h.Deliver(h.Read(100));
        h.Deliver(h.Read(90));
        var delayed = h.Session.BeginRead(h.State);
        RuntimeGameReadResult old = h.Read(90);
        h.State = Selected(GameId.Parse(game), profile, slot, member);
        h.Session.SelectState(h.State);
        RuntimeGameReadResult? previous = h.Desktop;
        h.Complete(delayed, old);
        Assert.Same(previous, h.Desktop);
        h.Deliver(h.Read(10));
        Assert.Equal(10, h.Output?.Observation?.TotalDeaths.Value);
    }

    [Fact]
    public void OlderObservationAndCancelledCompletionDoNotPublishOrConfirm()
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        RuntimeGameReadResult older = h.Read(90);
        h.Deliver(h.Read(90));
        h.Deliver(older);
        Assert.Equal(100, h.Output?.Observation?.TotalDeaths.Value);
        using var cancellation = new CancellationTokenSource();
        var read = h.Session.BeginRead(h.State);
        cancellation.Cancel();
        h.Session.CompleteRead(read, h.State, h.Read(90), _ => Assert.Fail("Late desktop publication"),
            _ => Assert.Fail("Late output publication"), cancellation.Token);
        Assert.Equal(100, h.Output?.Observation?.TotalDeaths.Value);
    }

    [Fact]
    public void UnavailabilityExpiresWithoutLosingTheAcceptedComparator()
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        RuntimeGameReadResult lower = h.Read(90);
        h.Deliver(lower);
        h.Clock.Advance(32000);
        h.Deliver(RuntimeGameReadResult.Unavailable(h.State.SelectedGameId, lower.Observation));
        Assert.Null(h.Desktop?.Observation);
        RuntimeGameReadResult? unavailable = h.Desktop;
        h.Deliver(RuntimeGameReadResult.Cached(lower));
        Assert.Same(unavailable, h.Desktop);
        h.Deliver(h.Read(90));
        Assert.Equal(RuntimeGameReaderStatus.PendingLowerValue, h.Desktop?.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionUsingUnappliedDesktopStateCannotRollBackCommittedPublication(bool sameSource)
    {
        var h = new Harness { State = Selected(GameId.EldenRing) };
        h.Deliver(h.Read(100));
        var read = h.Session.BeginRead(h.State);
        PersistentTrackerState committed = sameSource
            ? new(PersistentTrackerState.CurrentSchemaVersion, GameId.EldenRing, OverlayConfiguration.Default,
                eldenRingNoticeAcknowledged: true, eldenRingSave: h.State.EldenRingSave,
                eldenRingMissedDeathAdjustments: h.State.EldenRingMissedDeathAdjustments.Increment(h.State.EldenRingSave))
            : Selected(GameId.Bloodborne);
        h.Session.SelectState(committed);
        RuntimeGameReadResult? original = h.Output;
        h.Complete(read, h.Read(110));
        Assert.Same(original, h.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AwaitedReaderCompletionCannotPublishAfterCancellationOrSwitchAwayAndBack(bool cancel)
    {
        var h = new Harness();
        h.Deliver(h.Read(100));
        h.Deliver(h.Read(90));
        var reader = new ControlledReader(h.State.SelectedGameId);
        var coordinator = new RuntimeGameReaderCoordinator([reader]);
        using var cancellation = new CancellationTokenSource();
        var ticket = h.Session.BeginRead(h.State);
        Task<RuntimeGameReadResult?> reading = coordinator.PollAsync(h.State.SelectedGameId, cancellation.Token).AsTask();
        Assert.False(reading.IsCompleted);
        if (cancel) cancellation.Cancel();
        else
        {
            h.Session.SelectState(Selected(GameId.Bloodborne));
            h.Session.SelectState(h.State);
        }
        reader.Completion.SetResult(h.Read(90));
        RuntimeGameReadResult? raw = await reading;
        h.Session.CompleteRead(ticket, h.State, raw, _ => Assert.Fail("Late Desktop result"),
            _ => Assert.Fail("Late output result"), cancellation.Token);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(100, h.Output?.Observation?.TotalDeaths.Value);
    }

    [Fact]
    public void NewRuntimeSessionAcceptsItsInitialLowerValue()
    {
        var first = new Harness();
        first.Deliver(first.Read(100));
        var restarted = new Harness { State = first.State };
        restarted.Deliver(restarted.Read(10));
        Assert.Equal(10, restarted.Output?.Observation?.TotalDeaths.Value);
    }

    private sealed class ControlledReader(GameId game) : IRuntimeGameDeathReader
    {
        internal readonly TaskCompletionSource<RuntimeGameReadResult?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads;
        public GameId GameId => game;
        public ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return new(Completion.Task);
        }
    }

    internal static PersistentTrackerState Selected(GameId game, string profile = "test", int slot = 1, int member = 1) =>
        new(PersistentTrackerState.CurrentSchemaVersion, game, OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true,
            eldenRingSave: new EldenRingSaveConfiguration($"C:/saves/{profile}/ER0000.sl2", slot),
            blackMythWukongSave: new BlackMythWukongSaveConfiguration($"C:/saves/{profile}/ArchiveSaveFile.1.sav"),
            liesOfPSave: new LiesOfPSaveConfiguration($"C:/saves/{profile}/SaveData-1_Character_{member}.sav"));

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        internal void Advance(int milliseconds) => ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    private sealed class Harness
    {
        internal readonly Clock Clock = new();
        internal readonly RuntimePublicationSession Session;
        internal Harness() => Session = new(Clock);
        internal PersistentTrackerState State = new(PersistentTrackerState.CurrentSchemaVersion,
            GameId.BlackMythWukong, OverlayConfiguration.Default,
            blackMythWukongSave: new BlackMythWukongSaveConfiguration("C:/saves/test/ArchiveSaveFile.1.sav"));
        internal RuntimeGameReadResult? Desktop;
        internal RuntimeGameReadResult? Output;
        private int second;
        internal RuntimeGameReadResult Read(long value) => RuntimeGameReadResult.Synced(
            new RuntimeGameObservation(State.SelectedGameId, value, DateTimeOffset.UnixEpoch.AddSeconds(++second),
                EffectiveDeathTotalResult.SourceIdentityFor(State)));
        internal void Deliver(RuntimeGameReadResult? result) => Session.CompleteRead(
            Session.BeginRead(State), State, result, r => Desktop = r, r => Output = r);
        internal void Complete(RuntimePublicationSession.ReadTicket read, RuntimeGameReadResult? result) => Session.CompleteRead(
            read, State, result, r => Desktop = r, r => Output = r);
    }
}
