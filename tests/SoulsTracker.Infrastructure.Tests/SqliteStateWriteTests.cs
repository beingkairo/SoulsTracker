using System.Text.Json;
using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class SqliteStateWriteTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"souls-state-write-{Guid.NewGuid():N}");

    [Fact]
    public async Task InterruptedSavePropagatesAndPreservesCompleteRowBeforeSubsequentSave()
    {
        var interruption = new SaveInterruption();
        PersistentTrackerState previous = State(GameId.EldenRing, 7);
        PersistentTrackerState next = State(GameId.LiesOfP, 19);
        StoredRow before;
        using var cancellation = new CancellationTokenSource();
        await using (var repository = new SqliteTrackerStateRepository(root, "test.db", saveInterruption: interruption))
        {
            await repository.SaveAsync(previous);
            before = await ReadRowAsync();
            interruption.Failure = new InvalidOperationException("Synthetic precommit interruption.");
            Exception failure = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(next, cancellation.Token));
            Assert.Same(interruption.Failure, failure);
            Assert.Equal(cancellation.Token, interruption.LastToken);
            Assert.Equal(2, interruption.Calls);
            Assert.Equal(before, await ReadRowAsync());
        }
        await using (var reopened = new SqliteTrackerStateRepository(root, "test.db"))
        {
            await AssertLoadedAsync(reopened, previous);
            Assert.Equal(before, await ReadRowAsync());
            await reopened.SaveAsync(next);
            Assert.NotEqual(before.Payload, (await ReadRowAsync()).Payload);
        }
        await using var final = new SqliteTrackerStateRepository(root, "test.db");
        await AssertLoadedAsync(final, next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedImportRollsBackStateAndAuditAndAllowsLaterWrites(bool existingAudit)
    {
        var interruption = new SaveInterruption();
        PersistentTrackerState previous = State(GameId.DemonsSouls, 0);
        LegacyProposalApplicationResult candidate = Candidate(previous);
        StoredRow before;
        string? auditBefore;
        using var cancellation = new CancellationTokenSource();
        await using (var repository = new SqliteTrackerStateRepository(root, "test.db", saveInterruption: interruption))
        {
            await repository.SaveAsync(previous);
            if (existingAudit)
            {
                Assert.Equal(ConfirmedLegacyImportCommitOutcome.Committed,
                    (await repository.CommitConfirmedLegacyImportAsync(candidate, Audit())).Outcome);
                await repository.SaveAsync(previous);
            }
            before = await ReadRowAsync();
            auditBefore = await ReadAuditAsync();
            Assert.Equal(existingAudit, auditBefore is not null);
            int callsBefore = interruption.Calls;
            interruption.Failure = new InvalidOperationException("Synthetic precommit interruption.");
            ConfirmedLegacyImportCommitResult failed = await repository.CommitConfirmedLegacyImportAsync(candidate, Audit(), cancellation.Token);
            Assert.Equal(ConfirmedLegacyImportCommitOutcome.StorageUnavailable, failed.Outcome);
            Assert.Equal(callsBefore + 1, interruption.Calls);
            Assert.Equal(cancellation.Token, interruption.LastToken);
            Assert.Equal(before, await ReadRowAsync());
            Assert.Equal(auditBefore, await ReadAuditAsync());
        }
        await using (var reopened = new SqliteTrackerStateRepository(root, "test.db"))
        {
            await AssertLoadedAsync(reopened, previous);
            Assert.Equal(before, await ReadRowAsync());
            Assert.Equal(auditBefore, await ReadAuditAsync());
            ConfirmedLegacyImportCommitResult committed = await reopened.CommitConfirmedLegacyImportAsync(candidate, Audit());
            Assert.Equal(ConfirmedLegacyImportCommitOutcome.Committed, committed.Outcome);
            await AssertLoadedAsync(reopened, candidate.CandidateState!);
            using JsonDocument audit = JsonDocument.Parse((await ReadAuditAsync())!);
            Assert.Equal(existingAudit ? 2 : 1, audit.RootElement.GetArrayLength());
            string auditAfter = (await ReadAuditAsync())!;
            await reopened.SaveAsync(State(GameId.EldenRing, 23));
            Assert.Equal(auditAfter, await ReadAuditAsync());
        }
        await using var final = new SqliteTrackerStateRepository(root, "test.db");
        await AssertLoadedAsync(final, State(GameId.EldenRing, 23));
    }

    [Fact]
    public async Task RepeatedSaveAndReopenPreservesNondefaultPayloadAndSingleRow()
    {
        PersistentTrackerState expected = State(GameId.EldenRing, 7);
        StoredRow before;
        await using (var repository = new SqliteTrackerStateRepository(root, "test.db"))
        {
            await AssertLoadedAsync(repository, PersistentTrackerState.Default);
            await repository.SaveAsync(expected);
            before = await ReadRowAsync();
            await repository.SaveAsync(expected);
            Assert.Equal(before, await ReadRowAsync());
        }
        await using (var reopened = new SqliteTrackerStateRepository(root, "test.db"))
        {
            await AssertLoadedAsync(reopened, expected);
            PersistentTrackerState loaded = (await reopened.LoadAsync()).State!;
            await reopened.SaveAsync(loaded);
            Assert.Equal(before, await ReadRowAsync());
            using JsonDocument payload = JsonDocument.Parse(before.Payload);
            Assert.False(payload.RootElement.TryGetProperty("Port", out _));
            Assert.False(payload.RootElement.GetProperty("ShowGameName").GetBoolean());
            Assert.True(payload.RootElement.GetProperty("TotalCompactTitle").GetBoolean());
            await reopened.SaveAsync(State(GameId.BlackMythWukong, 31));
            Assert.NotEqual(before.Payload, (await ReadRowAsync()).Payload);
        }
        await using var final = new SqliteTrackerStateRepository(root, "test.db");
        await AssertLoadedAsync(final, State(GameId.BlackMythWukong, 31));
    }

    private PersistentTrackerState State(GameId game, long deaths) => new(
        PersistentTrackerState.CurrentSchemaVersion, game,
        new OverlayConfiguration(1, new TotalDeathsOverlayOptions(false, false, true,
            new OverlayAppearance("Synthetic deaths", "Verdana", 36, "#123456", "#234567", "#345678", 45, 8, 6,
                OverlayTextAlignment.Left, true, "#456789", 3, true, "#56789A", -3, 5, 7, 80, "#6789AB"), OverlayTitleIconMode.SkullOnly)),
        new GlobalHotkeyConfiguration(2, 118, 2, 119),
        new TextExportConfiguration(Path.Combine(root, "deaths.txt"), true),
        ManualDeathCounter.CreateFor(GameId.DemonsSouls, deaths), true,
        new EldenRingSaveConfiguration("C:/synthetic/ER0000.sl2", 1),
        new BlackMythWukongSaveConfiguration("C:/synthetic/ArchiveSaveFile.1.sav"),
        new EldenRingMissedDeathAdjustments([new("C:/synthetic/ER0000.sl2", 1, 3), new("C:/synthetic/ER0000.sl2", 2, 5)]),
        new LiesOfPSaveConfiguration("C:/synthetic/SaveData-1_Character_1.sav"));

    private static LegacyProposalApplicationResult Candidate(PersistentTrackerState destination)
    {
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze("""{"settings":{"selected_game":"bloodborne"},"death_count":123}"""u8.ToArray());
        LegacyProposalApplicationResult candidate = ConfirmedLegacyProposalApplication.Apply(analysis, destination);
        Assert.Equal(LegacyProposalApplicationOutcome.Applied, candidate.Outcome);
        return candidate;
    }

    private static ConfirmedLegacyImportAuditMetadata Audit() => new(
        ConfirmedLegacyImportAuditMetadata.CurrentContractVersion, LegacyImportPreflightOutcome.Prepared, new string('A', 64), new string('B', 64));

    private static async Task AssertLoadedAsync(SqliteTrackerStateRepository repository, PersistentTrackerState expected)
    {
        TrackerStateLoadResult load = await repository.LoadAsync();
        Assert.True(load.IsSuccess);
        PersistentTrackerState actual = Assert.IsType<PersistentTrackerState>(load.State);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.SelectedGameId, actual.SelectedGameId);
        Assert.Equal(expected.ManualDemonsSoulsDeathCounter.Value, actual.ManualDemonsSoulsDeathCounter.Value);
        Assert.Equal(expected.GlobalHotkeys, actual.GlobalHotkeys);
        Assert.Equal(JsonSerializer.Serialize(expected.TextExports), JsonSerializer.Serialize(actual.TextExports));
        Assert.Equal(JsonSerializer.Serialize(expected.OverlayConfiguration), JsonSerializer.Serialize(actual.OverlayConfiguration));
        Assert.Equal(expected.EldenRingNoticeAcknowledged, actual.EldenRingNoticeAcknowledged);
        Assert.Equal(expected.EldenRingSave.LocalPath, actual.EldenRingSave.LocalPath);
        Assert.Equal(expected.EldenRingSave.SlotIndex, actual.EldenRingSave.SlotIndex);
        Assert.Equal(expected.BlackMythWukongSave.LocalPath, actual.BlackMythWukongSave.LocalPath);
        Assert.Equal(expected.LiesOfPSave.LocalPath, actual.LiesOfPSave.LocalPath);
        Assert.Equal(expected.EldenRingMissedDeathAdjustments.ToEntries(), actual.EldenRingMissedDeathAdjustments.ToEntries());
    }

    private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
    { DataSource = Path.Combine(root, "test.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

    private async Task<StoredRow> ReadRowAsync()
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,schema_version,payload,token FROM tracker_state";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = new StoredRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2));
        Assert.Equal(1, row.Id);
        Assert.Equal(PersistentTrackerState.CurrentSchemaVersion, row.Version);
        Assert.True(reader.IsDBNull(3));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private async Task<string?> ReadAuditAsync()
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='legacy_import_audit'";
        if ((long)(await command.ExecuteScalarAsync())! == 0) return null;
        command.CommandText = "SELECT import_id,committed_at_utc,contract_version,preflight_outcome,outcome,source_fingerprint,backup_fingerprint FROM legacy_import_audit ORDER BY import_id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        return JsonSerializer.Serialize(rows);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record StoredRow(int Id, int Version, string Payload);

    private sealed class SaveInterruption : ISqliteSaveInterruption
    {
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task BeforeCommitAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            LastToken = cancellationToken;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
