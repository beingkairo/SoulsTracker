using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HollowKnightAdjustmentTests
{
    private static readonly Guid Installation = Guid.NewGuid();
    private static readonly Guid First = Guid.NewGuid();
    private static PersistentTrackerState State(HollowKnightDeathAdjustments? values = null) => new(1, GameId.HollowKnight,
        OverlayConfiguration.Default, hollowKnight: new("C:\\Games\\Hollow Knight", Installation) { DeathAdjustments = values ?? HollowKnightDeathAdjustments.Empty });
    private static PersistentPluginObservation Observation(PersistentTrackerState state, Guid run, long total) =>
        new(EffectiveDeathTotalResult.SourceIdentityFor(state), run, 1, total, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void AdditionsAreIndependentOfSlotsOtherInstallationsAndRawTotals()
    {
        Guid other = Guid.NewGuid(), second = Guid.NewGuid();
        var values = HollowKnightDeathAdjustments.Empty.Change(Installation, First, true).Change(Installation, First, true)
            .Change(Installation, second, true).Change(other, First, true);
        var state = State(values);
        var raw = Observation(state, First, 3);
        Assert.Equal(5, EffectiveDeathTotalResult.Resolve(state, null, raw).EffectiveDisplayedTotal);
        Assert.Equal(3, raw.Total);
        Assert.Equal(1, values.Get(Installation, second));
        Assert.Equal(1, values.Get(other, First));
        Assert.Equal(0, values.Get(Installation, Guid.NewGuid()));
        Assert.Equal(1, EffectiveDeathTotalResult.Resolve(state, null, Observation(state, second, 0)).EffectiveDisplayedTotal);
        Assert.Null(EffectiveDeathTotalResult.Resolve(state, null).EffectiveDisplayedTotal);
        Assert.Null(EffectiveDeathTotalResult.Resolve(state, null, raw with { SourceIdentity = "foreign" }).EffectiveDisplayedTotal);
        Assert.Equal(EffectiveDeathTotalStatus.Unreadable, EffectiveDeathTotalResult.Resolve(state, null, raw with { Total = long.MaxValue }).Status);
        Assert.Same(HollowKnightDeathAdjustments.Empty, HollowKnightDeathAdjustments.Empty.Change(Installation, First, false));
        Assert.Throws<ArgumentException>(() => new HollowKnightDeathAdjustments([new(Guid.Empty, First, 1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HollowKnightDeathAdjustments([new(Installation, First, -1)]));
        Assert.Throws<ArgumentException>(() => new HollowKnightDeathAdjustments([new(Installation, First, 1), new(Installation, First, 2)]));
    }

    [Fact]
    public void CommandsRetainAdditionsAcrossDirectoryAndUnrelatedStateChanges()
    {
        var state = TrackerStateTransitionService.Apply(State(), new AdjustHollowKnightDeathsCommand(Installation, First, true)).State;
        state = TrackerStateTransitionService.Apply(state, new UpdateHollowKnightConfigurationCommand(new("C:\\Other", Guid.NewGuid()))).State;
        Assert.Equal(1, state.HollowKnight.DeathAdjustments.Get(Installation, First));
        state = TrackerStateTransitionService.Apply(state, new SetCheckForUpdatesOnStartupCommand(true)).State;
        state = TrackerStateTransitionService.Apply(state, new SelectGameCommand(GameId.Ds3)).State;
        Assert.Equal(1, state.HollowKnight.DeathAdjustments.Get(Installation, First));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new AdjustHollowKnightDeathsCommand(Installation, First, true)));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(State(), new AdjustHollowKnightDeathsCommand(Guid.NewGuid(), First, true)));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(State(), new AdjustHollowKnightDeathsCommand(Installation, Guid.Empty, true)));
    }

    [Fact]
    public async Task SqliteRestartsPreservePerRunValuesAndCorruptEntriesFailWithoutResettingData()
    {
        string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.HkAdjustments", Guid.NewGuid().ToString("N"));
        try
        {
            Guid second = Guid.NewGuid();
            var state = State(HollowKnightDeathAdjustments.Empty.Change(Installation, First, true).Change(Installation, second, true));
            await using (var repository = new SqliteTrackerStateRepository(root, "tracker.db")) { await repository.SaveAsync(state); }
            await using (var repository = new SqliteTrackerStateRepository(root, "tracker.db"))
            {
                var loaded = await repository.LoadAsync();
                Assert.True(loaded.IsSuccess);
                Assert.Equal(1, loaded.State!.HollowKnight.DeathAdjustments.Get(Installation, First));
                Assert.Equal(1, loaded.State.HollowKnight.DeathAdjustments.Get(Installation, second));
            }
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "tracker.db"), Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tracker_state SET payload=json_set(payload,'$.HollowKnightDeathAdjustments[0].Value',-1)";
            await command.ExecuteNonQueryAsync();
            await using var corrupt = new SqliteTrackerStateRepository(root, "tracker.db");
            Assert.False((await corrupt.LoadAsync()).IsSuccess);
            command.CommandText = "SELECT json_extract(payload,'$.HollowKnightDeathAdjustments[0].Value') FROM tracker_state";
            Assert.Equal(-1L, await command.ExecuteScalarAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
