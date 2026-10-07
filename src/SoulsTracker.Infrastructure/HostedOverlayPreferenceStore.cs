using Microsoft.Data.Sqlite;

namespace SoulsTracker.Infrastructure;

/// <summary>Independent local consent setting; old tracker rows are distinguishable from new installs.</summary>
public sealed class HostedOverlayPreferenceStore(string applicationDataRoot)
{
    private readonly string path = Path.Combine(Path.GetFullPath(applicationDataRoot), "tracker.db");

    public async Task<bool> ResolveAsync(Func<CancellationToken, Task<bool>> hasValidActiveIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasValidActiveIdentity);
        bool historical = false;
        if (File.Exists(path))
        {
            await using var existing = Open();
            await existing.OpenAsync(cancellationToken);
            await using var check = existing.CreateCommand();
            check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='tracker_state'";
            if ((long)(await check.ExecuteScalarAsync(cancellationToken))! != 0)
            {
                check.CommandText = "SELECT count(*) FROM tracker_state WHERE id=1";
                historical = (long)(await check.ExecuteScalarAsync(cancellationToken))! != 0;
            }
        }
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS hosted_overlay_preference (id INTEGER PRIMARY KEY CHECK(id=1), enabled INTEGER NOT NULL CHECK(enabled IN (0,1)))";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT enabled FROM hosted_overlay_preference WHERE id=1";
            object? saved = await read.ExecuteScalarAsync(cancellationToken);
            if (saved is long value) return value == 1;
        }
        bool enabled = historical && await hasValidActiveIdentity(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO hosted_overlay_preference(id,enabled) VALUES(1,$enabled)";
        insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        // Another resolver may have won the insert; respect the durable value.
        await using var resolved = connection.CreateCommand();
        resolved.CommandText = "SELECT enabled FROM hosted_overlay_preference WHERE id=1";
        return (long)(await resolved.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    public async Task SaveAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE hosted_overlay_preference SET enabled=$enabled WHERE id=1";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Hosted Overlay preference has not been initialized.");
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString());
}
