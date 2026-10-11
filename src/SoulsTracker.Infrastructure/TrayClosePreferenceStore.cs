using Microsoft.Data.Sqlite;

namespace SoulsTracker.Infrastructure;

/// <summary>Stores the desktop close preference separately from tracker state.</summary>
public sealed class TrayClosePreferenceStore(string applicationDataRoot)
{
    private readonly string path = Path.Combine(Path.GetFullPath(applicationDataRoot), "tracker.db");

    public async Task<bool> ResolveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS tray_close_preference (id INTEGER PRIMARY KEY CHECK(id=1), enabled INTEGER NOT NULL CHECK(enabled IN (0,1)))";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO tray_close_preference(id,enabled) VALUES(1,0)";
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT enabled FROM tray_close_preference WHERE id=1";
        return (long)(await read.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    public async Task SaveAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tray_close_preference SET enabled=$enabled WHERE id=1";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Tray close preference has not been initialized.");
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString());
}
