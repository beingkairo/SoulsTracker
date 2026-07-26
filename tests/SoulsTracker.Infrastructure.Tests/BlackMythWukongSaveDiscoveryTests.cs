using SoulsTracker.Infrastructure;
using SoulsTracker.Domain;
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
    public async Task ProductionSteamBoundaryFindsConventionalInstallWithoutLibraryMetadata()
    {
        string steam = Path.Combine(root, "steam");
        string install = Path.Combine(steam, "steamapps", "common", "Wukong");
        WriteValidSave(install, "account", 6, 6);
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        await File.WriteAllTextAsync(Path.Combine(steam, "steamapps", "appmanifest_2358720.acf"), "\"installdir\" \"Wukong\"");

        var discovery = new BlackMythWukongSaveDiscovery(new LocalBlackMythWukongInstallRootSource(new TestEnvironment(steam, steam, null)));

        DiscoveredLocalSave save = Assert.Single(await discovery.DiscoverAsync(default));
        Assert.Equal("Save slot 6", save.Label);
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

    [Fact]
    public async Task SteamRequiresExactBoundedMetadataAndRejectsEscapingInstallDirectories()
    {
        string conventional = Path.Combine(root, "conventional");
        string custom = Path.Combine(root, "custom");
        Directory.CreateDirectory(Path.Combine(conventional, "steamapps"));
        Directory.CreateDirectory(Path.Combine(custom, "steamapps"));
        await File.WriteAllTextAsync(Path.Combine(conventional, "steamapps", "libraryfolders.vdf"), $"\"path\" \"{custom.Replace("\\", "\\\\", StringComparison.Ordinal)}\"");

        var missing = new BlackMythWukongSaveDiscovery(new LocalBlackMythWukongInstallRootSource(new TestEnvironment(null, conventional, null)));
        Assert.Empty(await missing.DiscoverAsync(default));

        string manifest = Path.Combine(custom, "steamapps", "appmanifest_2358720.acf");
        foreach (string rejected in new[] { "\"installdir\" \"", "\"installdir\" \".\"", "\"installdir\" \"..\"", "\"installdir\" \"nested/game\"", "\"installdir\" \"nested\\\\game\"", $"\"installdir\" \"{Path.GetPathRoot(root)}\"" })
        {
            await File.WriteAllTextAsync(manifest, rejected);
            Assert.Empty(await missing.DiscoverAsync(default));
        }

        await File.WriteAllBytesAsync(manifest, new byte[1_048_577]);
        Assert.Empty(await missing.DiscoverAsync(default));
        await File.WriteAllTextAsync(manifest, "\"installdir\" \"Wukong\"");
        await File.WriteAllTextAsync(Path.Combine(conventional, "steamapps", "libraryfolders.vdf"), "\"path\" \"");
        Assert.Empty(await missing.DiscoverAsync(default));
        await File.WriteAllBytesAsync(Path.Combine(conventional, "steamapps", "libraryfolders.vdf"), new byte[1_048_577]);
        Assert.Empty(await missing.DiscoverAsync(default));
    }

    [Fact]
    public async Task SteamDeduplicatesConventionalRegistryAndRepeatedCustomRoots()
    {
        string steam = Path.Combine(root, "steam");
        string custom = Path.Combine(root, "custom");
        string install = Path.Combine(custom, "steamapps", "common", "Wukong");
        WriteValidSave(install, "account", 3, 3);
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(custom, "steamapps"));
        string escaped = custom.Replace("\\", "\\\\", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"path\" \"{escaped}\"\n\"path\" \"{escaped}\"");
        await File.WriteAllTextAsync(Path.Combine(custom, "steamapps", "appmanifest_2358720.acf"), "\"installdir\" \"Wukong\"");

        var discovery = new BlackMythWukongSaveDiscovery(new LocalBlackMythWukongInstallRootSource(new TestEnvironment(steam, steam, null)));

        Assert.Single(await discovery.DiscoverAsync(default));
    }

    [Fact]
    public async Task EpicRejectsMalformedOversizedMissingNetworkDuplicateAndParserInvalidManifests()
    {
        string manifests = Path.Combine(root, "manifests");
        string validInstall = Path.Combine(root, "valid-install");
        string invalidInstall = Path.Combine(root, "invalid-install");
        Directory.CreateDirectory(manifests);
        WriteValidSave(validInstall, "account", 5, 5);
        Directory.CreateDirectory(Path.Combine(invalidInstall, "b1", "Saved", "SaveGames", "account"));
        await File.WriteAllBytesAsync(Path.Combine(invalidInstall, "b1", "Saved", "SaveGames", "account", "ArchiveSaveFile.1.sav"), [1]);
        await File.WriteAllTextAsync(Path.Combine(manifests, "malformed.item"), "{");
        await File.WriteAllBytesAsync(Path.Combine(manifests, "oversized.item"), new byte[1_048_577]);
        await File.WriteAllTextAsync(Path.Combine(manifests, "missing.item"), "{}");
        await File.WriteAllTextAsync(Path.Combine(manifests, "network.item"), JsonSerializer.Serialize(new { InstallLocation = @"\\server\share" }));
        await File.WriteAllTextAsync(Path.Combine(manifests, "invalid.item"), JsonSerializer.Serialize(new { InstallLocation = invalidInstall }));
        await File.WriteAllTextAsync(Path.Combine(manifests, "valid.item"), JsonSerializer.Serialize(new { InstallLocation = validInstall }));
        await File.WriteAllTextAsync(Path.Combine(manifests, "duplicate.item"), JsonSerializer.Serialize(new { InstallLocation = validInstall }));

        var discovery = new BlackMythWukongSaveDiscovery(new LocalBlackMythWukongInstallRootSource(new TestEnvironment(null, null, manifests)));

        DiscoveredLocalSave save = Assert.Single(await discovery.DiscoverAsync(default));
        Assert.Equal("Save slot 5", save.Label);
    }

    [Fact]
    public async Task DiscoveryRejectsInvalidUnsupportedOversizedLockedWrongNamedNestedAndBackupSaves()
    {
        string account = Path.Combine(root, "b1", "Saved", "SaveGames", "account");
        Directory.CreateDirectory(account);
        await File.WriteAllBytesAsync(Path.Combine(account, "ArchiveSaveFile.1.sav"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(account, "ArchiveSaveFile.2.sav"), CreateArchive(2, 23832));
        await File.WriteAllBytesAsync(Path.Combine(account, "ArchiveSaveFile.3.sav"), new byte[8 * 1024 * 1024 + 1]);
        await File.WriteAllBytesAsync(Path.Combine(account, "WrongName.sav"), CreateArchive(4));
        Directory.CreateDirectory(Path.Combine(account, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(account, "nested", "ArchiveSaveFile.5.sav"), CreateArchive(5));
        string backup = Path.Combine(root, "b1", "Saved", "SaveGamesBackup", "account");
        Directory.CreateDirectory(backup);
        await File.WriteAllBytesAsync(Path.Combine(backup, "ArchiveSaveFile.6.sav"), CreateArchive(6));
        string lockedPath = Path.Combine(account, "ArchiveSaveFile.7.sav");
        await File.WriteAllBytesAsync(lockedPath, CreateArchive(7));

        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var discovery = new BlackMythWukongSaveDiscovery(new TestInstallRoots(root));
            Assert.Empty(await discovery.DiscoverAsync(default));
        }
    }

    [Fact]
    public async Task DuplicateSlotNumbersReceiveDeterministicNonSensitiveLabels()
    {
        WriteValidSave(root, "account-z", 1, 1);
        WriteValidSave(root, "account-a", 1, 2);
        var discovery = new BlackMythWukongSaveDiscovery(new TestInstallRoots(root));

        IReadOnlyList<DiscoveredLocalSave> first = await discovery.DiscoverAsync(default);
        IReadOnlyList<DiscoveredLocalSave> second = await discovery.DiscoverAsync(default);

        Assert.Equal(["Save slot 1 (1)", "Save slot 1 (2)"], first.Select(static save => save.Label));
        Assert.Equal(first, second);
        Assert.All(first, save =>
        {
            Assert.DoesNotContain("account", save.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, save.Label, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task EpicRejectsReparseManifestAndAccountWhenRuntimeSupportsReparseCreation()
    {
        string manifests = Path.Combine(root, "manifests");
        string install = Path.Combine(root, "install");
        string external = Path.Combine(root, "external");
        Directory.CreateDirectory(manifests);
        WriteValidSave(external, "real-account", 8, 8);
        Directory.CreateDirectory(Path.Combine(install, "b1", "Saved", "SaveGames"));
        string accountLink = Path.Combine(install, "b1", "Saved", "SaveGames", "linked-account");
        string manifestTarget = Path.Combine(root, "manifest-target.item");
        await File.WriteAllTextAsync(manifestTarget, JsonSerializer.Serialize(new { InstallLocation = install }));
        string manifestLink = Path.Combine(manifests, "linked.item");
        try
        {
            Directory.CreateSymbolicLink(accountLink, Path.Combine(external, "b1", "Saved", "SaveGames", "real-account"));
            File.CreateSymbolicLink(manifestLink, manifestTarget);
            await File.WriteAllTextAsync(Path.Combine(manifests, "regular.item"), JsonSerializer.Serialize(new { InstallLocation = install }));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.True(exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException);
            return;
        }

        var discovery = new BlackMythWukongSaveDiscovery(new LocalBlackMythWukongInstallRootSource(new TestEnvironment(null, null, manifests)));

        Assert.Empty(await discovery.DiscoverAsync(default));
    }

    [Fact]
    public void RegularSaveValidationRejectsMissingWrongNameAndOversizedFiles()
    {
        string account = Path.Combine(root, "account");
        Directory.CreateDirectory(account);
        string wrongName = Path.Combine(account, "save.sav");
        File.WriteAllBytes(wrongName, CreateArchive(1));
        string oversized = Path.Combine(account, "ArchiveSaveFile.2.sav");
        File.WriteAllBytes(oversized, new byte[8 * 1024 * 1024 + 1]);

        Assert.False(BlackMythWukongSaveDiscovery.IsRegularBoundedSave(Path.Combine(account, "ArchiveSaveFile.1.sav")));
        Assert.True(BlackMythWukongSaveDiscovery.IsRegularBoundedSave(wrongName));
        Assert.False(BlackMythWukongSaveDiscovery.IsRegularBoundedSave(oversized));
        Assert.False(BlackMythWukongSaveConfiguration.IsArchiveSaveFileName(Path.GetFileName(wrongName)));
    }

    private sealed class TestInstallRoots(params string[] roots) : IBlackMythWukongInstallRootSource
    {
        public IEnumerable<string> GetInstallRoots(CancellationToken cancellationToken) => roots;
    }

    private sealed record TestEnvironment(string? CurrentUserSteamRoot, string? ConventionalSteamRoot, string? EpicManifestRoot) : IBlackMythWukongLauncherEnvironment;

    private static void WriteValidSave(string installRoot, string account, int slot, int deaths)
    {
        string saveRoot = Path.Combine(installRoot, "b1", "Saved", "SaveGames", account);
        Directory.CreateDirectory(saveRoot);
        File.WriteAllBytes(Path.Combine(saveRoot, $"ArchiveSaveFile.{slot}.sav"), CreateArchive(deaths));
    }

    private static byte[] CreateArchive(int deaths, int buildRevision = 23831)
    {
        byte[] death = FieldVarint(1, (ulong)deaths);
        byte[] decoded = FieldBytes(6, FieldBytes(1, FieldBytes(5, death)));
        byte[] encrypted = decoded.ToArray();
        byte[] key = [0x7B, 0x5C, 0xDA, 0x91, 0x3E, 0xFC, 0xDA, 0x37];
        for (int index = 0; index < encrypted.Length; index++) encrypted[index] ^= key[index % key.Length];
#pragma warning disable CA5351
        byte[] checksum = Convert.ToHexStringLower(MD5.HashData(encrypted.Concat("lhx2tkh6lj1wj8jmrgs3k1xb2brusehx"u8.ToArray()).ToArray())).Select(static value => (byte)value).ToArray();
#pragma warning restore CA5351
        byte[] metadata = Concat(FieldBytes(1, checksum), FieldVarint(7, 14), FieldVarint(8, 1), FieldVarint(10, (ulong)buildRevision), FieldVarint(11, (ulong)buildRevision));
        return Concat(FieldBytes(1, metadata), FieldBytes(2, encrypted));
    }

    private static byte[] FieldBytes(ulong field, byte[] value) { var result = new List<byte>(); Write(result, field << 3 | 2); Write(result, (ulong)value.Length); result.AddRange(value); return result.ToArray(); }
    private static byte[] FieldVarint(ulong field, ulong value) { var result = new List<byte>(); Write(result, field << 3); Write(result, value); return result.ToArray(); }
    private static byte[] Concat(params byte[][] values) => values.SelectMany(static value => value).ToArray();
    private static void Write(List<byte> target, ulong value) { do { byte next = (byte)(value & 0x7F); value >>= 7; target.Add(value == 0 ? next : (byte)(next | 0x80)); } while (value != 0); }
}
