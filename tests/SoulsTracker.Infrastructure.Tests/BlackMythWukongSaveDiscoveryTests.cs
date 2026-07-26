using SoulsTracker.Infrastructure;
using System.Security.Cryptography;
using System.Text.Json;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class BlackMythWukongSaveDiscoveryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.WukongDiscovery", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }

    [Fact]
    public async Task DiscoversOnlyValidImmediateSlotsInNaturalOrderAndDeduplicatesRoots()
    {
        string saveGames = Path.Combine(root, "b1", "Saved", "SaveGames", "account");
        Directory.CreateDirectory(saveGames);
        await File.WriteAllBytesAsync(Path.Combine(saveGames, "ArchiveSaveFile.10.sav"), CreateArchive(10));
        await File.WriteAllBytesAsync(Path.Combine(saveGames, "ArchiveSaveFile.2.sav"), CreateArchive(2));
        await File.WriteAllBytesAsync(Path.Combine(saveGames, "ArchiveSaveFile.invalid.sav"), [1]);
        Directory.CreateDirectory(Path.Combine(saveGames, "backup"));
        await File.WriteAllBytesAsync(Path.Combine(saveGames, "backup", "ArchiveSaveFile.1.sav"), [1]);

        var discovery = new BlackMythWukongSaveDiscovery(new TestInstallRoots(root, root));

        IReadOnlyList<DiscoveredLocalSave> saves = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(["Save slot 2", "Save slot 10"], saves.Select(static save => save.Label));
        Assert.Equal(2, saves.Count);
    }

    [Fact]
    public async Task HonorsCancellationBeforeInspectingRoots()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var discovery = new BlackMythWukongSaveDiscovery(new TestInstallRoots(root));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await discovery.DiscoverAsync(cancellation.Token));
    }

    [Fact]
    public async Task ProductionSteamBoundaryFindsConventionalAndCustomLibrariesButRejectsEscapedInstallDirectories()
    {
        string steam = Path.Combine(root, "steam");
        string custom = Path.Combine(root, "custom");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(custom, "steamapps", "common", "Wukong", "b1", "Saved", "SaveGames", "a"));
        await File.WriteAllTextAsync(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"path\" \"{custom.Replace("\\", "\\\\", StringComparison.Ordinal)}\"");
        await File.WriteAllTextAsync(Path.Combine(custom, "steamapps", "appmanifest_2358720.acf"), "\"installdir\" \"Wukong\"");
        await File.WriteAllBytesAsync(Path.Combine(custom, "steamapps", "common", "Wukong", "b1", "Saved", "SaveGames", "a", "ArchiveSaveFile.1.sav"), CreateArchive(1));
        await File.WriteAllTextAsync(Path.Combine(steam, "steamapps", "appmanifest_2358720.acf"), "\"installdir\" \"..\\escape\"");

        var roots = new LocalBlackMythWukongInstallRootSource(new TestEnvironment(steam, null, null));
        var discovery = new BlackMythWukongSaveDiscovery(roots);

        DiscoveredLocalSave save = Assert.Single(await discovery.DiscoverAsync(default));
        Assert.Equal("Save slot 1", save.Label);
    }

    [Fact]
    public async Task ProductionEpicBoundaryUsesParserValidInstallWithoutAppName()
    {
        string manifests = Path.Combine(root, "epic", "manifests");
        string install = Path.Combine(root, "epic-install");
        Directory.CreateDirectory(Path.Combine(manifests));
        Directory.CreateDirectory(Path.Combine(install, "b1", "Saved", "SaveGames", "a"));
        await File.WriteAllBytesAsync(Path.Combine(install, "b1", "Saved", "SaveGames", "a", "ArchiveSaveFile.4.sav"), CreateArchive(4));
        await File.WriteAllTextAsync(Path.Combine(manifests, "one.item"), JsonSerializer.Serialize(new { InstallLocation = install }));
        await File.WriteAllTextAsync(Path.Combine(manifests, "bad.item"), "{");

        var discovery = new BlackMythWukongSaveDiscovery(new LocalBlackMythWukongInstallRootSource(new TestEnvironment(null, null, manifests)));

        DiscoveredLocalSave save = Assert.Single(await discovery.DiscoverAsync(default));
        Assert.Equal("Save slot 4", save.Label);
    }

    private sealed class TestInstallRoots(params string[] roots) : IBlackMythWukongInstallRootSource
    {
        public IEnumerable<string> GetInstallRoots(CancellationToken cancellationToken) => roots;
    }

    private sealed record TestEnvironment(string? CurrentUserSteamRoot, string? ConventionalSteamRoot, string? EpicManifestRoot) : IBlackMythWukongLauncherEnvironment;

    private static byte[] CreateArchive(int deaths)
    {
        byte[] death = FieldVarint(1, (ulong)deaths);
        byte[] decoded = FieldBytes(6, FieldBytes(1, FieldBytes(5, death)));
        byte[] encrypted = decoded.ToArray();
        byte[] key = [0x7B, 0x5C, 0xDA, 0x91, 0x3E, 0xFC, 0xDA, 0x37];
        for (int index = 0; index < encrypted.Length; index++) encrypted[index] ^= key[index % key.Length];
#pragma warning disable CA5351
        byte[] checksum = Convert.ToHexStringLower(MD5.HashData(encrypted.Concat("lhx2tkh6lj1wj8jmrgs3k1xb2brusehx"u8.ToArray()).ToArray())).Select(static value => (byte)value).ToArray();
#pragma warning restore CA5351
        byte[] metadata = Concat(FieldBytes(1, checksum), FieldVarint(7, 14), FieldVarint(8, 1), FieldVarint(10, 23831), FieldVarint(11, 23831));
        return Concat(FieldBytes(1, metadata), FieldBytes(2, encrypted));
    }

    private static byte[] FieldBytes(ulong field, byte[] value) { var result = new List<byte>(); Write(result, field << 3 | 2); Write(result, (ulong)value.Length); result.AddRange(value); return result.ToArray(); }
    private static byte[] FieldVarint(ulong field, ulong value) { var result = new List<byte>(); Write(result, field << 3); Write(result, value); return result.ToArray(); }
    private static byte[] Concat(params byte[][] values) => values.SelectMany(static value => value).ToArray();
    private static void Write(List<byte> target, ulong value) { do { byte next = (byte)(value & 0x7F); value >>= 7; target.Add(value == 0 ? next : (byte)(next | 0x80)); } while (value != 0); }
}
