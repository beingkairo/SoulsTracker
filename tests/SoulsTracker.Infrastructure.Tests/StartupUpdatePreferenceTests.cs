using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class StartupUpdatePreferenceTests
{
    [Fact]
    public void AllStateTransitionsAndLegacyImportPreserveEnabledPreference()
    {
        PersistentTrackerState state = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true);
        ITrackerCommand[] commands = [
            new IncrementManualDeathsCommand(), new DecrementManualDeathsCommand(),
            new UpdateOverlayPresentationCommand(false, false), new ResetOverlayAppearanceCommand(true),
            new UpdateOverlayAppearanceCommand(true, OverlayAppearance.Default, true, true),
            new AcknowledgeEldenRingNoticeCommand(),
            new UpdateEldenRingSaveConfigurationCommand(new("C:/synthetic/ER0000.sl2", 1)),
            new UpdateBlackMythWukongSaveConfigurationCommand(new("C:/synthetic/ArchiveSaveFile.1.sav")),
            new UpdateLiesOfPSaveConfigurationCommand(new("C:/synthetic/SaveData-1_Character_1.sav")),
            new SelectGameCommand(GameId.EldenRing), new AdjustEldenRingMissedDeathsCommand(true),
            new AdjustEldenRingMissedDeathsCommand(false), new SelectGameCommand(GameId.DemonsSouls)
        ];
        foreach (ITrackerCommand command in commands)
        {
            state = TrackerStateTransitionService.Apply(state, command).State;
            Assert.True(state.CheckForUpdatesOnStartup, command.GetType().Name);
        }
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze("""{"settings":{"selected_game":"bloodborne"},"death_count":123}"""u8.ToArray());
        LegacyProposalApplicationResult imported = ConfirmedLegacyProposalApplication.Apply(analysis, state);
        Assert.Equal(LegacyProposalApplicationOutcome.Applied, imported.Outcome);
        Assert.True(imported.CandidateState!.CheckForUpdatesOnStartup);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreferenceSurvivesSerializedMutationAndSqliteReopen(bool enabled)
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-update-setting-{Guid.NewGuid():N}");
        try
        {
            await using (var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "test.db"), new NullPublisher()))
            {
                Assert.False((await coordinator.InitializeAsync()).State!.CheckForUpdatesOnStartup);
                await coordinator.SubmitAsync(new SetCheckForUpdatesOnStartupCommand(!enabled));
                TrackerCommandExecutionResult result = await coordinator.SubmitAsync(new SetCheckForUpdatesOnStartupCommand(enabled));
                Assert.Equal(TrackerCommandExecutionStatus.Applied, result.Status);
                Assert.Equal(enabled, result.CommittedState!.CheckForUpdatesOnStartup);
                await coordinator.SubmitAsync(new IncrementManualDeathsCommand());
                await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
                await coordinator.SetTextExportConfigurationAsync(TextExportConfiguration.Default);
                await coordinator.SetGlobalHotkeysAsync(GlobalHotkeyConfiguration.Default);
                Assert.Equal(enabled, (await coordinator.SetEldenRingSaveConfigurationAsync(new("C:/synthetic/ER0000.sl2", 1))).CheckForUpdatesOnStartup);
                Assert.Equal(enabled, (await coordinator.SetBlackMythWukongSaveConfigurationAsync(new("C:/synthetic/ArchiveSaveFile.1.sav"))).CheckForUpdatesOnStartup);
                Assert.Equal(enabled, (await coordinator.SetLiesOfPSaveConfigurationAsync(new("C:/synthetic/SaveData-1_Character_1.sav"))).CheckForUpdatesOnStartup);
            }
            await using var reopened = new SqliteTrackerStateRepository(root, "test.db");
            TrackerStateLoadResult loaded = await reopened.LoadAsync();
            Assert.True(loaded.IsSuccess);
            Assert.Equal(enabled, loaded.State!.CheckForUpdatesOnStartup);
            Assert.Equal(1, loaded.State.ManualDemonsSoulsDeathCounter.Value);
            Assert.Equal(PersistentTrackerState.CurrentSchemaVersion, loaded.State.SchemaVersion);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task HistoricalAbsentPreferenceLoadsDisabled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-update-legacy-{Guid.NewGuid():N}");
        try
        {
            await using var repository = new SqliteTrackerStateRepository(root, "test.db");
            await repository.SaveAsync(PersistentTrackerState.Default);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "test.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE tracker_state SET payload='{\"TotalEnabled\":true,\"ShowGameName\":true}' WHERE id=1";
                await command.ExecuteNonQueryAsync();
            }
            TrackerStateLoadResult loaded = await repository.LoadAsync();
            Assert.True(loaded.IsSuccess);
            Assert.False(loaded.State!.CheckForUpdatesOnStartup);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
