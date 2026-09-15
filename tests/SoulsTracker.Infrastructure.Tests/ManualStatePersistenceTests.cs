using System.Text.Json;
using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class ManualStatePersistenceTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("123")]
    [InlineData("-1")]
    [InlineData("999999999999999999999999999999")]
    [InlineData("{\"obsolete\":true}")]
    [InlineData("null")]
    public async Task HistoricalBloodborneManualValuesAreIgnoredAndNeverSavedAgain(string historicalValue)
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-manual-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var repository = new SqliteTrackerStateRepository(root, "test.db");
            await repository.SaveAsync(PersistentTrackerState.Default);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "test.db"), Pooling = false,
            }.ToString());
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE tracker_state SET payload=$payload WHERE id=1";
                command.Parameters.AddWithValue("$payload", $$"""
                    {"SelectedGameId":"bloodborne","ManualDeaths":{{historicalValue}},"ManualDemonsSoulsDeaths":7,"TotalEnabled":true,"ShowGameName":true}
                    """);
                await command.ExecuteNonQueryAsync();
            }

            TrackerStateLoadResult loaded = await repository.LoadAsync();
            Assert.True(loaded.IsSuccess);
            PersistentTrackerState state = loaded.State!;
            Assert.Equal(GameId.Bloodborne, state.SelectedGameId);
            Assert.Equal(7, state.ManualDemonsSoulsDeathCounter.Value);
            Assert.Throws<InvalidOperationException>(() => state.GetManualDeathCounter(GameId.Bloodborne));
            Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new IncrementManualDeathsCommand()));
            Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new DecrementManualDeathsCommand()));
            Assert.Null(EffectiveDeathTotalResult.Resolve(state, null).EffectiveDisplayedTotal);
            RuntimeGameObservation observation = new(GameId.Bloodborne, new GameLifetimeDeathTotal(42),
                DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(state));
            Assert.Equal(42, EffectiveDeathTotalResult.Resolve(state, observation).EffectiveDisplayedTotal);

            await repository.SaveAsync(state);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT payload FROM tracker_state WHERE id=1";
            using JsonDocument saved = JsonDocument.Parse((string)(await read.ExecuteScalarAsync())!);
            Assert.False(saved.RootElement.TryGetProperty("ManualDeaths", out _));
            Assert.Equal(7, saved.RootElement.GetProperty("ManualDemonsSoulsDeaths").GetInt64());
            TrackerStateLoadResult reloaded = await repository.LoadAsync();
            Assert.True(reloaded.IsSuccess);
            Assert.Equal(GameId.Bloodborne, reloaded.State!.SelectedGameId);
            Assert.Equal(7, reloaded.State.ManualDemonsSoulsDeathCounter.Value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DemonSoulsCommandsSurviveSqliteReopenAndGameSwitch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-manual-state-{Guid.NewGuid():N}");
        try
        {
            await using (var coordinator = new SerializedTrackerCoordinator(
                new SqliteTrackerStateRepository(root, "test.db"), new NullPublisher()))
            {
                Assert.True((await coordinator.InitializeAsync()).IsSuccess);
                await coordinator.SubmitAsync(new IncrementManualDeathsCommand());
                await coordinator.SubmitAsync(new IncrementManualDeathsCommand());
                await coordinator.SubmitAsync(new DecrementManualDeathsCommand());
                await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
                await coordinator.SubmitAsync(new SelectGameCommand(GameId.DemonsSouls));
            }
            await using var repository = new SqliteTrackerStateRepository(root, "test.db");
            TrackerStateLoadResult reloaded = await repository.LoadAsync();
            Assert.True(reloaded.IsSuccess);
            Assert.Equal(GameId.DemonsSouls, reloaded.State!.SelectedGameId);
            Assert.Equal(1, reloaded.State.ManualDemonsSoulsDeathCounter.Value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LegacyImportPreservesNonzeroDemonSoulsStateAtBothGuards()
    {
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(
            """{"settings":{"selected_game":"bloodborne"},"death_count":123}"""u8.ToArray());
        PersistentTrackerState destination = new(PersistentTrackerState.CurrentSchemaVersion, GameId.DemonsSouls,
            OverlayConfiguration.Default, manualDemonsSoulsDeathCounter: ManualDeathCounter.CreateFor(GameId.DemonsSouls, 7));
        LegacyProposalApplicationResult refused = ConfirmedLegacyProposalApplication.Apply(analysis, destination);
        Assert.Equal(LegacyProposalApplicationOutcome.DestinationHasManualDeaths, refused.Outcome);
        Assert.Null(refused.CandidateState);

        LegacyProposalApplicationResult candidate = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);
        Assert.Equal(LegacyProposalApplicationOutcome.Applied, candidate.Outcome);
        Assert.Equal(GameId.Bloodborne, candidate.CandidateState!.SelectedGameId);
        Assert.Equal(0, candidate.CandidateState.ManualDemonsSoulsDeathCounter.Value);
        string root = Path.Combine(Path.GetTempPath(), $"souls-manual-state-{Guid.NewGuid():N}");
        try
        {
            await using var repository = new SqliteTrackerStateRepository(root, "test.db");
            await repository.SaveAsync(destination);
            var audit = new ConfirmedLegacyImportAuditMetadata(ConfirmedLegacyImportAuditMetadata.CurrentContractVersion,
                LegacyImportPreflightOutcome.Prepared, new string('A', 64), new string('B', 64));
            ConfirmedLegacyImportCommitResult result = await repository.CommitConfirmedLegacyImportAsync(candidate, audit);
            Assert.Equal(ConfirmedLegacyImportCommitOutcome.DestinationHasManualDeaths, result.Outcome);
            TrackerStateLoadResult loaded = await repository.LoadAsync();
            Assert.True(loaded.IsSuccess);
            Assert.Equal(GameId.DemonsSouls, loaded.State!.SelectedGameId);
            Assert.Equal(7, loaded.State.ManualDemonsSoulsDeathCounter.Value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
