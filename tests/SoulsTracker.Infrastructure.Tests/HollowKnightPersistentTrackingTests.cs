using System.IO.Compression;
using System.Diagnostics;
using Soulstracker.DeathTracking;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HollowKnightPersistentTrackingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.HollowKnight", Guid.NewGuid().ToString("N"));
    private readonly Guid installation = Guid.NewGuid();
    private static string Repository
    {
        get
        {
            string? path = AppContext.BaseDirectory;
            while (path is not null && !File.Exists(Path.Combine(path, "SoulsTracker.sln"))) path = Path.GetDirectoryName(path);
            return path ?? throw new IOException("Repository not found.");
        }
    }
    private static byte[] Api => File.ReadAllBytes(Path.Combine(Repository, "plugins", "Soulstracker.DeathTracker", "obj", "api-v77", "api.zip"));
    private RecordStore Store => new(Path.Combine(root, "records", installation.ToString("D")));
    private string Game => Path.Combine(root, "game");
    private static HollowKnightPluginInstaller Installer(Action<int>? fail = null, bool running = false, bool api = false, bool supported = true) =>
        new(() => running, path => new(path, supported ? HollowKnightInstallDiscovery.SupportedGameVersion : "1.5.12620", api, supported), fail);
    private void MakeGame()
    {
        Directory.CreateDirectory(Path.Combine(Game, "hollow_knight_Data", "Managed"));
        File.WriteAllText(Path.Combine(Game, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll"), "original game");
        File.WriteAllText(Path.Combine(Game, "hollow_knight.exe"), "test fixture only");
    }

    [Fact]
    public void DeathIsDurableWithoutDesktopAndDuplicateHookIsIgnoredUntilHealthy()
    {
        Guid run = Guid.NewGuid();
        var session = new DeathSession(Store, installation);
        session.Open(run, 1, true);
        session.Die(); session.Die();
        Assert.Equal(1, Store.Load(run)!.Total);
        session.Healthy(); session.Die();
        Assert.Equal(2, Store.Load(run)!.Total);
        new DeathSession(Store, installation).Open(run, 1, false);
        Assert.Equal(2, Store.Load(run)!.Total);
    }

    [Fact]
    public void SaveSwitchingAndSlotReuseHaveSeparateRunIdentities()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), replacement = Guid.NewGuid();
        var session = new DeathSession(Store, installation);
        session.Die();
        session.Open(a, 1, true); session.Die();
        session.Open(b, 2, true); session.Die(); session.Healthy(); session.Die();
        session.Open(a, 1, false); Assert.Equal(1, Store.Load(a)!.Total);
        session.Open(replacement, 1, true); Assert.Equal(0, Store.Load(replacement)!.Total);
        Assert.Equal(2, Store.Load(b)!.Total);
        Assert.Throws<IOException>(() => session.Open(a, 2, false));
        Assert.False(session.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptExistingRunNeverBecomesZero(bool corrupt)
    {
        Guid run = Guid.NewGuid();
        if (corrupt)
        {
            Store.Commit(new(installation, run, 1, 12, 13, DateTime.UtcNow.Ticks));
            File.WriteAllText(Path.Combine(root, "records", installation.ToString("D"), run.ToString("D") + ".record"), "corrupt");
        }
        var session = new DeathSession(Store, installation);
        Assert.Throws<IOException>(() => session.Open(run, 1, false));
        Assert.False(session.Available);
    }

    [Fact]
    public void InterruptedRecordWriteIsNotPublishedAndRetryKeepsTheSameRevision()
    {
        Guid run = Guid.NewGuid();
        var record = new DeathRecord(installation, run, 1, 10, 11, DateTime.UtcNow.Ticks);
        Store.Commit(record);
        string folder = Path.Combine(root, "records", installation.ToString("D"));
        File.WriteAllText(Path.Combine(folder, run.ToString("D") + ".record"), new DeathRecord(installation, run, 1, 11, 12, DateTime.UtcNow.Ticks).Encode());
        Assert.Null(Read(false).PluginObservation);
        new DeathSession(Store, installation).Open(run, 1, false);
        Assert.Equal(11, Read(false).PluginObservation!.Total);
    }

    private RuntimeGameReadResult Read(bool running, bool matches = true)
    {
        var reader = new HollowKnightRecordReader(Path.Combine(root, "records"), () => running, (_, _) => matches, () => DateTimeOffset.UtcNow);
        reader.Configure(new(Game, installation));
        return reader.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult()!;
    }

    [Fact]
    public void ActiveTrackingNeedsFreshHeartbeatAndMatchingGameProcess()
    {
        Store.Commit(new(installation, Guid.NewGuid(), 1, 7, 8, DateTime.UtcNow.Ticks));
        Assert.Equal(7, Read(false).PluginObservation!.Total);
        Assert.Null(Read(true).PluginObservation);
        Store.WriteStatus(installation, 123, DateTime.UtcNow.Ticks, true);
        Assert.Equal("Tracking active", Read(true).PluginStatus);
        Assert.Null(Read(true, false).PluginObservation);
        Store.WriteStatus(installation, 123, DateTime.UtcNow.Ticks, false);
        Assert.Null(Read(true).PluginObservation);
    }

    [Fact]
    public void RecordChecksumAndBoundsRejectCorruptValues()
    {
        var record = new DeathRecord(installation, Guid.NewGuid(), 4, long.MaxValue, 1, DateTime.UtcNow.Ticks);
        Assert.Equal(long.MaxValue, DeathRecord.Decode(record.Encode())!.Total);
        Assert.Null(DeathRecord.Decode(record.Encode().Replace("SOULSTRACKER", "BROKEN", StringComparison.Ordinal)));
        Assert.Null(DeathRecord.Decode(new string('x', 3000)));
        Assert.Throws<ArgumentException>(() => new DeathRecord(installation, Guid.NewGuid(), 0, 0, 1, DateTime.UtcNow.Ticks));
    }

    [Fact]
    public void FailedProjectionWriteRetriesTheOriginalIncrement()
    {
        Guid run = Guid.NewGuid();
        var session = new DeathSession(Store, installation);
        session.Open(run, 1, true);
        string active = Path.Combine(root, "records", installation.ToString("D"), "active.record");
        File.Delete(active); Directory.CreateDirectory(active);
        Assert.ThrowsAny<IOException>(() => session.Die());
        Assert.Equal(1, Store.Load(run)!.Total);
        session.Die();
        Directory.Delete(active, false);
        session.Flush();
        Assert.Equal(1, Read(false).PluginObservation!.Total);
        Assert.Equal(2, Read(false).PluginObservation!.Revision);
    }

    [Fact]
    public void InterruptedInitialBindingRetriesBeforePublishingANewRun()
    {
        string binding = Path.Combine(root, "records", installation.ToString("D"), "slot-1.binding");
        Directory.CreateDirectory(binding);
        Guid run = Guid.NewGuid();
        var session = new DeathSession(Store, installation);
        Assert.ThrowsAny<IOException>(() => session.Open(run, 1, true));
        Assert.Null(Store.Load(run));
        Directory.Delete(binding, false);
        session.Flush();
        Assert.True(Store.HasSlot(1));
        Assert.Equal(0, Read(false).PluginObservation!.Total);
    }

    [Fact]
    public void ChangedOwnedPluginBlocksUpdateAndUninstallWithoutTouchingOtherFiles()
    {
        MakeGame();
        Installer().Apply(Installer().Prepare(Game, [1], Api, installation));
        string plugin = Path.Combine(Game, HollowKnightPluginInstaller.PluginFolder, HollowKnightPluginInstaller.PluginName);
        File.WriteAllText(plugin, "changed by someone else");
        Assert.Throws<IOException>(() => Installer().Prepare(Game, [2], null));
        Assert.Throws<IOException>(() => Installer().Uninstall(Game));
        Assert.Equal("changed by someone else", File.ReadAllText(plugin));
        Assert.True(HollowKnightPluginInstaller.HasCompatibleApi(Game));
    }

    [Fact]
    public void SteamLibrariesAreDiscoveredAndAmbiguousInstallationsRequireManualChoice()
    {
        string steam = Path.Combine(root, "steam"), library = Path.Combine(root, "library");
        string candidate = Path.Combine(library, "steamapps", "common", "Hollow Knight");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"path\" \"" + library.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"");
        CreateInspectable(candidate);
        Assert.Equal(candidate, HollowKnightInstallDiscovery.DiscoverFromSteamRoots([steam]));
        string second = Path.Combine(steam, "steamapps", "common", "Hollow Knight");
        CreateInspectable(second);
        Assert.Null(HollowKnightInstallDiscovery.DiscoverFromSteamRoots([steam]));
        Assert.True(HollowKnightInstallDiscovery.Inspect(candidate)!.Supported);
        Assert.Null(HollowKnightInstallDiscovery.Inspect(Path.Combine(root, "missing")));
    }

    [Fact]
    public void LinkedInstallationsAreRejectedWithoutChangingTheTargetWhenSupported()
    {
        CreateInspectable(Game);
        string link = Path.Combine(root, "linked");
        try { Directory.CreateSymbolicLink(link, Game); }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            using Process junction = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Game}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            junction.WaitForExit();
            Assert.Equal(0, junction.ExitCode);
        }
        try
        {
            Assert.Null(HollowKnightInstallDiscovery.Inspect(link));
            Assert.Throws<IOException>(() => Installer().Prepare(link, [1], Api));
            Assert.True(HollowKnightInstallDiscovery.Inspect(Game)!.Supported);
        }
        finally { Directory.Delete(link, false); }
    }

    private static void CreateInspectable(string directory)
    {
        string managed = Path.Combine(directory, "hollow_knight_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(directory, "hollow_knight.exe"), "fixture");
        using var archive = new ZipArchive(new MemoryStream(Api), ZipArchiveMode.Read);
        archive.GetEntry("Assembly-CSharp.dll")!.ExtractToFile(Path.Combine(managed, "Assembly-CSharp.dll"), true);
    }

    [Theory]
    [InlineData("hollow_knight", true)]
    [InlineData("HOLLOW_KNIGHT", true)]
    [InlineData("Hollow Knight", true)]
    [InlineData("Hollow Knight Silksong", false)]
    [InlineData("hollow_knight_backup", false)]
    public void ProcessIdentityRequiresOriginalGameAlias(string name, bool expected) =>
        Assert.Equal(expected, HollowKnightInstallDiscovery.IsGameProcessName(name));

    [Fact]
    public void InstallReuseUpdateAndUninstallPreserveSharedApiOtherModsAndRecords()
    {
        MakeGame();
        string other = Path.Combine(Game, "hollow_knight_Data", "Managed", "Mods", "OtherMod", "other.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!); File.WriteAllText(other, "other mod");
        var installer = Installer();
        HollowKnightInstallPlan plan = installer.Prepare(Game, [1, 2, 3], Api, installation);
        Assert.False(plan.ReusesApi);
        Assert.Equal("original game", File.ReadAllText(Path.Combine(Game, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll")));
        installer.Apply(plan);
        Assert.True(HollowKnightPluginInstaller.HasCompatibleApi(Game));
        Assert.Equal(installation, HollowKnightPluginInstaller.GetInstalledIdentity(Game));
        var update = installer.Prepare(Game, [4, 5], null);
        Assert.True(update.ReusesApi); Assert.Equal(3, update.Files.Count);
        installer.Apply(update);
        installer.Uninstall(Game);
        Assert.True(HollowKnightPluginInstaller.HasCompatibleApi(Game));
        Assert.Null(HollowKnightPluginInstaller.GetInstalledIdentity(Game));
        Assert.Equal("other mod", File.ReadAllText(other));
        Assert.True(Directory.Exists(Path.Combine(Game, ".soulstracker-death-tracking")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(11)]
    public void InterruptedInstallRollsBackOriginalBytes(int failureAt)
    {
        MakeGame();
        var installer = Installer(index => { if (index == failureAt) throw new IOException("Simulated interrupted install"); });
        var plan = installer.Prepare(Game, [1], Api);
        Assert.Throws<IOException>(() => installer.Apply(plan));
        foreach (string relative in plan.Files)
        {
            string path = Path.Combine(Game, relative);
            if (plan.BeforeHashes[relative] is null) Assert.False(File.Exists(path));
            else Assert.Equal(plan.BeforeHashes[relative], HollowKnightPluginInstaller.Hash(File.ReadAllBytes(path)));
        }
    }

    [Fact]
    public void PendingTransactionCanBeRecoveredAfterApplicationRestart()
    {
        MakeGame();
        bool running = false;
        var installer = new HollowKnightPluginInstaller(() => running, path => new(path, "1.5.78.11833", false, true),
            index => { if (index == 0) { running = true; throw new IOException("Game launched during installation"); } });
        var plan = installer.Prepare(Game, [1], Api);
        Assert.Throws<IOException>(() => installer.Apply(plan));
        running = false;
        Assert.Throws<IOException>(() => installer.Prepare(Game, [1], Api));
        Installer().RollbackPending(Game);
        Assert.Equal("original game", File.ReadAllText(Path.Combine(Game, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll")));
        Assert.NotEmpty(Directory.EnumerateDirectories(Path.Combine(Game, ".soulstracker-death-tracking")));
        Assert.NotNull(Installer().Prepare(Game, [1], Api));
    }

    [Fact]
    public void ChangedRecoveryTargetPreventsRollbackAndPreservesBackups()
    {
        MakeGame();
        bool running = false;
        var installer = new HollowKnightPluginInstaller(() => running, path => new(path, "1.5.78.11833", false, true),
            _ => { running = true; throw new IOException("Interrupted"); });
        Assert.Throws<IOException>(() => installer.Apply(installer.Prepare(Game, [1], Api)));
        string changed = Path.Combine(Game, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll");
        File.WriteAllText(changed, "a subsequent change");
        Assert.Throws<IOException>(() => Installer().RollbackPending(Game));
        Assert.Equal("a subsequent change", File.ReadAllText(changed));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(Game, ".soulstracker-death-tracking"), "*.backup", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExistingUnknownModAndTamperedOwnedFilesAreNeverOverwrittenOrUninstalled()
    {
        MakeGame();
        string folder = Path.Combine(Game, HollowKnightPluginInstaller.PluginFolder);
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "unknown.txt"), "unique");
        Assert.Throws<IOException>(() => Installer().Prepare(Game, [1], Api));
        Assert.Equal("unique", File.ReadAllText(Path.Combine(folder, "unknown.txt")));
    }

    [Fact]
    public void RunningGameUnsupportedVersionChangedPlanAndBadApiAreRejected()
    {
        MakeGame();
        Assert.Throws<IOException>(() => Installer(running: true).Prepare(Game, [1], Api));
        Assert.Throws<IOException>(() => Installer(supported: false).Prepare(Game, [1], Api));
        Assert.Throws<IOException>(() => Installer(api: true).Prepare(Game, [1], Api));
        Assert.Throws<IOException>(() => Installer().Prepare(Game, [1], [1]));
        var plan = Installer().Prepare(Game, [1], Api);
        File.WriteAllText(Path.Combine(Game, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll"), "changed after confirmation");
        Assert.Throws<IOException>(() => Installer().Apply(plan));
        Assert.False(Directory.Exists(Path.Combine(Game, ".soulstracker-death-tracking")));
    }

    [Fact]
    public async Task SelectedInstallationPersistsAcrossDesktopRestartAndUnrelatedCommands()
    {
        var state = TrackerStateTransitionService.Apply(PersistentTrackerState.Default, new UpdateHollowKnightConfigurationCommand(new(Game, installation))).State;
        state = TrackerStateTransitionService.Apply(state, new SelectGameCommand(GameId.HollowKnight)).State;
        await using (var repository = new SqliteTrackerStateRepository(Path.Combine(root, "db"), "test.db")) await repository.SaveAsync(state);
        await using var reopened = new SqliteTrackerStateRepository(Path.Combine(root, "db"), "test.db");
        Assert.Equal(new HollowKnightConfiguration(Game, installation), (await reopened.LoadAsync()).State!.HollowKnight);
    }

    [Fact]
    public void OfficialApiVersionIsReadWithoutExecutingTheAssembly()
    {
        MakeGame();
        using var zip = new ZipArchive(new MemoryStream(Api), ZipArchiveMode.Read);
        zip.GetEntry("Assembly-CSharp.dll")!.ExtractToFile(Path.Combine(Game, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll"), true);
        var result = HollowKnightInstallDiscovery.Inspect(Game);
        Assert.NotNull(result); Assert.True(result.HasApi); Assert.True(result.Supported);
        Assert.Equal("1.5.78.11833", result.Version);
    }

    public void Dispose()
    {
        // The only recursive cleanup target is this test's unique temporary directory.
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
