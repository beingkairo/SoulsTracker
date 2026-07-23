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

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    public async Task UsesTheSelectedAutomaticGameDisplayedTotalAndDoesNotWriteBeforeItIsAvailable(string selectedGameValue)
    {
        GameId selectedGame = GameId.Parse(selectedGameValue);
        string deathsPath = Path.Combine(root, "automatic-deaths.txt");
        PersistentTrackerState state = new(1, selectedGame, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 4), BossProgress.Empty, OverlayConfiguration.Default, textExports: new TextExportConfiguration(deathsPath, true, null, false));

        Assert.True(await TextExportStatePublisher.WriteAsync(state));
        Assert.False(File.Exists(deathsPath));
        Assert.True(await TextExportStatePublisher.WriteAsync(state, 17));
        Assert.Equal("Total Deaths: 17", await File.ReadAllTextAsync(deathsPath));
    }

    [Fact]
    public async Task ReusesTheConfiguredDeathsFileForTheCurrentlySelectedManualGameWithoutSharingTotals()
    {
        string deathsPath = Path.Combine(root, "manual-deaths.txt");
        TextExportConfiguration exports = new(deathsPath, true, null, false);
        ManualBloodborneDeathCounter bloodborne = ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 4);
        ManualBloodborneDeathCounter demonsSouls = ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls, 9);

        PersistentTrackerState bloodborneState = new(1, GameId.Bloodborne, bloodborne, BossProgress.Empty, OverlayConfiguration.Default, textExports: exports, manualDemonsSoulsDeathCounter: demonsSouls);
        PersistentTrackerState demonsSoulsState = new(1, GameId.DemonsSouls, bloodborne, BossProgress.Empty, OverlayConfiguration.Default, textExports: exports, manualDemonsSoulsDeathCounter: demonsSouls);

        Assert.True(await TextExportStatePublisher.WriteAsync(bloodborneState));
        Assert.Equal("Total Deaths: 4", await File.ReadAllTextAsync(deathsPath));

        Assert.True(await TextExportStatePublisher.WriteAsync(demonsSoulsState));
        Assert.Equal("Total Deaths: 9", await File.ReadAllTextAsync(deathsPath));
    }

    [Fact]
    public async Task EldenRingBossExportUsesTheSamePersistedScopeAndRequiredFilter()
    {
        string bossPath = Path.Combine(root, "elden-bosses.txt");
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            textExports: new TextExportConfiguration(null, false, bossPath, true),
            eldenRingNoticeAcknowledged: true,
            eldenRingSave: new EldenRingSaveConfiguration(null, 0, EldenRingBossListScope.ShadowOfTheErdtree, requiredBossesOnly: true));

        Assert.True(await TextExportStatePublisher.WriteAsync(state));
        string export = await File.ReadAllTextAsync(bossPath);
        Assert.Contains("Radahn (Promised Consort)", export, StringComparison.Ordinal);
        Assert.DoesNotContain("Blackgaol Knight", export, StringComparison.Ordinal);
        Assert.Equal(7, export.Split(Environment.NewLine, StringSplitOptions.None).Length);
    }
}
