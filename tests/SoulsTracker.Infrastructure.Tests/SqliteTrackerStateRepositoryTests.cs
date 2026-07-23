using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class SqliteTrackerStateRepositoryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTrackerTests", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch (IOException) { } } return Task.CompletedTask; }

    [Fact]
    public async Task FirstOpenCreatesAndReloadsStateWithoutPlaintextToken()
    {
        const string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        PersistentTrackerState state = new(1, GameId.Bloodborne, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 7), BossProgress.Empty.MarkDefeated(GameId.Bloodborne, BossId.Parse("cleric_beast")), new OverlayConfiguration(1, new OverlayEndpointConfiguration(5123, OverlayAccessToken.Parse(token)), new TotalDeathsOverlayOptions(false, true), new BossListOverlayOptions(true, BossListVisibilityMode.Remaining)), manualDemonsSoulsDeathCounter: ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls, 4));
        await using (var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector())) { Assert.True((await repository.LoadAsync()).IsSuccess); await repository.SaveAsync(state); }
        await using (var reopened = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector()))
        {
            TrackerStateLoadResult result = await reopened.LoadAsync();
            Assert.True(result.IsSuccess); Assert.Equal(7, result.State!.ManualBloodborneDeathCounter.Value); Assert.Equal(4, result.State.ManualDemonsSoulsDeathCounter.Value); Assert.True(result.State.BossProgress.IsDefeated(GameId.Bloodborne, BossId.Parse("cleric_beast"))); Assert.Equal(5123, result.State.OverlayConfiguration.Endpoint.Port);
        }
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(root, "state.db")), StringComparison.Ordinal);
        await new TimestampedSqliteMigrationBackup().CreateBeforeMigrationAsync(Path.Combine(root, "state.db"));
        string backupPath = Assert.Single(Directory.GetFiles(root, "*.pre-migration-*.bak"));
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(backupPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistedHotkeysRoundTripAndUnsupportedStoredKeysFallBackToDefaults()
    {
        var configured = new PersistentTrackerState(
            1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty,
            OverlayConfiguration.Default, new ManualBloodborneHotkeyConfiguration(0x0003, 0x41, 0x0007, 0x70));
        await using (var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector()))
        {
            await repository.LoadAsync();
            await repository.SaveAsync(configured);
        }
        await using (var reopened = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector()))
        {
            PersistentTrackerState loaded = (await reopened.LoadAsync()).State!;
            Assert.Equal(configured.ManualBloodborneHotkeys, loaded.ManualBloodborneHotkeys);
        }

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "state.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tracker_state SET payload=json_set(payload, '$.IncrementVirtualKey', 1) WHERE id=1";
            await command.ExecuteNonQueryAsync();
        }
        await using var corrupted = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        Assert.Equal(ManualBloodborneHotkeyConfiguration.Default, (await corrupted.LoadAsync()).State!.ManualBloodborneHotkeys);
    }

    [Fact]
    public async Task EldenRingNoticeAcknowledgementRoundTripsLocally()
    {
        PersistentTrackerState configured = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true);

        await using (var repository = new SqliteTrackerStateRepository(root, "elden-notice.db", new ReversingProtector()))
        {
            await repository.LoadAsync();
            await repository.SaveAsync(configured);
        }

        await using var reopened = new SqliteTrackerStateRepository(root, "elden-notice.db", new ReversingProtector());
        PersistentTrackerState restored = (await reopened.LoadAsync()).State!;
        Assert.True(restored.EldenRingNoticeAcknowledged);
        Assert.Equal(GameId.EldenRing, restored.SelectedGameId);
    }

    [Fact]
    public async Task EldenRingSaveSelectionAndProfileSlotRoundTripLocally()
    {
        const string savePath = "C:\\local-only\\ER0000.sl2";
        PersistentTrackerState configured = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true,
            eldenRingSave: new EldenRingSaveConfiguration(savePath, 3, EldenRingBossListScope.ShadowOfTheErdtree, requiredBossesOnly: true));

        await using (var repository = new SqliteTrackerStateRepository(root, "elden-save.db", new ReversingProtector()))
        {
            await repository.LoadAsync();
            await repository.SaveAsync(configured);
        }

        await using var reopened = new SqliteTrackerStateRepository(root, "elden-save.db", new ReversingProtector());
        EldenRingSaveConfiguration restored = (await reopened.LoadAsync()).State!.EldenRingSave;
        Assert.Equal(savePath, restored.LocalPath);
        Assert.Equal(3, restored.SlotIndex);
        Assert.Equal(EldenRingBossListScope.ShadowOfTheErdtree, restored.BossListScope);
        Assert.True(restored.RequiredBossesOnly);
    }

    [Fact]
    public async Task DeathSoundConfigurationRoundTripsAndMalformedPersistedPathFallsBackSafely()
    {
        const string path = "C:\\local-only\\death.wav";
        PersistentTrackerState configured = new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default, deathSound: new DeathSoundConfiguration(path, true, 42));
        await using (var repository = new SqliteTrackerStateRepository(root, "sound.db", new ReversingProtector())) { await repository.LoadAsync(); await repository.SaveAsync(configured); }
        await using (var reopened = new SqliteTrackerStateRepository(root, "sound.db", new ReversingProtector()))
        {
            DeathSoundConfiguration sound = (await reopened.LoadAsync()).State!.DeathSound;
            Assert.Equal(path, sound.LocalPath); Assert.True(sound.IsEnabled); Assert.Equal(42, sound.Volume);
        }
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "sound.db")};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tracker_state SET payload=json_set(payload, '$.DeathSoundPath', 'not-audio.exe') WHERE id=1"; await command.ExecuteNonQueryAsync();
        }
        await using var malformed = new SqliteTrackerStateRepository(root, "sound.db", new ReversingProtector());
        Assert.Equal(DeathSoundConfiguration.Default, (await malformed.LoadAsync()).State!.DeathSound);
    }

    [Fact]
    public async Task TextExportConfigurationRoundTripsAndInvalidExtensionsFallBack()
    {
        PersistentTrackerState configured = new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default, textExports: new TextExportConfiguration("C:\\exports\\deaths.txt", true, "C:\\exports\\bosses.txt", true));
        await using (var repository = new SqliteTrackerStateRepository(root, "exports.db", new ReversingProtector())) { await repository.LoadAsync(); await repository.SaveAsync(configured); }
        await using (var reopened = new SqliteTrackerStateRepository(root, "exports.db", new ReversingProtector()))
        {
            TextExportConfiguration exports = (await reopened.LoadAsync()).State!.TextExports;
            Assert.True(exports.DeathsEnabled); Assert.True(exports.BossListEnabled);
        }
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "exports.db")};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE tracker_state SET payload=json_set(payload, '$.DeathsExportPath', 'bad.exe') WHERE id=1"; await command.ExecuteNonQueryAsync();
        }
        await using var invalid = new SqliteTrackerStateRepository(root, "exports.db", new ReversingProtector());
        Assert.Equal(TextExportConfiguration.Default, (await invalid.LoadAsync()).State!.TextExports);
    }

    [Fact]
    public async Task OverlayAppearanceRoundTripsThroughSQLite()
    {
        OverlayAppearance total = new("Deaths", "Verdana", 44, "#010203", "#040506", "#070809", 70, 12, 6, OverlayTextAlignment.Center, true, "#AABBCC", 2, true, "#DDEEFF", -3, 4, 5);
        OverlayAppearance boss = new("Bosses", "Arial", 22, "#111213", "#141516", "#171819", 20, 4, 2, OverlayTextAlignment.Right, true, "#212223", 3, true, "#242526", 6, -7, 8);
        PersistentTrackerState configured = new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty,
            new OverlayConfiguration(1, OverlayEndpointConfiguration.Unassigned, new TotalDeathsOverlayOptions(true, false, true, total), new BossListOverlayOptions(true, BossListVisibilityMode.Defeated, boss, "#202122", DefeatedBossTreatment.Hidden, false, "#232425", 7)));
        await using (var repository = new SqliteTrackerStateRepository(root, "appearance.db", new ReversingProtector())) { await repository.LoadAsync(); await repository.SaveAsync(configured); }
        await using (var reopened = new SqliteTrackerStateRepository(root, "appearance.db", new ReversingProtector()))
        {
            PersistentTrackerState loaded = (await reopened.LoadAsync()).State!;
            Assert.Equal("Deaths", loaded.OverlayConfiguration.TotalDeaths.Appearance.Title);
            Assert.True(loaded.OverlayConfiguration.TotalDeaths.CompactTitle);
            Assert.Equal(OverlayTextAlignment.Left, loaded.OverlayConfiguration.TotalDeaths.Appearance.Alignment);
            Assert.True(loaded.OverlayConfiguration.TotalDeaths.Appearance.OutlineEnabled);
            Assert.Equal("#AABBCC", loaded.OverlayConfiguration.TotalDeaths.Appearance.OutlineColor);
            Assert.Equal(2, loaded.OverlayConfiguration.TotalDeaths.Appearance.OutlineWidth);
            Assert.True(loaded.OverlayConfiguration.TotalDeaths.Appearance.ShadowEnabled);
            Assert.Equal(-3, loaded.OverlayConfiguration.TotalDeaths.Appearance.ShadowOffsetX);
            Assert.Equal("Bosses", loaded.OverlayConfiguration.BossList.Appearance.Title);
            Assert.True(loaded.OverlayConfiguration.BossList.Appearance.OutlineEnabled);
            Assert.Equal("#212223", loaded.OverlayConfiguration.BossList.Appearance.OutlineColor);
            Assert.Equal(3, loaded.OverlayConfiguration.BossList.Appearance.OutlineWidth);
            Assert.True(loaded.OverlayConfiguration.BossList.Appearance.ShadowEnabled);
            Assert.Equal(-7, loaded.OverlayConfiguration.BossList.Appearance.ShadowOffsetY);
            Assert.Equal(DefeatedBossTreatment.Nothing, loaded.OverlayConfiguration.BossList.DefeatedTreatment);
            Assert.False(loaded.OverlayConfiguration.BossList.ShowCheckmark);
            Assert.Equal(7, loaded.OverlayConfiguration.BossList.MaximumVisibleCount);
        }
    }

    [Fact]
    public async Task LoadAppliesTheVersionedBossTitleCorrectionOnceAndPreservesAllOtherAppearanceFields()
    {
        OverlayAppearance historicalAccidentalBossAppearance = new("TOTAL DEATHS", "Verdana", 44, "#010203", "#040506", "#070809", 70, 12, 6, OverlayTextAlignment.Right);
        PersistentTrackerState legacy = new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty,
            new OverlayConfiguration(1, OverlayEndpointConfiguration.Unassigned, TotalDeathsOverlayOptions.Default,
                new BossListOverlayOptions(true, BossListVisibilityMode.All, historicalAccidentalBossAppearance)));
        OverlayAppearance nonmatchingLegacyAppearance = new("MY BOSSES", "Arial", 31, "#111213", "#141516", "#171819", 20, 4, 2, OverlayTextAlignment.Center);
        PersistentTrackerState nonmatchingLegacy = new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty,
            new OverlayConfiguration(1, OverlayEndpointConfiguration.Unassigned, TotalDeathsOverlayOptions.Default,
                new BossListOverlayOptions(true, BossListVisibilityMode.All, nonmatchingLegacyAppearance)));
        PersistentTrackerState currentExactTitle = new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty,
            new OverlayConfiguration(1, OverlayEndpointConfiguration.Unassigned, TotalDeathsOverlayOptions.Default,
                new BossListOverlayOptions(true, BossListVisibilityMode.All, historicalAccidentalBossAppearance)));

        await using (var repository = new SqliteTrackerStateRepository(root, "legacy-boss-default.db", new ReversingProtector()))
        {
            await repository.LoadAsync();
            await repository.SaveAsync(legacy);
        }
        await SetBossListTitleCorrectionMarkerAsync("legacy-boss-default.db", 2);
        await using (var reopened = new SqliteTrackerStateRepository(root, "legacy-boss-default.db", new ReversingProtector()))
        {
            PersistentTrackerState loaded = (await reopened.LoadAsync()).State!;
            Assert.Equal("Boss List", loaded.OverlayConfiguration.BossList.Appearance.Title);
            Assert.Equal("Total Deaths", loaded.OverlayConfiguration.TotalDeaths.Appearance.Title);
            Assert.Equal(historicalAccidentalBossAppearance.FontFamily, loaded.OverlayConfiguration.BossList.Appearance.FontFamily);
            Assert.Equal(historicalAccidentalBossAppearance.FontSize, loaded.OverlayConfiguration.BossList.Appearance.FontSize);
            Assert.Equal(historicalAccidentalBossAppearance.TextColor, loaded.OverlayConfiguration.BossList.Appearance.TextColor);
            Assert.Equal(historicalAccidentalBossAppearance.Alignment, loaded.OverlayConfiguration.BossList.Appearance.Alignment);
        }
        await using (var reloaded = new SqliteTrackerStateRepository(root, "legacy-boss-default.db", new ReversingProtector()))
        {
            Assert.Equal("Boss List", (await reloaded.LoadAsync()).State!.OverlayConfiguration.BossList.Appearance.Title);
        }

        await using (var repository = new SqliteTrackerStateRepository(root, "nonmatching-legacy-boss-title.db", new ReversingProtector()))
        {
            await repository.LoadAsync();
            await repository.SaveAsync(nonmatchingLegacy);
        }
        await SetBossListTitleCorrectionMarkerAsync("nonmatching-legacy-boss-title.db", 2);
        await using (var reopened = new SqliteTrackerStateRepository(root, "nonmatching-legacy-boss-title.db", new ReversingProtector()))
        {
            Assert.Equal("MY BOSSES", (await reopened.LoadAsync()).State!.OverlayConfiguration.BossList.Appearance.Title);
        }
        await using (var repository = new SqliteTrackerStateRepository(root, "current-exact-boss-title.db", new ReversingProtector()))
        {
            await repository.LoadAsync();
            await repository.SaveAsync(currentExactTitle);
        }
        await using (var reopened = new SqliteTrackerStateRepository(root, "current-exact-boss-title.db", new ReversingProtector()))
        {
            Assert.Equal("TOTAL DEATHS", (await reopened.LoadAsync()).State!.OverlayConfiguration.BossList.Appearance.Title);
        }
    }

    [Fact]
    public async Task LoadReevaluatesAnEarlierCorrectionMarkerThenCoordinatorReceivesTheCorrectedState()
    {
        OverlayAppearance accidentalAppearance = new("TOTAL DEATHS", "Arial", 31, "#111213", "#141516", "#171819", 20, 4, 2, OverlayTextAlignment.Center);
        PersistentTrackerState legacy = new(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty,
            new OverlayConfiguration(1, OverlayEndpointConfiguration.Unassigned, TotalDeathsOverlayOptions.Default,
                new BossListOverlayOptions(true, BossListVisibilityMode.All, accidentalAppearance)));

        await using (var repository = new SqliteTrackerStateRepository(root, "earlier-marker.db", new ReversingProtector()))
        {
            await repository.LoadAsync();
            await repository.SaveAsync(legacy);
        }
        await SetBossListTitleCorrectionMarkerAsync("earlier-marker.db", 2);

        await using (var repository = new SqliteTrackerStateRepository(root, "earlier-marker.db", new ReversingProtector()))
        await using (var coordinator = new SerializedTrackerCoordinator(repository, new NoOpPublisher()))
        {
            TrackerStateLoadResult result = await coordinator.InitializeAsync();
            Assert.True(result.IsSuccess);
            Assert.Equal("Boss List", result.State!.OverlayConfiguration.BossList.Appearance.Title);
        }

        await using (var reloaded = new SqliteTrackerStateRepository(root, "earlier-marker.db", new ReversingProtector()))
        {
            Assert.Equal("Boss List", (await reloaded.LoadAsync()).State!.OverlayConfiguration.BossList.Appearance.Title);
        }
    }

    [Fact]
    public async Task CorruptStoreReturnsFailureAndDoesNotDefault()
    {
        Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, "broken.db"), "not sqlite");
        await using var repository = new SqliteTrackerStateRepository(root, "broken.db", new ReversingProtector());
        TrackerStateLoadResult result = await repository.LoadAsync();
        Assert.False(result.IsSuccess); Assert.NotEqual(TrackerStateLoadFailureKind.None, result.FailureKind);
    }

    [Fact]
    public async Task TruncatedStoreReturnsTypedFailureWithoutReplacingOriginal()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "truncated.db");
        await File.WriteAllBytesAsync(path, [0x53, 0x51, 0x4C]);
        byte[] original = await File.ReadAllBytesAsync(path);
        await using var repository = new SqliteTrackerStateRepository(root, "truncated.db", new ReversingProtector());
        TrackerStateLoadResult result = await repository.LoadAsync();
        Assert.False(result.IsSuccess); Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SecondWriterIsRejected()
    {
        await using var first = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        Assert.Throws<InvalidOperationException>(() => new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector()));
    }

    [Fact]
    public async Task InvalidRootEscapeIsRejectedAndBackupIsSameDirectoryWithoutToken()
    {
        Assert.Throws<ArgumentException>(() => new SqliteTrackerStateRepository(root, "../escape.db", new ReversingProtector()));
        await using var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        await repository.LoadAsync();
        var backup = new TimestampedSqliteMigrationBackup();
        await backup.CreateBeforeMigrationAsync(Path.Combine(root, "state.db"));
        string backupPath = Assert.Single(Directory.GetFiles(root, "*.pre-migration-*.bak"));
        Assert.Equal(root, Path.GetDirectoryName(backupPath));
    }

    [Fact]
    public async Task UnsupportedVersionIsActionableAndLeavesDatabaseUnchanged()
    {
        await using (var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector())) { await repository.LoadAsync(); }
        string path = Path.Combine(root, "state.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False")) { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE tracker_state SET schema_version=99 WHERE id=1"; await command.ExecuteNonQueryAsync(); }
        byte[] before = await File.ReadAllBytesAsync(path);
        await using var reopened = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        TrackerStateLoadResult result = await reopened.LoadAsync();
        Assert.Equal(TrackerStateLoadFailureKind.UnsupportedVersion, result.FailureKind); Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task PreCommitInterruptionLeavesPriorCompleteState()
    {
        await using (var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector())) { await repository.LoadAsync(); await repository.SaveAsync(new PersistentTrackerState(1, GameId.Bloodborne, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default)); }
        await using (var interrupted = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector(), saveInterruption: new ThrowBeforeCommit())) { await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.SaveAsync(new PersistentTrackerState(1, GameId.Ds1, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default))); }
        await using var reopened = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        Assert.Equal(GameId.Bloodborne, (await reopened.LoadAsync()).State!.SelectedGameId);
    }

    [Fact]
    public async Task ApprovedMigrationCreatesBackupBeforeChangingVersion()
    {
        await using (var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector())) { await repository.LoadAsync(); }
        string path = Path.Combine(root, "state.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False")) { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE tracker_state SET schema_version=0 WHERE id=1"; await command.ExecuteNonQueryAsync(); }
        var migration = new ZeroToOneMigration();
        await using var migrated = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector(), migration: migration);
        Assert.True((await migrated.LoadAsync()).IsSuccess); Assert.True(migration.Ran); Assert.Single(Directory.GetFiles(root, "*.pre-migration-*.bak"));
    }

    [Fact]
    public async Task ConfirmedImportCommitsCandidateAndSafeAuditTogetherFromAcceptedFixtureData()
    {
        const string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var destination = new PersistentTrackerState(
            PersistentTrackerState.CurrentSchemaVersion,
            selectedGameId: null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            new OverlayConfiguration(
                OverlayConfiguration.CurrentSchemaVersion,
                new OverlayEndpointConfiguration(45781, OverlayAccessToken.Parse(token)),
                new TotalDeathsOverlayOptions(isEnabled: false, showGameName: false),
                new BossListOverlayOptions(isEnabled: false, BossListVisibilityMode.Remaining)));
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(ReadAcceptedFixture());
        LegacyProposalApplicationResult candidate = ConfirmedLegacyProposalApplication.Apply(analysis, destination);

        await using var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        await repository.SaveAsync(destination);
        ConfirmedLegacyImportCommitResult result = await repository.CommitConfirmedLegacyImportAsync(candidate, AcceptedAuditMetadata());

        Assert.Equal(ConfirmedLegacyImportCommitOutcome.Committed, result.Outcome);
        Assert.Matches("^[0-9a-f]{32}$", Assert.IsType<string>(result.ImportId));
        Assert.NotNull(result.CommittedAtUtc);
        TrackerStateLoadResult reloaded = await repository.LoadAsync();
        Assert.Equal(GameId.Ds3, reloaded.State!.SelectedGameId);
        Assert.True(reloaded.State.BossProgress.IsDefeated(GameId.Ds1, BossId.Parse("asylum_demon")));
        Assert.Equal(0, reloaded.State.ManualBloodborneDeathCounter.Value);
        Assert.Equal(45781, reloaded.State.OverlayConfiguration.Endpoint.Port);
        Assert.Equal(BossListVisibilityMode.All, reloaded.State.OverlayConfiguration.BossList.VisibilityMode);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand audit = connection.CreateCommand();
        audit.CommandText = "SELECT import_id, contract_version, preflight_outcome, outcome, source_fingerprint, backup_fingerprint FROM legacy_import_audit";
        await using SqliteDataReader reader = await audit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(result.ImportId, reader.GetString(0));
        Assert.Equal(ConfirmedLegacyImportAuditMetadata.CurrentContractVersion, reader.GetInt32(1));
        Assert.Equal((int)LegacyImportPreflightOutcome.Prepared, reader.GetInt32(2));
        Assert.Equal((int)ConfirmedLegacyImportCommitOutcome.Committed, reader.GetInt32(3));
        Assert.Equal(FixtureFingerprint(), reader.GetString(4));
        Assert.Equal(FixtureFingerprint(), reader.GetString(5));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [MemberData(nameof(PopulatedDestinations))]
    public async Task ConfirmedImportRechecksStoredPopulationAndWritesNeitherStateNorAudit(PersistentTrackerState stored, ConfirmedLegacyImportCommitOutcome expected)
    {
        await using var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        await repository.SaveAsync(stored);
        LegacyProposalApplicationResult candidate = CreateCandidate();

        ConfirmedLegacyImportCommitResult result = await repository.CommitConfirmedLegacyImportAsync(candidate, AcceptedAuditMetadata());

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.ImportId);
        Assert.Null(result.CommittedAtUtc);
        TrackerStateLoadResult reloaded = await repository.LoadAsync();
        Assert.Equal(stored.SelectedGameId, reloaded.State!.SelectedGameId);
        Assert.Equal(stored.ManualBloodborneDeathCounter.Value, reloaded.State.ManualBloodborneDeathCounter.Value);
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Fact]
    public async Task CandidatePreparedAgainstAnEmptyStateIsRefusedWhenTheStoredDestinationChangesBeforeCommit()
    {
        LegacyProposalApplicationResult candidate = CreateCandidate();
        var populatedState = new PersistentTrackerState(
            PersistentTrackerState.CurrentSchemaVersion,
            selectedGameId: null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 2),
            BossProgress.Empty,
            OverlayConfiguration.Default);
        await using var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        await repository.SaveAsync(populatedState);

        ConfirmedLegacyImportCommitResult result = await repository.CommitConfirmedLegacyImportAsync(candidate, AcceptedAuditMetadata());

        Assert.Equal(ConfirmedLegacyImportCommitOutcome.DestinationHasManualBloodborneDeaths, result.Outcome);
        Assert.Equal(2, (await repository.LoadAsync()).State!.ManualBloodborneDeathCounter.Value);
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Fact]
    public async Task InterruptedConfirmedImportRollsBackBothCandidateAndAudit()
    {
        await using (var initial = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector()))
        {
            await initial.SaveAsync(PersistentTrackerState.Default);
        }

        await using var interrupted = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector(), saveInterruption: new ThrowBeforeCommit());
        ConfirmedLegacyImportCommitResult result = await interrupted.CommitConfirmedLegacyImportAsync(CreateCandidate(), AcceptedAuditMetadata());

        Assert.Equal(ConfirmedLegacyImportCommitOutcome.StorageUnavailable, result.Outcome);
        TrackerStateLoadResult reloaded = await interrupted.LoadAsync();
        Assert.Null(reloaded.State!.SelectedGameId);
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Fact]
    public async Task ExistingWriterOwnershipPreventsAConcurrentConfirmedImportWriter()
    {
        await using var owner = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        Assert.Throws<InvalidOperationException>(() => new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector()));

        ConfirmedLegacyImportCommitResult result = await owner.CommitConfirmedLegacyImportAsync(CreateCandidate(), AcceptedAuditMetadata());

        Assert.Equal(ConfirmedLegacyImportCommitOutcome.Committed, result.Outcome);
    }

    [Fact]
    public async Task InvalidAuditAndPublicResultRemainSafeAndDoNotWrite()
    {
        await using var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        await repository.SaveAsync(PersistentTrackerState.Default);
        var invalid = new ConfirmedLegacyImportAuditMetadata(
            contractVersion: 999,
            LegacyImportPreflightOutcome.Prepared,
            "not-a-fingerprint",
            "not-a-fingerprint");

        ConfirmedLegacyImportCommitResult result = await repository.CommitConfirmedLegacyImportAsync(CreateCandidate(), invalid);
        string serialized = JsonSerializer.Serialize(result);

        Assert.Equal(ConfirmedLegacyImportCommitOutcome.InvalidAuditMetadata, result.Outcome);
        Assert.DoesNotContain("not-a-fingerprint", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(ConfirmedLegacyImportCommitResult).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.PropertyType == typeof(Exception) ||
                property.Name.Contains("Path", StringComparison.Ordinal) ||
                property.Name.Contains("Source", StringComparison.Ordinal) ||
                property.Name.Contains("Backup", StringComparison.Ordinal) ||
                property.Name.Contains("Token", StringComparison.Ordinal));
        Assert.All(typeof(ConfirmedLegacyImportCommitResult).GetProperties(BindingFlags.Instance | BindingFlags.Public), property => Assert.False(property.CanWrite));
        Assert.Equal(0L, await AuditRowCountAsync());
        Assert.Null((await repository.LoadAsync()).State!.SelectedGameId);
    }

    [Fact]
    public void ConfirmedImportSourceDoesNotAccessLegacySourcesOrDesktop()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "SoulsTracker.Infrastructure",
            "SqliteTrackerStateRepository.cs"));
        string source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("LegacyImportPreflight", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Copy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SoulsTracker.Desktop", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonAppliedP6ProposalResultIsRefusedBeforeAnyStateOrAuditWrite()
    {
        await using var repository = new SqliteTrackerStateRepository(root, "state.db", new ReversingProtector());
        await repository.SaveAsync(PersistentTrackerState.Default);
        LegacyStateAnalysis rejected = LegacyStateAnalyzer.Analyze("{\"unfinished\":"u8.ToArray());
        LegacyProposalApplicationResult nonApplied = ConfirmedLegacyProposalApplication.Apply(rejected, PersistentTrackerState.Default);

        ConfirmedLegacyImportCommitResult result = await repository.CommitConfirmedLegacyImportAsync(nonApplied, AcceptedAuditMetadata());

        Assert.Equal(ConfirmedLegacyImportCommitOutcome.InvalidCandidate, result.Outcome);
        Assert.Null((await repository.LoadAsync()).State!.SelectedGameId);
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Fact]
    public void CommitApiDoesNotExposeARawPersistentStateBypass()
    {
        MethodInfo method = typeof(SqliteTrackerStateRepository).GetMethod(
            nameof(SqliteTrackerStateRepository.CommitConfirmedLegacyImportAsync))
            ?? throw new InvalidOperationException("The confirmed-import operation is missing.");

        Assert.Equal(typeof(LegacyProposalApplicationResult), method.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(PersistentTrackerState));
    }

    public static IEnumerable<object[]> PopulatedDestinations()
    {
        yield return [
            new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, GameId.Ds1, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default),
            ConfirmedLegacyImportCommitOutcome.DestinationHasSelectedGame,
        ];
        yield return [
            new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty.MarkDefeated(GameId.Ds1, BossId.Parse("asylum_demon")), OverlayConfiguration.Default),
            ConfirmedLegacyImportCommitOutcome.DestinationHasDefeatedBossProgress,
        ];
        yield return [
            new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 1), BossProgress.Empty, OverlayConfiguration.Default),
            ConfirmedLegacyImportCommitOutcome.DestinationHasManualBloodborneDeaths,
        ];
    }

    private static ConfirmedLegacyImportAuditMetadata AcceptedAuditMetadata() => new(
        ConfirmedLegacyImportAuditMetadata.CurrentContractVersion,
        LegacyImportPreflightOutcome.Prepared,
        FixtureFingerprint(),
        FixtureFingerprint());

    private static LegacyProposalApplicationResult CreateCandidate()
    {
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(ReadAcceptedFixture());
        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);
        Assert.Equal(LegacyProposalApplicationOutcome.Applied, result.Outcome);
        return result;
    }

    private async Task<long> AuditRowCountAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='legacy_import_audit'";
        long tableCount = (long)(await command.ExecuteScalarAsync() ?? 0L);
        if (tableCount == 0)
        {
            return 0;
        }

        command.CommandText = "SELECT COUNT(*) FROM legacy_import_audit";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static byte[] ReadAcceptedFixture()
    {
        string fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "docs",
            "fixtures",
            "legacy-state.real-redacted.json"));
        return File.ReadAllBytes(fixturePath);
    }

    private async Task SetBossListTitleCorrectionMarkerAsync(string databaseFileName, int version)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, databaseFileName)};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM tracker_state WHERE id=1";
        JsonObject payload = JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!.AsObject();
        payload["BossListTitleCorrectionMigrationVersion"] = version;
        command.CommandText = "UPDATE tracker_state SET payload=$payload WHERE id=1";
        command.Parameters.AddWithValue("$payload", payload.ToJsonString());
        await command.ExecuteNonQueryAsync();
    }

    private static string FixtureFingerprint() => Convert.ToHexString(SHA256.HashData(ReadAcceptedFixture()));

    private sealed class NoOpPublisher : ITrackerStateChangePublisher { public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class ReversingProtector : IStateSecretProtector { public byte[] Protect(byte[] value) => value.Select(static b => (byte)(b ^ 0xA5)).ToArray(); public byte[] Unprotect(byte[] value) => value.Select(static b => (byte)(b ^ 0xA5)).ToArray(); }
    private sealed class ThrowBeforeCommit : ISqliteSaveInterruption { public Task BeforeCommitAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("interrupted"); }
    private sealed class ZeroToOneMigration : ISqliteStateMigration
    {
        public bool Ran { get; private set; }
        public bool CanMigrate(int fromVersion, int targetVersion) => fromVersion == 0 && targetVersion == 1;
        public async Task MigrateAsync(string databasePath, int fromVersion, int targetVersion, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"); await connection.OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE tracker_state SET schema_version=1 WHERE id=1"; await command.ExecuteNonQueryAsync(cancellationToken); Ran = true;
        }
    }
}
