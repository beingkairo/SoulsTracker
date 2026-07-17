namespace SoulsTracker.Infrastructure;

/// <summary>Creates retained, same-directory copies before an approved in-place schema migration.</summary>
public interface ISqliteMigrationBackup
{
    Task CreateBeforeMigrationAsync(string databasePath, CancellationToken cancellationToken = default);
}

/// <summary>Controlled Infrastructure-only migration decision seam; no successor schema is enabled by default.</summary>
public interface ISqliteStateMigration
{
    bool CanMigrate(int fromVersion, int targetVersion);
    Task MigrateAsync(string databasePath, int fromVersion, int targetVersion, CancellationToken cancellationToken = default);
}

public sealed class NoSupportedSqliteStateMigration : ISqliteStateMigration
{
    public bool CanMigrate(int fromVersion, int targetVersion) => false;
    public Task MigrateAsync(string databasePath, int fromVersion, int targetVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No SQLite state migration is approved.");
}

public interface ISqliteSaveInterruption
{
    Task BeforeCommitAsync(CancellationToken cancellationToken = default);
}

public sealed class TimestampedSqliteMigrationBackup : ISqliteMigrationBackup
{
    public Task CreateBeforeMigrationAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string directory = Path.GetDirectoryName(databasePath) ?? throw new ArgumentException("The database must have a parent directory.", nameof(databasePath));
        string name = Path.GetFileName(databasePath);
        string backup = Path.Combine(directory, $"{name}.pre-migration-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak");
        File.Copy(databasePath, backup, overwrite: false);
        return Task.CompletedTask;
    }
}
