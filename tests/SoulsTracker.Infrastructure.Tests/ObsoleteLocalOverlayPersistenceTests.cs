using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class ObsoleteLocalOverlayPersistenceTests
{
    [Theory]
    [InlineData("4321")]
    [InlineData("-1")]
    [InlineData("{\"obsolete\":true}")]
    public async Task HistoricalEndpointBytesAreIgnoredWhileRetainedStateSurvivesSaveAndReopen(string port)
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-obsolete-overlay-{Guid.NewGuid():N}");
        try
        {
            PersistentTrackerState original = new(1, GameId.EldenRing, OverlayConfiguration.Default,
                globalHotkeys: new GlobalHotkeyConfiguration(2, 118, 2, 119),
                textExports: new TextExportConfiguration(Path.Combine(root, "deaths.txt"), true),
                manualDemonsSoulsDeathCounter: ManualDeathCounter.CreateFor(GameId.DemonsSouls, 7),
                eldenRingNoticeAcknowledged: true,
                eldenRingSave: new EldenRingSaveConfiguration("C:/saves/ER0000.sl2", 1),
                blackMythWukongSave: new BlackMythWukongSaveConfiguration("C:/saves/ArchiveSaveFile.1.sav"),
                eldenRingMissedDeathAdjustments: new EldenRingMissedDeathAdjustments([new("C:/saves/ER0000.sl2", 1, 3)]),
                liesOfPSave: new LiesOfPSaveConfiguration("C:/saves/SaveData-1_Character_1.sav"));
            await using (var repository = new SqliteTrackerStateRepository(root, "test.db"))
            {
                await repository.SaveAsync(original);
                string retainedPayload = await ReadPayloadAsync(root);
                await AddObsoleteEndpointAsync(root, port);
                TrackerStateLoadResult loaded = await repository.LoadAsync();
                Assert.True(loaded.IsSuccess);
                await repository.SaveAsync(loaded.State!);
                Assert.Equal(retainedPayload, await ReadPayloadAsync(root));
                await AssertNoEndpointSavedAsync(root);
            }
            await using var reopened = new SqliteTrackerStateRepository(root, "test.db");
            PersistentTrackerState state = (await reopened.LoadAsync()).State!;
            Assert.Equal(GameId.EldenRing, state.SelectedGameId);
            Assert.Equal(7, state.ManualDemonsSoulsDeathCounter.Value);
            Assert.Equal(3, state.EldenRingMissedDeathAdjustments.Get(state.EldenRingSave));
            Assert.Equal(original.OverlayConfiguration.TotalDeaths.Appearance.Title, state.OverlayConfiguration.TotalDeaths.Appearance.Title);
            Assert.Equal(original.GlobalHotkeys.IncrementVirtualKey, state.GlobalHotkeys.IncrementVirtualKey);
            Assert.Equal(original.TextExports.DeathsPath, state.TextExports.DeathsPath);
            Assert.Equal(original.BlackMythWukongSave.LocalPath, state.BlackMythWukongSave.LocalPath);
            Assert.Equal(original.LiesOfPSave.LocalPath, state.LiesOfPSave.LocalPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConfirmedImportIgnoresUnusableHistoricalTokenInDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-obsolete-overlay-{Guid.NewGuid():N}");
        try
        {
            await using (var repository = new SqliteTrackerStateRepository(root, "test.db"))
            {
                await repository.SaveAsync(PersistentTrackerState.Default);
                await AddObsoleteEndpointAsync(root, "4321");
                LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(
                    """{"settings":{"selected_game":"bloodborne"},"death_count":123}"""u8.ToArray());
                LegacyProposalApplicationResult candidate = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);
                Assert.Equal(LegacyProposalApplicationOutcome.Applied, candidate.Outcome);
                var audit = new ConfirmedLegacyImportAuditMetadata(ConfirmedLegacyImportAuditMetadata.CurrentContractVersion,
                    LegacyImportPreflightOutcome.Prepared, new string('A', 64), new string('B', 64));
                var result = await repository.CommitConfirmedLegacyImportAsync(candidate, audit);
                Assert.Equal(ConfirmedLegacyImportCommitOutcome.Committed, result.Outcome);
                await AssertNoEndpointSavedAsync(root);
            }
            await using var reopened = new SqliteTrackerStateRepository(root, "test.db");
            TrackerStateLoadResult loaded = await reopened.LoadAsync();
            Assert.True(loaded.IsSuccess);
            Assert.Equal(GameId.Bloodborne, loaded.State!.SelectedGameId);
            Assert.Equal(0, loaded.State.ManualDemonsSoulsDeathCounter.Value);
        }
        finally { Directory.Delete(root, true); }
    }

    private static SqliteConnection Connection(string root) => new(new SqliteConnectionStringBuilder
    { DataSource = Path.Combine(root, "test.db"), Pooling = false }.ToString());

    private static async Task<string> ReadPayloadAsync(string root)
    {
        await using var connection = Connection(root);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM tracker_state WHERE id=1";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AddObsoleteEndpointAsync(string root, string port)
    {
        JsonNode payload = JsonNode.Parse(await ReadPayloadAsync(root))!;
        payload["Port"] = JsonNode.Parse(port);
        await using var connection = Connection(root);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tracker_state SET payload=$payload, token=$token WHERE id=1";
        command.Parameters.AddWithValue("$payload", payload.ToJsonString());
        // Deliberately unusable historical ciphertext, never a real capability.
        command.Parameters.AddWithValue("$token", new byte[] { 0xFF, 0x00, 0x12 });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertNoEndpointSavedAsync(string root)
    {
        using JsonDocument payload = JsonDocument.Parse(await ReadPayloadAsync(root));
        Assert.False(payload.RootElement.TryGetProperty("Port", out _));
        await using var connection = Connection(root);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT token FROM tracker_state WHERE id=1";
        Assert.IsType<DBNull>(await command.ExecuteScalarAsync());
    }
}
