using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop.Tests;

public sealed class HostedOverlayProjectionTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(7L)]
    [InlineData(9223372036854775807L)]
    public void PersistedManualValueIsAvailableImmediatelyAndDecrementKeepsZeroFloor(long value)
    {
        var counter = ManualDeathCounter.CreateFor(GameId.DemonsSouls, value);
        var state = new PersistentTrackerState(1, GameId.DemonsSouls, OverlayConfiguration.Default, manualDemonsSoulsDeathCounter: counter);
        var h = new Harness(state);
        Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), h.Output.Death?.Value);
        Assert.Equal("available", h.Output.Death?.Availability);
        Assert.Equal(h.Output, HostedOverlayJson.Parse(HostedOverlayJson.Serialize(h.Output)));
        counter = counter.Decrement();
        var decremented = new Harness(new(1, GameId.DemonsSouls, OverlayConfiguration.Default, manualDemonsSoulsDeathCounter: counter));
        Assert.Equal(counter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), decremented.Output.Death?.Value);
        if (value == 0) Assert.Equal("0", decremented.Output.Death?.Value);
    }

    [Fact]
    public void EldenRingAdjustmentRequiresAnAcceptedBaseAndIsAppliedExactlyOnce()
    {
        var selected = RuntimePublicationSessionTests.Selected(GameId.EldenRing);
        var state = new PersistentTrackerState(1, GameId.EldenRing, OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true, eldenRingSave: selected.EldenRingSave,
            eldenRingMissedDeathAdjustments: selected.EldenRingMissedDeathAdjustments.Increment(selected.EldenRingSave));
        var h = new Harness(state);
        h.Deliver(null);
        Assert.False(h.Output.HasDeath);
        h.Deliver(h.Read(100));
        Assert.Equal("101", h.Output.Death?.Value);
        h.Deliver(h.Read(0));
        Assert.Equal("101", h.Output.Death?.Value);
        h.Deliver(h.Read(0));
        Assert.Equal("1", h.Output.Death?.Value);
        h.Deliver(null);
        Assert.Equal("0", h.Output.Death?.Value);
        Assert.Equal("unavailable", h.Output.Death?.Availability);
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
    public void ExplicitSwitchInvalidatesBeforeDelayedCompletionAndDiffersFromStartup(string game)
    {
        var h = new Harness(RuntimePublicationSessionTests.Selected(GameId.Parse(game)));
        var original = h.State;
        var delayed = h.Session.BeginRead(original);
        var old = h.Read(900);
        h.Select(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
        h.Select(RuntimePublicationSessionTests.Selected(original.SelectedGameId, "other", 2, 2));
        var switched = h.Output;
        Assert.True(switched.HasDeath);
        Assert.Equal("unavailable", switched.Death?.Availability);
        Assert.Equal(game is "black_myth_wukong" or "lies_of_p" ? null : "0", switched.Death?.Value);
        h.Complete(delayed, old);
        Assert.Same(switched, h.Output);
        h.Deliver(RuntimeGameReadResult.Synced(new RuntimeGameObservation(h.State.SelectedGameId, 999,
            DateTimeOffset.UnixEpoch.AddSeconds(99), "wrong-source")));
        Assert.Same(switched, h.Output);
        h.Deliver(h.Read(0));
        Assert.Equal("available", h.Output.Death?.Availability);
        Assert.Equal("0", h.Output.Death?.Value);
    }

    [Fact]
    public void MissingSelectedSaveCannotTurnSyntheticObservationIntoStartupPublication()
    {
        var h = new Harness(new(1, GameId.BlackMythWukong, OverlayConfiguration.Default));
        h.Deliver(h.Read(0));
        Assert.False(h.Output.HasDeath);
    }

    [Fact]
    public async Task DelayedReaderCompletionCannotEscapeSessionGenerationOrCancellation()
    {
        var h = new Harness(RuntimePublicationSessionTests.Selected(GameId.Bloodborne));
        var ticket = h.Session.BeginRead(h.State);
        var completion = new TaskCompletionSource<RuntimeGameReadResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task CompleteAsync() => h.Complete(ticket, await completion.Task);
        Task pending = CompleteAsync();
        h.Select(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
        h.Select(RuntimePublicationSessionTests.Selected(GameId.Bloodborne));
        var before = h.Output;
        completion.SetResult(h.Read(123));
        await pending;
        Assert.Same(before, h.Output);
        var older = h.Session.BeginRead(h.State);
        h.Deliver(h.Read(100));
        h.Complete(older, h.Read(150));
        Assert.Equal("100", h.Output.Death?.Value);
        var cancelled = h.Session.BeginRead(h.State);
        h.Session.CompleteRead(cancelled, h.State, h.Read(200), _ => Assert.Fail("Cancelled desktop callback"),
            _ => Assert.Fail("Cancelled hosted callback"), new CancellationToken(true));
        Assert.Equal("100", h.Output.Death?.Value);
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
    public void AcceptedSessionOwnsStartupZeroLowerAndRepeatedPolls(string game)
    {
        var h = new Harness(RuntimePublicationSessionTests.Selected(GameId.Parse(game)));
        Assert.False(h.Output.HasDeath);
        Assert.DoesNotContain("death", HostedOverlayJson.Serialize(h.Output), StringComparison.Ordinal);
        h.Deliver(RuntimeGameReadResult.Synced(new RuntimeGameObservation(h.State.SelectedGameId, 0,
            DateTimeOffset.UnixEpoch, "malformed-source")));
        Assert.False(h.Output.HasDeath);
        for (int i = 0; i < 3; i++)
        {
            h.Deliver(null);
            h.Deliver(RuntimeGameReadResult.WaitingForSaveFile(h.State.SelectedGameId));
            h.Deliver(RuntimeGameReadResult.WaitingForActiveCharacter(h.State.SelectedGameId));
            h.Deliver(RuntimeGameReadResult.SelectedSaveUnreadable(h.State.SelectedGameId));
            h.Deliver(RuntimeGameReadResult.Unavailable(h.State.SelectedGameId));
            h.Deliver(RuntimeGameReadResult.Cached(h.Read(0)));
            Assert.False(h.Output.HasDeath);
            Assert.NotNull(h.Output.Appearance);
        }
        h.Deliver(h.Read(0));
        Assert.Equal("0", h.Output.Death?.Value);
        Assert.Equal("available", h.Output.Death?.Availability);
        h.Deliver(h.Read(100));
        var accepted = h.Output;
        h.Deliver(h.Read(100));
        Assert.Null(HostedOverlayDiff.Between(accepted, h.Output));
        var lower = h.Read(90);
        h.Deliver(lower);
        h.Deliver(RuntimeGameReadResult.Cached(lower));
        Assert.Equal("100", h.Output.Death?.Value);
        h.Deliver(h.Read(90));
        Assert.Equal("90", h.Output.Death?.Value);
        Assert.Null(HostedOverlayDiff.Between(accepted, h.Output)!.Appearance);
        h.Deliver(h.Read(9007199254740993L));
        Assert.Equal("9007199254740993", HostedOverlayJson.Parse(HostedOverlayJson.Serialize(h.Output)).Death!.Value);
        h.Deliver(null);
        Assert.True(h.Output.HasDeath);
        Assert.Equal("unavailable", h.Output.Death?.Availability);
        Assert.Equal(game is "black_myth_wukong" or "lies_of_p" ? null : "0", h.Output.Death?.Value);
        Assert.Equal(OverlaySnapshotFactory.Create(h.State, null, 0).TotalDeaths.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture), h.Output.Death?.Value);
    }

    private sealed class Harness
    {
        internal readonly RuntimePublicationSession Session = new();
        internal readonly HostedOverlayProjection Projection = new();
        internal PersistentTrackerState State;
        internal HostedOverlayEnvelope Output;
        internal RuntimeGameReadResult? Desktop;
        private int second;
        internal Harness(PersistentTrackerState state)
        {
            State = state;
            Session.SelectState(state);
            Output = Projection.Initialize(state);
        }
        internal RuntimeGameReadResult Read(long total) => RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            State.SelectedGameId, total, DateTimeOffset.UnixEpoch.AddSeconds(++second), EffectiveDeathTotalResult.SourceIdentityFor(State)));
        internal void Deliver(RuntimeGameReadResult? raw) => Complete(Session.BeginRead(State), raw);
        internal void Select(PersistentTrackerState state)
        {
            State = state;
            Session.SelectState(state);
            Output = Projection.FromExplicitSourceChange(state);
        }
        internal void Complete(RuntimePublicationSession.ReadTicket ticket, RuntimeGameReadResult? raw) =>
            Session.CompleteRead(ticket, State, raw, r => Desktop = r,
                accepted => Output = Projection.FromAcceptedPublication(Session.CurrentState!, accepted));
    }
}
