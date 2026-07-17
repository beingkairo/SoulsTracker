using SoulsTracker.Desktop;
using SoulsTracker.Domain;
using System.IO;

namespace SoulsTracker.Desktop.Tests;

public sealed class TextExportStatePublisherTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.TextExports", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }

    [Fact]
    public async Task WritesExactDeathsAndDeterministicSelectedGameBossListAtomically()
    {
        string deathsPath = Path.Combine(root, "deaths.txt");
        string bossPath = Path.Combine(root, "bosses.txt");
        BossProgress progress = BossProgress.Empty.MarkDefeated(GameId.Ds1, GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id);
        PersistentTrackerState state = new(1, GameId.Ds1, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 12), progress, OverlayConfiguration.Default, textExports: new TextExportConfiguration(deathsPath, true, bossPath, true));

        Assert.True(await TextExportStatePublisher.WriteAsync(state, 12));
        Assert.Equal("Total Deaths: 12", await File.ReadAllTextAsync(deathsPath));
        string bossContent = await File.ReadAllTextAsync(bossPath);
        Assert.StartsWith("Dark Souls: Remastered" + Environment.NewLine, bossContent, StringComparison.Ordinal);
        Assert.Contains("[x] " + GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].DisplayName, bossContent, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(root), path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReportsFailureWithoutThrowingWhenChosenDirectoryIsUnavailable()
    {
        string missingPath = Path.Combine(root, "missing", "deaths.txt");
        PersistentTrackerState state = new(1, GameId.Bloodborne, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default, textExports: new TextExportConfiguration(missingPath, true, null, false));

        Assert.False(await TextExportStatePublisher.WriteAsync(state));
    }

    [Fact]
    public async Task UsesTheSelectedAutomaticGameDisplayedTotalAndDoesNotWriteBeforeItIsAvailable()
    {
        string deathsPath = Path.Combine(root, "automatic-deaths.txt");
        PersistentTrackerState state = new(1, GameId.Ds3, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 4), BossProgress.Empty, OverlayConfiguration.Default, textExports: new TextExportConfiguration(deathsPath, true, null, false));

        Assert.True(await TextExportStatePublisher.WriteAsync(state));
        Assert.False(File.Exists(deathsPath));
        Assert.True(await TextExportStatePublisher.WriteAsync(state, 17));
        Assert.Equal("Total Deaths: 17", await File.ReadAllTextAsync(deathsPath));
    }
}
