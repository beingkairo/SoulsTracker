using System.IO;
using System.IO.Compression;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;
using SoulsTracker.Application;

namespace SoulsTracker.Desktop.Tests;

public sealed class HollowKnightPersistentPublicationTests
{
    private static PersistentTrackerState State => new(1, GameId.HollowKnight, OverlayConfiguration.Default,
        hollowKnight: new(@"C:\Games\Hollow Knight", Guid.Parse("9d38b867-1eaa-4415-b15d-a31a64fbe90e")));
    private static PersistentPluginObservation Observation(long total, Guid? run = null, long revision = 1) =>
        new(EffectiveDeathTotalResult.SourceIdentityFor(State), run ?? Guid.NewGuid(), 1, total, revision, DateTimeOffset.UtcNow);

    [Fact]
    public void OnlyPluginRecordsSupplyHollowKnightTotals()
    {
        Assert.Null(EffectiveDeathTotalResult.Resolve(State, null).EffectiveDisplayedTotal);
        Assert.Equal(123, EffectiveDeathTotalResult.Resolve(State, null, Observation(123)).EffectiveDisplayedTotal);
        Assert.Equal(0, EffectiveDeathTotalResult.Resolve(State, null, Observation(0)).EffectiveDisplayedTotal);
        Assert.Null(EffectiveDeathTotalResult.Resolve(State, null, Observation(10) with { SourceIdentity = "another installation" }).EffectiveDisplayedTotal);
        Assert.Null(OverlaySnapshotFactory.Create(State, null, 0).TotalDeaths.Value);
        Assert.Equal(TotalDeathsDisplaySource.PersistentPlugin, OverlaySnapshotFactory.Create(State, null, 0, Observation(123)).TotalDeaths.Source);
    }

    [Fact]
    public void HostedOverlayPublishesSavedTotalAndUnavailableWithoutInventingZero()
    {
        var projection = new HostedOverlayProjection();
        Assert.True(projection.Initialize(State).HasDeath);
        Assert.Null(projection.Initialize(State).Death!.Value);
        var accepted = RuntimeGameReadResult.FromHollowKnight(Observation(44), "Saved total (game closed)");
        Assert.Equal("44", projection.FromAcceptedPublication(State, accepted).Death!.Value);
        var unavailable = projection.FromAcceptedPublication(State, RuntimeGameReadResult.FromHollowKnight(null, "Tracking unavailable"));
        Assert.True(unavailable.HasDeath);
        Assert.Null(unavailable.Death!.Value);
        Assert.Equal("unavailable", unavailable.Death.Availability);
    }

    [Fact]
    public void SaveSwitchPublishesNewRunImmediatelyAndOldReadTicketsCannotReplaceIt()
    {
        var session = new RuntimePublicationSession();
        RuntimeGameReadResult? desktop = null, output = null;
        var first = session.BeginRead(State);
        session.CompleteRead(first, State, RuntimeGameReadResult.FromHollowKnight(Observation(100), "Tracking active"), r => desktop = r, r => output = r);
        var older = session.BeginRead(State);
        var latest = session.BeginRead(State);
        session.CompleteRead(latest, State, RuntimeGameReadResult.FromHollowKnight(Observation(0), "Tracking active"), r => desktop = r, r => output = r);
        session.CompleteRead(older, State, RuntimeGameReadResult.FromHollowKnight(Observation(101), "Tracking active"), r => desktop = r, r => output = r);
        Assert.Equal(0, desktop?.PluginObservation?.Total);
        Assert.Equal(0, output?.PluginObservation?.Total);
        session.CompleteRead(session.BeginRead(State), State, RuntimeGameReadResult.FromHollowKnight(null, "Tracking unavailable"), r => desktop = r, r => output = r);
        Assert.Null(output?.PluginObservation);
    }

    [Fact]
    public async Task TextOutputUsesPersistentTotalAndClearsOnUnavailable()
    {
        string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.PluginExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "deaths.txt");
            var state = new PersistentTrackerState(1, GameId.HollowKnight, OverlayConfiguration.Default, textExports: new(path, true), hollowKnight: State.HollowKnight);
            Assert.True(await TextExportStatePublisher.WriteAsync(state, 18));
            Assert.Equal("Total Deaths: 18", await File.ReadAllTextAsync(path));
            Assert.True(await TextExportStatePublisher.WriteAsync(state));
            Assert.Empty(await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DesktopAndPreviewReadTheSamePersistentValueAfterRestart()
    {
        var repository = new SaveDirectoryWorkflowTests.MemoryRepository(GameId.HollowKnight) { State = State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new SaveDirectoryWorkflowTests.NullPublisher());
        await using var vm = SaveDirectoryWorkflowTests.CreateViewModel(coordinator);
        await vm.InitializeAsync();
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.FromHollowKnight(Observation(901), "Tracking active"));
        Assert.Equal("901", vm.TotalDeathsText);
        Assert.Equal("Synced", vm.RuntimeReaderStatusText);
        var preview = new AppearancePreviewModel(new OverlayAppearanceDraft(), OverlayAppearance.Default);
        preview.Update(OverlayTitleIconMode.Off, vm.TotalDeathsText);
        Assert.False(preview.IsRepresentative);
        Assert.Equal("901", preview.Value);
        Assert.Empty(repository.Saves);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.FromHollowKnight(null, "Tracking unavailable"));
        Assert.NotEqual("0", vm.TotalDeathsText);
        Assert.Equal("Game unavailable", vm.RuntimeReaderStatusText);
    }

    [Fact]
    public async Task ManualGameFolderCanBeChangedAndIncompatibleApiPreparationWritesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.PluginSetup", Guid.NewGuid().ToString("N"));
        try
        {
            string? repositoryPath = AppContext.BaseDirectory;
            while (repositoryPath is not null && !File.Exists(Path.Combine(repositoryPath, "SoulsTracker.sln"))) repositoryPath = Path.GetDirectoryName(repositoryPath);
            Assert.NotNull(repositoryPath);
            var repository = new SaveDirectoryWorkflowTests.MemoryRepository(GameId.HollowKnight);
            await using var coordinator = new SerializedTrackerCoordinator(repository, new SaveDirectoryWorkflowTests.NullPublisher());
            await using var vm = SaveDirectoryWorkflowTests.CreateViewModel(coordinator);
            await vm.InitializeAsync();
            foreach (string folder in new[] { "first", "replacement" })
            {
                string directory = Path.Combine(root, folder);
                string managed = Path.Combine(directory, "hollow_knight_Data", "Managed");
                Directory.CreateDirectory(managed);
                File.WriteAllText(Path.Combine(directory, "hollow_knight.exe"), "fixture");
                using var zip = ZipFile.OpenRead(Path.Combine(repositoryPath, "plugins", "Soulstracker.DeathTracker", "obj", "api-v77", "api.zip"));
                zip.GetEntry("Assembly-CSharp.dll")!.ExtractToFile(Path.Combine(managed, "Assembly-CSharp.dll"));
                await vm.SetHollowKnightGameDirectoryAsync(directory);
                Assert.Equal(directory, repository.State.HollowKnight.GameDirectory);
                Assert.Equal(directory, vm.HollowKnightGameDirectory);
                Assert.Equal("Not installed", vm.HollowKnightSetupStatus);
                Assert.Null(await vm.PrepareHollowKnightInstallAsync());
                Assert.NotNull(vm.ErrorMessage);
                Assert.False(Directory.Exists(Path.Combine(directory, ".soulstracker-death-tracking")));
                Assert.False(Directory.Exists(Path.Combine(managed, "Mods")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
