using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

/// <summary>
/// P6 acceptance evidence. These tests use only the redacted fixture beneath a
/// synthetic temporary root; they never query the current user's ApplicationData.
/// </summary>
public sealed class MigrationEndToEndAcceptanceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 12, 4, 5, 6, 789, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTrackerP606Acceptance", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConfirmedImportTraversesApprovedDiscoveryThroughDesktopSqliteAuditAndPublisherWhileRetainingSourceAndBackup()
    {
        byte[] sourceBytes = ReadAcceptedFixture();
        string sourcePath = CreateCandidate("SoulsTracker", sourceBytes);
        PersistentTrackerState destination = EmptyDestinationWithRetainedOverlaySettings();
        await using Flow flow = await CreateFlowAsync(destination);

        flow.Import.OfferIfEligible(flow.Tracker.CurrentState!);

        LegacyImportCandidate candidate = Assert.Single(flow.Import.Candidates);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal(0, flow.Preflight.Calls);
        Assert.Empty(FindBackups("SoulsTracker"));
        Assert.Equal(0L, await AuditRowCountAsync());
        Assert.Empty(flow.Publisher.Notifications);

        await flow.Import.ReviewAsync(candidate);

        Assert.Equal(LegacyImportPreflightOutcome.Prepared, flow.Import.Review!.Outcome);
        Assert.True(flow.Import.Review.BackupCreated);
        Assert.True(flow.Import.CanImport);
        Assert.Equal(1, flow.Preflight.Calls);
        string backupPath = Assert.Single(FindBackups("SoulsTracker"));
        Assert.Equal(Path.GetDirectoryName(sourcePath), Path.GetDirectoryName(backupPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(backupPath));

        await flow.Import.ImportReviewedSettingsAsync();

        PersistentTrackerState committed = Assert.IsType<PersistentTrackerState>(flow.Tracker.CurrentState);
        Assert.Equal(GameId.Ds3, committed.SelectedGameId);
        Assert.True(committed.BossProgress.IsDefeated(GameId.Ds1, BossId.Parse("asylum_demon")));
        Assert.True(committed.BossProgress.IsDefeated(GameId.Ds2, BossId.Parse("last_giant")));
        Assert.True(committed.BossProgress.IsDefeated(GameId.Ds3, BossId.Parse("iudex_gundyr")));
        Assert.True(committed.BossProgress.IsDefeated(GameId.Sekiro, BossId.Parse("gyoubu_oniwa")));
        Assert.Equal(0, committed.ManualBloodborneDeathCounter.Value);
        Assert.Equal(45781, committed.OverlayConfiguration.Endpoint.Port);
        Assert.False(committed.OverlayConfiguration.TotalDeaths.IsEnabled);
        Assert.False(committed.OverlayConfiguration.TotalDeaths.ShowGameName);
        Assert.False(committed.OverlayConfiguration.BossList.IsEnabled);
        Assert.Equal(BossListVisibilityMode.All, committed.OverlayConfiguration.BossList.VisibilityMode);
        Assert.Equal(GameId.Ds3, flow.Tracker.SelectedGame!.GameId);
        Assert.Single(flow.Publisher.Notifications);
        Assert.Equal(TrackerCommandType.LegacyImport, flow.Publisher.Notifications[0].CommandType);
        Assert.Equal(GameId.Ds3, flow.Publisher.Notifications[0].State.SelectedGameId);

        await flow.Coordinator.SubmitAsync(new SetBossDefeatedCommand(GameId.Ds3, BossId.Parse("vordt"), true));

        TrackerStateLoadResult persisted = await flow.Repository.LoadAsync();
        Assert.Equal(GameId.Ds3, persisted.State!.SelectedGameId);
        Assert.True(persisted.State.BossProgress.IsDefeated(GameId.Ds3, BossId.Parse("vordt")));
        Assert.Equal(2, flow.Publisher.Notifications.Count);
        Assert.Equal(GameId.Ds3, flow.Publisher.Notifications[1].State.SelectedGameId);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(backupPath));

        string audit = await ReadAuditAsync();
        Assert.Equal(1L, await AuditRowCountAsync());
        Assert.Contains(Fingerprint(sourceBytes), audit, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(backupPath, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.UTF8.GetString(sourceBytes), audit, StringComparison.Ordinal);
        Assert.DoesNotContain("death_sfx_path", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", audit, StringComparison.Ordinal);
        AssertSecretSafe(flow.Import.Review, root, sourcePath, backupPath);
        AssertSecretSafe(flow.Import.StatusMessage, root, sourcePath, backupPath);
    }

    [Fact]
    public async Task DiscoveryIsInertAndNoCandidateDoesNotCreateAnOfferOrTouchMigrationServices()
    {
        await using Flow flow = await CreateFlowAsync(PersistentTrackerState.Default);

        flow.Import.OfferIfEligible(flow.Tracker.CurrentState!);

        Assert.Empty(flow.Import.Candidates);
        Assert.False(flow.Import.OfferVisible);
        Assert.Equal(1, flow.Locator.DiscoverCalls);
        Assert.Equal(0, flow.Preflight.Calls);
        Assert.Empty(flow.Publisher.Notifications);
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Fact]
    public async Task SeveralCandidatesRequireExplicitSelectionAndCancellationBeforeReviewDoesNoSourceIo()
    {
        byte[] sourceBytes = ReadAcceptedFixture();
        string firstPath = CreateCandidate("SoulsTracker", sourceBytes);
        string secondPath = CreateCandidate("DeathGambit", sourceBytes);
        await using Flow flow = await CreateFlowAsync(PersistentTrackerState.Default);

        flow.Import.OfferIfEligible(flow.Tracker.CurrentState!);
        await flow.Import.ReviewAsync(candidate: null);

        Assert.Equal(2, flow.Import.Candidates.Count);
        Assert.Equal("Choose one legacy settings source to review.", flow.Import.StatusMessage);
        Assert.Equal(0, flow.Preflight.Calls);
        Assert.Empty(FindBackups("SoulsTracker"));
        Assert.Empty(FindBackups("DeathGambit"));

        flow.Import.Cancel();

        Assert.Equal(sourceBytes, File.ReadAllBytes(firstPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(secondPath));
        Assert.Equal(0, flow.Preflight.Calls);
        Assert.Empty(flow.Publisher.Notifications);
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Fact]
    public async Task PopulatedDestinationDoesNotDiscoverOrAccessAnApprovedSource()
    {
        string sourcePath = CreateCandidate("SoulsTracker", ReadAcceptedFixture());
        PersistentTrackerState populated = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.Bloodborne,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default);
        await using Flow flow = await CreateFlowAsync(populated);

        flow.Import.OfferIfEligible(flow.Tracker.CurrentState!);

        Assert.Empty(flow.Import.Candidates);
        Assert.Equal(0, flow.Locator.DiscoverCalls);
        Assert.Equal(0, flow.Preflight.Calls);
        Assert.Empty(FindBackups("SoulsTracker"));
        Assert.Equal(ReadAcceptedFixture(), File.ReadAllBytes(sourcePath));
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Fact]
    public async Task CancellationAfterPreparedReviewRetainsBackupWithoutDestinationAuditOrPublisherChange()
    {
        byte[] sourceBytes = ReadAcceptedFixture();
        string sourcePath = CreateCandidate("SoulsTracker", sourceBytes);
        await using Flow flow = await CreateFlowAsync(PersistentTrackerState.Default);

        flow.Import.OfferIfEligible(flow.Tracker.CurrentState!);
        await flow.Import.ReviewAsync(Assert.Single(flow.Import.Candidates));
        string backupPath = Assert.Single(FindBackups("SoulsTracker"));
        flow.Import.Cancel();
        await flow.Import.ImportReviewedSettingsAsync();

        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(backupPath));
        Assert.Null((await flow.Repository.LoadAsync()).State!.SelectedGameId);
        Assert.Empty(flow.Publisher.Notifications);
        Assert.Equal(0L, await AuditRowCountAsync());
    }

    [Theory]
    [InlineData(LegacyImportPreflightOutcome.Rejected)]
    [InlineData(LegacyImportPreflightOutcome.Unavailable)]
    [InlineData(LegacyImportPreflightOutcome.VerificationFailed)]
    public async Task FailedPreflightOutcomesNeverEnableFinalImportOrCommit(LegacyImportPreflightOutcome expected)
    {
        byte[] original = expected == LegacyImportPreflightOutcome.Rejected
            ? "{\"unfinished\":"u8.ToArray()
            : ReadAcceptedFixture();
        string sourcePath = CreateCandidate("SoulsTracker", original);
        LegacyImportPreflight preflight = expected == LegacyImportPreflightOutcome.VerificationFailed
            ? new LegacyImportPreflight(() => FixedTime, () => File.WriteAllText(sourcePath, "{\"changed\":true}"))
            : new LegacyImportPreflight(() => FixedTime);
        await using Flow flow = await CreateFlowAsync(PersistentTrackerState.Default, preflight);

        flow.Import.OfferIfEligible(flow.Tracker.CurrentState!);
        LegacyImportCandidate candidate = Assert.Single(flow.Import.Candidates);
        if (expected == LegacyImportPreflightOutcome.Unavailable) File.Delete(sourcePath);
        await flow.Import.ReviewAsync(candidate);
        await flow.Import.ImportReviewedSettingsAsync();

        Assert.Equal(expected, flow.Import.Review!.Outcome);
        Assert.False(flow.Import.CanImport);
        Assert.Null((await flow.Repository.LoadAsync()).State!.SelectedGameId);
        Assert.Empty(flow.Publisher.Notifications);
        Assert.Equal(0L, await AuditRowCountAsync());
        if (expected == LegacyImportPreflightOutcome.VerificationFailed)
        {
            string backupPath = Assert.Single(FindBackups("SoulsTracker"));
            Assert.Equal(original, File.ReadAllBytes(backupPath));
        }
        else
        {
            Assert.Empty(FindBackups("SoulsTracker"));
        }
        AssertSecretSafe(flow.Import.Review, root, sourcePath);
        AssertSecretSafe(flow.Import.StatusMessage, root, sourcePath);
    }

    private async Task<Flow> CreateFlowAsync(PersistentTrackerState destination, LegacyImportPreflight? preflight = null)
    {
        string databaseRoot = Path.Combine(root, "database");
        var repository = new SqliteTrackerStateRepository(databaseRoot, "state.db", new ReversingProtector());
        await repository.SaveAsync(destination);
        var publisher = new RecordingPublisher();
        var coordinator = new SerializedTrackerCoordinator(repository, publisher, new SqliteConfirmedLegacyImportCommitter(repository));
        var tracker = new DesktopTrackerViewModel(coordinator);
        await tracker.InitializeAsync();
        var locator = new TrackingLocator(new ApprovedLegacyImportLocationLocator(new DisposableRootFileSystem(root)));
        var trackingPreflight = new TrackingPreflight(new ApprovedLegacyImportPreflight(locator, preflight ?? new LegacyImportPreflight(() => FixedTime)));
        var workflow = new LegacyImportWorkflow(locator, trackingPreflight, coordinator);
        var import = new LegacyImportViewModel(workflow, tracker.ApplyImportedCommittedState);
        return new Flow(repository, coordinator, tracker, import, locator, trackingPreflight, publisher);
    }

    private string CreateCandidate(string directory, byte[] content)
    {
        string candidateDirectory = Path.Combine(root, directory);
        Directory.CreateDirectory(candidateDirectory);
        string sourcePath = Path.Combine(candidateDirectory, "state.json");
        File.WriteAllBytes(sourcePath, content);
        return sourcePath;
    }

    private string[] FindBackups(string directory) => Directory.Exists(Path.Combine(root, directory))
        ? Directory.GetFiles(Path.Combine(root, directory), "soulstracker-legacy-backup-*.json")
        : [];

    private async Task<long> AuditRowCountAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "database", "state.db")};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='legacy_import_audit'";
        if ((long)(await command.ExecuteScalarAsync() ?? 0L) == 0) return 0;
        command.CommandText = "SELECT COUNT(*) FROM legacy_import_audit";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<string> ReadAuditAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "database", "state.db")};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT import_id, committed_at_utc, contract_version, preflight_outcome, outcome, source_fingerprint, backup_fingerprint FROM legacy_import_audit";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            for (int index = 0; index < reader.FieldCount; index++) values.Add(reader.GetValue(index).ToString()!);
        }
        return string.Join("|", values);
    }

    private static PersistentTrackerState EmptyDestinationWithRetainedOverlaySettings()
    {
        const string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        return new PersistentTrackerState(
            PersistentTrackerState.CurrentSchemaVersion,
            selectedGameId: null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            new OverlayConfiguration(
                OverlayConfiguration.CurrentSchemaVersion,
                new OverlayEndpointConfiguration(45781, OverlayAccessToken.Parse(token)),
                new TotalDeathsOverlayOptions(isEnabled: false, showGameName: false),
                new BossListOverlayOptions(isEnabled: false, BossListVisibilityMode.Remaining)));
    }

    private static string Fingerprint(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static void AssertSecretSafe(object? value, params string[] forbidden)
    {
        string serialized = JsonSerializer.Serialize(value);
        foreach (string rawValue in forbidden) Assert.DoesNotContain(rawValue, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("death_sfx_path", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("area-0-0-0:2576", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", serialized, StringComparison.Ordinal);
    }

    private static byte[] ReadAcceptedFixture()
    {
        string fixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "fixtures", "legacy-state.real-redacted.json"));
        return File.ReadAllBytes(fixturePath);
    }

    private sealed class Flow(
        SqliteTrackerStateRepository repository,
        SerializedTrackerCoordinator coordinator,
        DesktopTrackerViewModel tracker,
        LegacyImportViewModel import,
        TrackingLocator locator,
        TrackingPreflight preflight,
        RecordingPublisher publisher) : IAsyncDisposable
    {
        public SqliteTrackerStateRepository Repository { get; } = repository;
        public SerializedTrackerCoordinator Coordinator { get; } = coordinator;
        public DesktopTrackerViewModel Tracker { get; } = tracker;
        public LegacyImportViewModel Import { get; } = import;
        public TrackingLocator Locator { get; } = locator;
        public TrackingPreflight Preflight { get; } = preflight;
        public RecordingPublisher Publisher { get; } = publisher;
        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class DisposableRootFileSystem(string root) : IApprovedLegacyImportFileSystem
    {
        public string ApplicationDataRoot => root;
        public string GetFullPath(string path) => Path.GetFullPath(path);
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    }

    private sealed class TrackingLocator(IApprovedLegacyImportLocationLocator inner) : IApprovedLegacyImportLocationLocator
    {
        public int DiscoverCalls { get; private set; }
        public IReadOnlyList<LegacyImportCandidate> Discover() { DiscoverCalls++; return inner.Discover(); }
        public bool IsStillApproved(LegacyImportCandidate candidate) => inner.IsStillApproved(candidate);
    }

    private sealed class TrackingPreflight(IApprovedLegacyImportPreflight inner) : IApprovedLegacyImportPreflight
    {
        public int Calls { get; private set; }
        public LegacyImportPreflightReview Prepare(LegacyImportCandidate candidate) { Calls++; return inner.Prepare(candidate); }
    }

    private sealed class RecordingPublisher : ITrackerStateChangePublisher
    {
        public List<TrackerStateChanged> Notifications { get; } = [];
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) { Notifications.Add(notification); return Task.CompletedTask; }
    }

    private sealed class ReversingProtector : IStateSecretProtector
    {
        public byte[] Protect(byte[] value) => value.Select(static value => (byte)(value ^ 0xA5)).ToArray();
        public byte[] Unprotect(byte[] value) => value.Select(static value => (byte)(value ^ 0xA5)).ToArray();
    }
}
