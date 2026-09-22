using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class OutlineDefaultTests
{
    [Theory]
    [InlineData("appearance", 2)]
    [InlineData("width", 2)]
    [InlineData("zero", 0)]
    [InlineData("nonzero", 5)]
    public async Task MissingLocalWidthUsesDefaultWhileExplicitStoredValuesWin(string variant, int expected)
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-outline-{Guid.NewGuid():N}");
        try
        {
            await using (var repository = new SqliteTrackerStateRepository(root, "test.db")) await repository.SaveAsync(PersistentTrackerState.Default);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "test.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var read = connection.CreateCommand(); read.CommandText = "SELECT payload FROM tracker_state WHERE id=1";
                var node = JsonNode.Parse((string)(await read.ExecuteScalarAsync())!)!;
                if (variant == "appearance") node.AsObject().Remove("TotalAppearance");
                else if (variant == "width") node["TotalAppearance"]!.AsObject().Remove("OutlineWidth");
                else node["TotalAppearance"]!["OutlineWidth"] = expected;
                using var write = connection.CreateCommand(); write.CommandText = "UPDATE tracker_state SET payload=$payload WHERE id=1";
                write.Parameters.AddWithValue("$payload", node.ToJsonString()); await write.ExecuteNonQueryAsync();
            }
            await using (var repository = new SqliteTrackerStateRepository(root, "test.db"))
            {
                var loaded = (await repository.LoadAsync()).State!;
                Assert.Equal(expected, loaded.OverlayConfiguration.TotalDeaths.Appearance.OutlineWidth);
                await repository.SaveAsync(loaded);
            }
            await using var reconstructed = new SqliteTrackerStateRepository(root, "test.db");
            Assert.Equal(expected, (await reconstructed.LoadAsync()).State!.OverlayConfiguration.TotalDeaths.Appearance.OutlineWidth);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
