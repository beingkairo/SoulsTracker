using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class TrayClosePreferenceStoreTests
{
    [Fact]
    public async Task FreshAndExistingTrackerDataDefaultToUncheckedAndExplicitChoicesSurviveRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-tray-setting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var tracker = new SqliteTrackerStateRepository(root, "tracker.db");
            await tracker.LoadAsync();
            var preference = new TrayClosePreferenceStore(root);
            Assert.False(await preference.ResolveAsync());
            Assert.False(await new TrayClosePreferenceStore(root).ResolveAsync());
            await preference.SaveAsync(true);
            Assert.True(await new TrayClosePreferenceStore(root).ResolveAsync());
            await preference.SaveAsync(false);
            Assert.False(await new TrayClosePreferenceStore(root).ResolveAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FreshInstallationDefaultsToUnchecked()
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-tray-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { Assert.False(await new TrayClosePreferenceStore(root).ResolveAsync()); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
