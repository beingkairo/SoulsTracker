using System.Text.RegularExpressions;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

/// <summary>Exercises the installer's uninstall-time cleanup against isolated user roots,
/// then opens the same SQLite state repository as a fresh Desktop launch.</summary>
public sealed class InstallerUninstallStateTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), $"souls-uninstall-{Guid.NewGuid():N}");

    [Fact]
    public void DeleteChoiceIsAppliedAtUninstallTimeRatherThanWhenSetupRecordsDeletionRules()
    {
        string script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "SoulsTracker.iss"));
        Match section = Regex.Match(script, @"(?ms)^\[UninstallDelete\]\s*\r?\n(?<entries>.*?)(?=^\[|\z)");
        Assert.DoesNotContain("Check: ShouldDeleteLocalSettings", section.Success ? section.Groups["entries"].Value : "");
        Assert.Matches(@"(?s)procedure CurUninstallStepChanged\(CurUninstallStep: TUninstallStep\);.*?if \(CurUninstallStep = usPostUninstall\) and DeleteLocalSettings then\s+DeleteSoulsTrackerSettings;", script);
    }

    [Theory]
    [InlineData(false, false)] // interactive retain
    [InlineData(true, false)]  // interactive delete
    [InlineData(false, true)]  // silent retains regardless of prompt
    public async Task UninstallChoiceControlsStateOnReinstallWithoutTouchingOtherData(bool deleteChoice, bool silent)
    {
        string local = Path.Combine(fixture, "Local", "SoulsTracker");
        string roaming = Path.Combine(fixture, "Roaming", "SoulsTracker");
        string export = Path.Combine(fixture, "Exports", "deaths.txt");
        string save = Path.Combine(fixture, "Game", "save.dat");
        string runtime = Path.Combine(fixture, "SharedRuntime", "msedgewebview2.exe");
        string installed = Path.Combine(fixture, "Programs", "SoulsTracker", "SoulsTracker.Desktop.exe");
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.DemonsSouls,
            new OverlayConfiguration(1, new TotalDeathsOverlayOptions(true, true, false,
                new OverlayAppearance("Deaths", "Verdana", 39, "#F7F6FF", "#A78BFA", "#15171B", 0, 0, 0, OverlayTextAlignment.Left))),
            textExports: new TextExportConfiguration(export, true),
            manualDemonsSoulsDeathCounter: ManualDeathCounter.CreateFor(GameId.DemonsSouls, 37),
            checkForUpdatesOnStartup: true);
        await using (var repository = new SoulsTracker.Infrastructure.SqliteTrackerStateRepository(local, "tracker.db"))
        {
            await repository.SaveAsync(state);
        }
        Write(export);
        Write(save);
        Write(runtime);
        Write(installed);
        Write(Path.Combine(local, "hosted-pairing.private"));
        Write(Path.Combine(local, "overlay-setup.private"));
        Write(Path.Combine(local, "AppearancePreview", "preview-data.dat"));
        Write(Path.Combine(local, "other-user-file.txt"));
        Write(Path.Combine(roaming, "state.json"));
        Write(Path.Combine(roaming, "other-user-file.txt"));
        foreach (string name in new[] { "tracker.db-wal", "tracker.db-shm", "tracker.db-journal", "tracker.db.pre-migration-20200101.bak" })
            Write(Path.Combine(local, name));
        Write(Path.Combine(roaming, "soulstracker-legacy-backup-20200101-0000.json"));
        string overrideState = Path.Combine(fixture, "DevelopmentOverride", "tracker.db");
        Write(overrideState);

        // Installer-managed payload is removed by Inno separately from user state.
        File.Delete(installed);
        ApplyInstallerCleanup(deleteChoice && !silent, local, roaming);
        Assert.False(File.Exists(installed));
        foreach (string path in new[] { export, save, runtime, overrideState,
            Path.Combine(local, "other-user-file.txt"), Path.Combine(roaming, "other-user-file.txt") })
            Assert.True(File.Exists(path), $"Uninstall removed protected fixture: {Path.GetFileName(path)}");
        foreach (string path in new[] { Path.Combine(local, "hosted-pairing.private"),
            Path.Combine(local, "overlay-setup.private"), Path.Combine(local, "AppearancePreview", "preview-data.dat") })
            Assert.Equal(!deleteChoice || silent, File.Exists(path));
        Assert.Equal(!deleteChoice || silent, File.Exists(Path.Combine(roaming, "state.json")));
        Assert.Equal(!deleteChoice || silent, File.Exists(Path.Combine(local, "tracker.db.pre-migration-20200101.bak")));
        Assert.Equal(!deleteChoice || silent, File.Exists(Path.Combine(roaming, "soulstracker-legacy-backup-20200101-0000.json")));
        foreach (string name in new[] { "tracker.db-wal", "tracker.db-shm", "tracker.db-journal", "tracker.db.writer.lock" })
            Assert.Equal(!deleteChoice || silent, File.Exists(Path.Combine(local, name)));

        // Reinstall loads the same ordinary local root; it must not resurrect prior state.
        await using var reinstalled = new SoulsTracker.Infrastructure.SqliteTrackerStateRepository(local, "tracker.db");
        var loaded = await reinstalled.LoadAsync();
        Assert.True(loaded.IsSuccess);
        PersistentTrackerState actual = Assert.IsType<PersistentTrackerState>(loaded.State);
        bool retained = !deleteChoice || silent;
        Assert.Equal(retained ? 37 : 0, actual.ManualDemonsSoulsDeathCounter.Value);
        Assert.Equal(retained ? "Verdana" : OverlayAppearance.Default.FontFamily, actual.OverlayConfiguration.TotalDeaths.Appearance.FontFamily);
        Assert.Equal(retained ? 39 : OverlayAppearance.Default.FontSize, actual.OverlayConfiguration.TotalDeaths.Appearance.FontSize);
        Assert.Equal(retained, actual.CheckForUpdatesOnStartup);
        Assert.Equal(retained, actual.TextExports.DeathsEnabled);
        Assert.Equal(retained ? export : null, actual.TextExports.DeathsPath);
    }

    private static void ApplyInstallerCleanup(bool delete, string local, string roaming)
    {
        if (!delete) return; // InitializeUninstall: No and UninstallSilent both set DeleteLocalSettings=False.
        string script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "SoulsTracker.iss"));
        Assert.Contains("if UninstallSilent() then\n    DeleteLocalSettings := False", script.Replace("\r\n", "\n"));
        Assert.Contains("if (CurUninstallStep = usPostUninstall) and DeleteLocalSettings then", script);
        Assert.DoesNotMatch(@"(?m)^\[UninstallDelete\]", script);
        Match procedure = Regex.Match(script, @"(?s)procedure DeleteSoulsTrackerSettings;(?<body>.*?)\bend;");
        Assert.True(procedure.Success);
        string body = procedure.Groups["body"].Value;
        Assert.Contains("LocalRoot := ExpandConstant('{localappdata}\\SoulsTracker');", body);
        Assert.Contains("RoamingRoot := ExpandConstant('{userappdata}\\SoulsTracker');", body);
        MatchCollection calls = Regex.Matches(body, @"DelTree\((LocalRoot|RoamingRoot) \+ '\\([^']+)', (True|False), True, (True|False)\);");
        Assert.Equal(11, calls.Count);
        foreach (Match call in calls)
        {
            string root = call.Groups[1].Value == "LocalRoot" ? local : roaming;
            string name = call.Groups[2].Value;
            bool isDirectory = call.Groups[3].Value == "True";
            if (isDirectory)
            {
                Assert.Equal("AppearancePreview", name);
                Assert.Equal("True", call.Groups[4].Value);
                string path = Path.Combine(root, name);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            else
            {
                Assert.Equal("False", call.Groups[4].Value);
                foreach (string path in Directory.Exists(root) ? Directory.GetFiles(root, name) : []) File.Delete(path);
            }
        }
        Assert.Contains("RemoveDir(RoamingRoot);", body);
        Assert.Contains("RemoveDir(LocalRoot);", body);
        foreach (string root in new[] { roaming, local })
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "installer", "SoulsTracker.iss"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Installer source not found.");
    }

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic fixture");
    }

    public void Dispose()
    {
        if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
    }
}
