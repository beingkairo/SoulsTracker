using System.IO.Compression;
using System.Text;
using Soulstracker.DeathTracking;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HollowKnight12620Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.Hk12620", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> runtime = HollowKnightApiProfile.V78.RuntimeFiles.Keys.ToDictionary(name => name, name => Encoding.UTF8.GetBytes("runtime:" + name), StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> vanilla = new(StringComparer.Ordinal)
    {
        ["Assembly-CSharp.dll"] = "vanilla main"u8.ToArray(),
        ["TeamCherry.Localization.dll"] = "vanilla localization"u8.ToArray(),
        ["UnityEngine.CoreModule.dll"] = "vanilla engine"u8.ToArray()
    };
    private HollowKnightApiProfile Profile => new("1.5.12620", "1.5.12620.0-78", runtime.ToDictionary(x => x.Key, x => HollowKnightPluginInstaller.Hash(x.Value), StringComparer.Ordinal),
        vanilla.ToDictionary(x => x.Key, x => HollowKnightPluginInstaller.Hash(x.Value), StringComparer.Ordinal), null);
    private HollowKnightPluginInstaller Installer(Action<int>? fail = null, bool running = false) => new(() => running,
        path => new(path, "1.5.12620", File.ReadAllText(Target("Assembly-CSharp.dll")).StartsWith("runtime:", StringComparison.Ordinal), true), fail, Profile);
    private string Target(string file) => Path.Combine(root, "hollow_knight_Data", "Managed", file);
    private void Create()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target("Assembly-CSharp.dll"))!);
        File.WriteAllText(Path.Combine(root, "hollow_knight.exe"), "fixture only");
        foreach (var file in vanilla) File.WriteAllBytes(Target(file.Key), file.Value);
    }
    private byte[] Archive(bool corrupt = false, bool extra = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var item in runtime)
            {
                using Stream output = zip.CreateEntry(item.Key).Open();
                output.Write(corrupt && item.Key == "Assembly-CSharp.dll" ? "unverified binary"u8 : item.Value);
            }
            if (extra) { using Stream output = zip.CreateEntry("unexpected.dll").Open(); output.WriteByte(1); }
        }
        return memory.ToArray();
    }

    [Theory]
    [InlineData("1.5.78.11833", "1.5.78.11833-77", true)]
    [InlineData("1.5.12620", "1.5.12620.0-78", true)]
    [InlineData("1.5.12620", "1.5.78.11833-77", false)]
    [InlineData("1.5.78.11833", "1.5.12620.0-78", false)]
    [InlineData("1.5.12620", "1.5.12620.0-79", false)]
    [InlineData("1.5.12621", "1.5.12620.0-78", false)]
    public void PluginAllowsOnlyVerifiedPairs(string game, string api, bool allowed) => Assert.Equal(allowed, HollowKnightCompatibility.SupportsRuntime(game, api));

    [Fact]
    public void ModernProfileInstallsReusesUpdatesAndUninstallsWithoutRemovingSharedFiles()
    {
        Create();
        string other = Target("Mods/Other/other.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!); File.WriteAllText(other, "preserve");
        var installer = Installer();
        var plan = installer.Prepare(root, [1], Archive());
        Assert.Equal("1.5.12620.0-78", plan.ApiProfile);
        Assert.Equal(24, plan.Files.Count);
        installer.Apply(plan);
        var update = installer.Prepare(root, [2], null);
        Assert.True(update.ReusesApi); Assert.Equal(plan.InstallationId, update.InstallationId);
        installer.Apply(update);
        installer.Uninstall(root);
        foreach (var file in runtime) Assert.Equal(file.Value, File.ReadAllBytes(Target(file.Key)));
        Assert.Equal("preserve", File.ReadAllText(other));
        Assert.Equal(vanilla["UnityEngine.CoreModule.dll"], File.ReadAllBytes(Target("UnityEngine.CoreModule.dll")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(23)]
    public void ModernInstallRollsBackItsWholeTransaction(int failureAt)
    {
        Create();
        var installer = Installer(index => { if (index == failureAt) throw new IOException("simulated failure"); });
        Assert.Throws<IOException>(() => installer.Apply(installer.Prepare(root, [1], Archive())));
        foreach (var file in vanilla) Assert.Equal(file.Value, File.ReadAllBytes(Target(file.Key)));
        foreach (string file in runtime.Keys.Except(vanilla.Keys)) Assert.False(File.Exists(Target(file)));
    }

    [Fact]
    public void ModernRecoverySurvivesRestartAndRetainsProfileIdentity()
    {
        Create();
        bool running = false;
        var installer = new HollowKnightPluginInstaller(() => running, path => new(path, "1.5.12620", false, true),
            _ => { running = true; throw new IOException("interruption"); }, Profile);
        Assert.Throws<IOException>(() => installer.Apply(installer.Prepare(root, [1], Archive())));
        Installer().RollbackPending(root);
        foreach (var file in vanilla) Assert.Equal(file.Value, File.ReadAllBytes(Target(file.Key)));
    }

    [Fact]
    public void WrongInputsPackagesAndPostConfirmationChangesNeverWriteGameFiles()
    {
        Create();
        Assert.Throws<IOException>(() => Installer().Prepare(root, [1], Archive(corrupt: true)));
        Assert.Throws<IOException>(() => Installer().Prepare(root, [1], Archive(extra: true)));
        Assert.Throws<IOException>(() => Installer(running: true).Prepare(root, [1], Archive()));
        var plan = Installer().Prepare(root, [1], Archive());
        File.WriteAllText(Target("UnityEngine.CoreModule.dll"), "changed after confirmation");
        Assert.Throws<IOException>(() => Installer().Apply(plan));
        Assert.Throws<IOException>(() => Installer().Prepare(root, [1], Archive()));
        Assert.Equal(vanilla["Assembly-CSharp.dll"], File.ReadAllBytes(Target("Assembly-CSharp.dll")));
        Assert.False(Directory.Exists(Path.Combine(root, ".soulstracker-death-tracking")));
    }

    [Fact]
    public void IncompatibleSharedComponentsAndUnknownApiArePreserved()
    {
        Create();
        File.WriteAllText(Target("MonoMod.RuntimeDetour.dll"), "a different installed dependency");
        Assert.Throws<IOException>(() => Installer().Prepare(root, [1], Archive()));
        Assert.Equal("a different installed dependency", File.ReadAllText(Target("MonoMod.RuntimeDetour.dll")));
        File.WriteAllText(Target("Assembly-CSharp.dll"), "runtime:unknown API");
        Assert.Throws<IOException>(() => Installer().Prepare(root, [1], Archive()));
        Assert.Equal("runtime:unknown API", File.ReadAllText(Target("Assembly-CSharp.dll")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
