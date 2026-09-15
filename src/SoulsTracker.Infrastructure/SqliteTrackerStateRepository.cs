using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

public sealed class SqliteTrackerStateRepository : ITrackerStateRepository
{

    private const string TableSql = "CREATE TABLE IF NOT EXISTS tracker_state (id INTEGER PRIMARY KEY CHECK(id=1), schema_version INTEGER NOT NULL, payload TEXT NOT NULL, token BLOB NULL);";
    private const string ImportAuditTableSql = "CREATE TABLE IF NOT EXISTS legacy_import_audit (import_id TEXT PRIMARY KEY NOT NULL, committed_at_utc TEXT NOT NULL, contract_version INTEGER NOT NULL, preflight_outcome INTEGER NOT NULL, outcome INTEGER NOT NULL, source_fingerprint TEXT NOT NULL, backup_fingerprint TEXT NOT NULL);";
    private readonly string path;
    private readonly IStateSecretProtector protector;
    private readonly FileStream writerLock;
    private readonly ISqliteMigrationBackup migrationBackup;
    private readonly ISqliteStateMigration migration;
    private readonly ISqliteSaveInterruption? saveInterruption;
    private bool disposed;

    public SqliteTrackerStateRepository(string applicationDataRoot, string databaseFileName, IStateSecretProtector? protector = null, ISqliteMigrationBackup? migrationBackup = null, ISqliteStateMigration? migration = null, ISqliteSaveInterruption? saveInterruption = null)
    {
        if (string.IsNullOrWhiteSpace(applicationDataRoot) || string.IsNullOrWhiteSpace(databaseFileName) || Path.IsPathRooted(databaseFileName) || databaseFileName.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("The database path must remain under the supplied application-data root.");
        string root = Path.GetFullPath(applicationDataRoot);
        path = Path.GetFullPath(Path.Combine(root, databaseFileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The database path escapes the supplied application-data root.");
        Directory.CreateDirectory(root);
        try { writerLock = new FileStream(path + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("Another SoulsTracker repository writer already owns this database path.", ex); }
        this.protector = protector ?? CreateDefaultProtector();
        this.migrationBackup = migrationBackup ?? new TimestampedSqliteMigrationBackup();
        this.migration = migration ?? new NoSupportedSqliteStateMigration();
        this.saveInterruption = saveInterruption;
    }

    public async Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            bool exists = File.Exists(path);
            await using SqliteConnection connection = Open();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
            await ValidateIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, TableSql, cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT schema_version, payload, token FROM tracker_state WHERE id=1";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { PersistentTrackerState state = PersistentTrackerState.Default; await SaveAsync(state, cancellationToken).ConfigureAwait(false); return TrackerStateLoadResult.Loaded(state); }
            int version = reader.GetInt32(0);
            if (version != PersistentTrackerState.CurrentSchemaVersion)
            {
                if (!migration.CanMigrate(version, PersistentTrackerState.CurrentSchemaVersion)) return TrackerStateLoadResult.Failed(TrackerStateLoadFailureKind.UnsupportedVersion, "Stored state uses an unsupported schema version.");
                await reader.DisposeAsync().ConfigureAwait(false);
                await migrationBackup.CreateBeforeMigrationAsync(path, cancellationToken).ConfigureAwait(false);
                await migration.MigrateAsync(path, version, PersistentTrackerState.CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
                return await LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            StoredState dto = JsonSerializer.Deserialize<StoredState>(reader.GetString(1)) ?? throw new InvalidDataException(); string? token = reader.IsDBNull(2) ? null : Encoding.UTF8.GetString(protector.Unprotect((byte[])reader[2]));
            PersistentTrackerState loadedState = ToDomain(dto, token);
            await reader.DisposeAsync().ConfigureAwait(false);

            return TrackerStateLoadResult.Loaded(loadedState);
        }
        catch (SqliteException) { return TrackerStateLoadResult.Failed(TrackerStateLoadFailureKind.Integrity, "The local tracker database failed SQLite validation."); }
        catch (Exception) { return TrackerStateLoadResult.Failed(TrackerStateLoadFailureKind.Corrupt, "The local tracker state is unreadable. It was not replaced."); }
    }

    public async Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(state);
        (StoredState dto, string? token) = FromDomain(state);
        await using SqliteConnection connection = Open(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, TableSql, cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO tracker_state(id,schema_version,payload,token) VALUES(1,$version,$payload,$token) ON CONFLICT(id) DO UPDATE SET schema_version=$version,payload=$payload,token=$token";
        command.Parameters.AddWithValue("$version", PersistentTrackerState.CurrentSchemaVersion); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(dto));
        command.Parameters.AddWithValue("$token", token is null ? DBNull.Value : protector.Protect(Encoding.UTF8.GetBytes(token)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); if (saveInterruption is not null) await saveInterruption.BeforeCommitAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically persists only the applied result of P6-03 and its opaque audit
    /// metadata. This operation neither receives arbitrary state nor opens a
    /// legacy source.
    /// </summary>
    public async Task<ConfirmedLegacyImportCommitResult> CommitConfirmedLegacyImportAsync(
        LegacyProposalApplicationResult applicationResult,
        ConfirmedLegacyImportAuditMetadata auditMetadata,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(applicationResult);
        ArgumentNullException.ThrowIfNull(auditMetadata);

        if (applicationResult.Outcome != LegacyProposalApplicationOutcome.Applied || applicationResult.CandidateState is null)
        {
            return ConfirmedLegacyImportCommitResult.Refused(ConfirmedLegacyImportCommitOutcome.InvalidCandidate);
        }

        if (!auditMetadata.IsValid)
        {
            return ConfirmedLegacyImportCommitResult.Refused(ConfirmedLegacyImportCommitOutcome.InvalidAuditMetadata);
        }

        try
        {
            PersistentTrackerState candidate = applicationResult.CandidateState;
            (StoredState candidateDto, string? candidateToken) = FromDomain(candidate);
            await using SqliteConnection connection = Open();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, TableSql, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, ImportAuditTableSql, transaction, cancellationToken).ConfigureAwait(false);

            PersistentTrackerState stored = await ReadStoredStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            ConfirmedLegacyImportCommitOutcome? refusal = GetDestinationRefusal(stored);
            if (refusal is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ConfirmedLegacyImportCommitResult.Refused(refusal.Value);
            }

            string importId = Guid.NewGuid().ToString("N");
            DateTimeOffset committedAtUtc = DateTimeOffset.UtcNow;
            await UpsertStateAsync(connection, transaction, candidateDto, candidateToken, cancellationToken).ConfigureAwait(false);
            await InsertAuditAsync(connection, transaction, importId, committedAtUtc, auditMetadata, cancellationToken).ConfigureAwait(false);
            if (saveInterruption is not null)
            {
                await saveInterruption.BeforeCommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ConfirmedLegacyImportCommitResult.Committed(importId, committedAtUtc);
        }
        catch (Exception)
        {
            return ConfirmedLegacyImportCommitResult.Refused(ConfirmedLegacyImportCommitOutcome.StorageUnavailable);
        }
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
    private static CurrentUserDpapiSecretProtector CreateDefaultProtector()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Current-user DPAPI is required on Windows.");
        return new CurrentUserDpapiSecretProtector();
    }
    private static async Task ExecuteAsync(SqliteConnection c, string sql, CancellationToken ct) { await using SqliteCommand command = c.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
    private static async Task ValidateIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not string integrityResult || !string.Equals(integrityResult, "ok", StringComparison.Ordinal))
        {
            throw new SqliteException("SQLite integrity check failed.", 11);
        }
    }
    private static async Task ExecuteAsync(SqliteConnection c, string sql, SqliteTransaction transaction, CancellationToken ct) { await using SqliteCommand command = c.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
    private async Task<PersistentTrackerState> ReadStoredStateAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version, payload, token FROM tracker_state WHERE id=1";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return PersistentTrackerState.Default;
        }

        if (reader.GetInt32(0) != PersistentTrackerState.CurrentSchemaVersion)
        {
            throw new InvalidDataException();
        }

        StoredState dto = JsonSerializer.Deserialize<StoredState>(reader.GetString(1)) ?? throw new InvalidDataException();
        string? token = reader.IsDBNull(2) ? null : Encoding.UTF8.GetString(protector.Unprotect((byte[])reader[2]));
        return ToDomain(dto, token);
    }
    private static ConfirmedLegacyImportCommitOutcome? GetDestinationRefusal(PersistentTrackerState state)
    {
        if (state.SelectedGameId != GameId.DemonsSouls) return ConfirmedLegacyImportCommitOutcome.DestinationHasSelectedGame;

        return state.ManualDemonsSoulsDeathCounter.Value != 0
            ? ConfirmedLegacyImportCommitOutcome.DestinationHasManualDeaths
            : null;
    }
    private async Task UpsertStateAsync(SqliteConnection connection, SqliteTransaction transaction, StoredState dto, string? token, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO tracker_state(id,schema_version,payload,token) VALUES(1,$version,$payload,$token) ON CONFLICT(id) DO UPDATE SET schema_version=$version,payload=$payload,token=$token";
        command.Parameters.AddWithValue("$version", PersistentTrackerState.CurrentSchemaVersion);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(dto));
        command.Parameters.AddWithValue("$token", token is null ? DBNull.Value : protector.Protect(Encoding.UTF8.GetBytes(token)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private static async Task InsertAuditAsync(SqliteConnection connection, SqliteTransaction transaction, string importId, DateTimeOffset committedAtUtc, ConfirmedLegacyImportAuditMetadata auditMetadata, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO legacy_import_audit(import_id,committed_at_utc,contract_version,preflight_outcome,outcome,source_fingerprint,backup_fingerprint) VALUES($id,$committedAt,$contractVersion,$preflightOutcome,$outcome,$sourceFingerprint,$backupFingerprint)";
        command.Parameters.AddWithValue("$id", importId);
        command.Parameters.AddWithValue("$committedAt", committedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$contractVersion", auditMetadata.ContractVersion);
        command.Parameters.AddWithValue("$preflightOutcome", (int)auditMetadata.PreflightOutcome);
        command.Parameters.AddWithValue("$outcome", (int)ConfirmedLegacyImportCommitOutcome.Committed);
        command.Parameters.AddWithValue("$sourceFingerprint", auditMetadata.SourceFingerprint);
        command.Parameters.AddWithValue("$backupFingerprint", auditMetadata.BackupFingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private static (StoredState, string?) FromDomain(PersistentTrackerState state)
    {
        OverlayConfiguration config = state.OverlayConfiguration;
        GlobalHotkeyConfiguration hotkeys = state.GlobalHotkeys;
        StoredEldenRingMissedDeathAdjustment[] adjustments = state.EldenRingMissedDeathAdjustments.ToEntries()
            .Select(static entry => new StoredEldenRingMissedDeathAdjustment(entry.LocalSavePath, entry.SlotIndex, entry.Value))
            .ToArray();
        return (new StoredState(state.SelectedGameId.Value, config.Endpoint.Port, config.TotalDeaths.IsEnabled, config.TotalDeaths.ShowGameName, hotkeys.IncrementModifiers, hotkeys.IncrementVirtualKey, hotkeys.DecrementModifiers, hotkeys.DecrementVirtualKey, config.TotalDeaths.CompactTitle, config.TotalDeaths.Appearance, state.TextExports.DeathsPath, state.TextExports.DeathsEnabled, (int)config.TotalDeaths.TitleIconMode, state.ManualDemonsSoulsDeathCounter.Value, state.EldenRingNoticeAcknowledged, state.EldenRingSave.LocalPath, state.EldenRingSave.SlotIndex, state.BlackMythWukongSave.LocalPath, adjustments, state.LiesOfPSave.LocalPath), config.Endpoint.AccessToken?.PersistenceValue);
    }
    private static PersistentTrackerState ToDomain(StoredState dto, string? token)
    {
        OverlayAccessToken? accessToken = token is null ? null : OverlayAccessToken.Parse(token);
        var endpoint = new OverlayEndpointConfiguration(dto.Port, accessToken);

        var candidateHotkeys = dto.IncrementModifiers is uint incrementModifiers && dto.IncrementVirtualKey is uint incrementKey && dto.DecrementModifiers is uint decrementModifiers && dto.DecrementVirtualKey is uint decrementKey ? new GlobalHotkeyConfiguration(incrementModifiers, incrementKey, decrementModifiers, decrementKey) : null;
        var hotkeys = candidateHotkeys is { IsValid: true } ? candidateHotkeys : GlobalHotkeyConfiguration.Default;
        OverlayAppearance totalAppearance = dto.TotalAppearance ?? OverlayAppearance.Default;

        var total = new TotalDeathsOverlayOptions(dto.TotalEnabled, dto.ShowGameName, dto.TotalCompactTitle ?? false, totalAppearance, dto.TotalTitleIconMode is int icon && Enum.IsDefined((OverlayTitleIconMode)icon) ? (OverlayTitleIconMode)icon : OverlayTitleIconMode.Off);


        TextExportConfiguration exports;
        try { exports = new TextExportConfiguration(dto.DeathsExportPath, dto.DeathsExportEnabled ?? false); }
        catch (ArgumentException) { exports = TextExportConfiguration.Default; }
        EldenRingSaveConfiguration eldenRingSave;
        try { eldenRingSave = new EldenRingSaveConfiguration(dto.EldenRingSavePath, dto.EldenRingSaveSlotIndex ?? 0); }
        catch (ArgumentException) { eldenRingSave = EldenRingSaveConfiguration.Default; }
        BlackMythWukongSaveConfiguration blackMythWukongSave;
        try { blackMythWukongSave = new BlackMythWukongSaveConfiguration(dto.BlackMythWukongSavePath); }
        catch (ArgumentException) { blackMythWukongSave = BlackMythWukongSaveConfiguration.Default; }
        LiesOfPSaveConfiguration liesOfPSave;
        try { liesOfPSave = new LiesOfPSaveConfiguration(dto.LiesOfPSavePath); }
        catch (ArgumentException) { liesOfPSave = LiesOfPSaveConfiguration.Default; }
        EldenRingMissedDeathAdjustments adjustments;
        try { adjustments = new EldenRingMissedDeathAdjustments((dto.EldenRingMissedDeathAdjustments ?? []).Select(static entry => new EldenRingMissedDeathAdjustment(entry.LocalSavePath, entry.SlotIndex, entry.Value))); }
        catch (ArgumentException) { adjustments = EldenRingMissedDeathAdjustments.Empty; }

        GameId selectedGameId = dto.SelectedGameId is null ? GameId.DemonsSouls : GameId.Parse(dto.SelectedGameId);
        return new PersistentTrackerState(1, selectedGameId, new OverlayConfiguration(1, endpoint, total), hotkeys, exports, ManualDeathCounter.CreateFor(GameId.DemonsSouls, dto.ManualDemonsSoulsDeaths ?? 0), dto.EldenRingNoticeAcknowledged ?? false, eldenRingSave, blackMythWukongSave, adjustments, liesOfPSave);
    }


    public ValueTask DisposeAsync() { if (!disposed) { disposed = true; writerLock.Dispose(); } return ValueTask.CompletedTask; }
    private sealed record StoredState(string? SelectedGameId, int? Port, bool TotalEnabled, bool ShowGameName, uint? IncrementModifiers = null, uint? IncrementVirtualKey = null, uint? DecrementModifiers = null, uint? DecrementVirtualKey = null, bool? TotalCompactTitle = null, OverlayAppearance? TotalAppearance = null, string? DeathsExportPath = null, bool? DeathsExportEnabled = null, int? TotalTitleIconMode = null, long? ManualDemonsSoulsDeaths = null, bool? EldenRingNoticeAcknowledged = null, string? EldenRingSavePath = null, int? EldenRingSaveSlotIndex = null, string? BlackMythWukongSavePath = null, StoredEldenRingMissedDeathAdjustment[]? EldenRingMissedDeathAdjustments = null, string? LiesOfPSavePath = null);
    private sealed record StoredEldenRingMissedDeathAdjustment(string LocalSavePath, int SlotIndex, long Value);
}
