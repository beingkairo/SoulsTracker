using Microsoft.Data.Sqlite;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedOverlayPreferenceStoreTests
{
    [Fact]
    public async Task FreshInstallationPersistsDisabledEvenWhenIdentityCallbackWouldReturnTrue()
    {
        string root = Root();
        try
        {
            var preference = new HostedOverlayPreferenceStore(root);
            int inspections = 0;
            Assert.False(await preference.ResolveAsync(_ => { inspections++; return Task.FromResult(true); }));
            Assert.Equal(0, inspections);
            await using var repository = new SqliteTrackerStateRepository(root, "tracker.db");
            Assert.True((await repository.LoadAsync()).IsSuccess);
            Assert.False(await preference.ResolveAsync(_ => Task.FromResult(true)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingStateMigratesOnlyValidatedActiveIdentityAndNeverReclassifies(bool validActive)
    {
        string root = Root();
        try
        {
            await using (var repository = new SqliteTrackerStateRepository(root, "tracker.db"))
                await repository.SaveAsync(PersistentTrackerState.Default);
            var preference = new HostedOverlayPreferenceStore(root);
            Assert.Equal(validActive, await preference.ResolveAsync(_ => Task.FromResult(validActive)));
            Assert.Equal(validActive, await preference.ResolveAsync(_ => Task.FromResult(!validActive)));
            await preference.SaveAsync(!validActive);
            Assert.Equal(!validActive, await new HostedOverlayPreferenceStore(root).ResolveAsync(_ => Task.FromResult(validActive)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PendingOnlyUpgradeStaysDisabledAndPreservesUnrelatedTrackerState()
    {
        string root = Root();
        try
        {
            await using (var repository = new SqliteTrackerStateRepository(root, "tracker.db"))
                await repository.SaveAsync(PersistentTrackerState.Default);
            var preference = new HostedOverlayPreferenceStore(root);
            Assert.False(await preference.ResolveAsync(_ => Task.FromResult(false)));
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "tracker.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM tracker_state WHERE id=1";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Root()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
