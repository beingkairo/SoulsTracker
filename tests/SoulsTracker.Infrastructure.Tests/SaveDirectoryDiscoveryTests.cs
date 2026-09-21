using System.Reflection;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class SaveDirectoryDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.Directory.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EldenRingDirectoryDiscoversDirectAndBoundedAccountSavesWithoutScanningParents()
    {
        string selected = Path.Combine(root, "selected");
        string direct = Write(selected);
        string account = Write(Path.Combine(selected, "account"));
        Write(Path.Combine(selected, "account", "nested"));
        Write(root);
        byte[] before = File.ReadAllBytes(direct);

        IReadOnlyList<DiscoveredLocalSave> result = Discover(typeof(EldenRingSaveDiscovery), selected);

        Assert.Equal(new[] { direct, account }.Order(StringComparer.OrdinalIgnoreCase), result.Select(x => x.LocalPath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(before, File.ReadAllBytes(direct));
        Assert.Empty(Discover(typeof(EldenRingSaveDiscovery), direct));
        Assert.Empty(Discover(typeof(EldenRingSaveDiscovery), Path.Combine(root, "missing")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryKeepsSlotOrderingPairRulesAndBoundedAccountSearch(bool lies)
    {
        Type type = lies ? typeof(LiesOfPSaveDiscovery) : typeof(BlackMythWukongSaveDiscovery);
        byte[] bytes = lies ? LiesOfPSaveReaderTests.Fixture.Create(7) : BlackMythWukongSaveDiscoveryTests.CreateArchive(7);
        string Name(int slot) => lies ? $"SaveData-{slot}_Character_1.sav" : $"ArchiveSaveFile.{slot}.sav";
        string selected = Path.Combine(root, "selected");
        Directory.CreateDirectory(selected);
        string second = Path.Combine(selected, Name(2));
        string tenth = Path.Combine(selected, Name(10));
        File.WriteAllBytes(second, bytes);
        File.WriteAllBytes(tenth, bytes);
        string account = Path.Combine(selected, "account");
        Directory.CreateDirectory(account);
        string child = Path.Combine(account, Name(3));
        File.WriteAllBytes(child, bytes);
        Directory.CreateDirectory(Path.Combine(account, "nested"));
        File.WriteAllBytes(Path.Combine(account, "nested", Name(4)), bytes);
        File.WriteAllBytes(Path.Combine(root, Name(5)), bytes);
        File.WriteAllBytes(Path.Combine(selected, Name(6)), [1, 2]);
        if (lies)
        {
            string pair = Path.Combine(selected, "SaveData-2_Character_2.sav");
            File.WriteAllBytes(pair, bytes);
            File.SetLastWriteTimeUtc(pair, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(second, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        Assert.Equal(new[] { second, child, tenth }, Discover(type, selected).Select(x => x.LocalPath));
        Assert.Equal(bytes, File.ReadAllBytes(second));
        Assert.Empty(Discover(type, second));
        Assert.Empty(Discover(type, Path.Combine(root, "missing")));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        Assert.Empty(Discover(type, Path.Combine(root, "empty")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingOnlyDiscoveryDirectoryDoesNotResetReaderCache(bool eldenRing)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, eldenRing ? "ER0000.sl2" : "ArchiveSaveFile.1.sav");
        File.WriteAllBytes(path, eldenRing ? EldenRingSaveDeathReaderTests.EldenRingSaveFixture.Create((0, 22)) : BlackMythWukongSaveDiscoveryTests.CreateArchive(22));
        var er = new EldenRingSaveDeathReader();
        var wk = new BlackMythWukongSaveDeathReader();
        IRuntimeGameDeathReader reader = eldenRing ? er : wk;

        if (eldenRing) er.Configure(new(path, 0));
        else wk.Configure(new(path));
        RuntimeGameReadResult first = (await reader.ReadAsync(default))!;
        if (eldenRing) er.Configure(new(path, 0, root));
        else wk.Configure(new(path, root));
        RuntimeGameReadResult next = (await reader.ReadAsync(default))!;
        Assert.Equal(RuntimeGameReaderStatus.Cached, next.Status);
        Assert.Equal(first.Observation, next.Observation);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public void DirectoryRejectsLinkedRootsAndAccountsWhenSupported(string game)
    {
        string selected = Path.Combine(root, "selected");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(outside);
        Type discovery;
        if (game == "er") { Write(outside); discovery = typeof(EldenRingSaveDiscovery); }
        else if (game == "wk")
        {
            File.WriteAllBytes(Path.Combine(outside, "ArchiveSaveFile.1.sav"), BlackMythWukongSaveDiscoveryTests.CreateArchive(7));
            discovery = typeof(BlackMythWukongSaveDiscovery);
        }
        else
        {
            File.WriteAllBytes(Path.Combine(outside, "SaveData-1_Character_1.sav"), LiesOfPSaveReaderTests.Fixture.Create(7));
            discovery = typeof(LiesOfPSaveDiscovery);
        }
        string link = Path.Combine(selected, "linked-account");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        try
        {
            Assert.Empty(Discover(discovery, selected));
            Assert.Empty(Discover(discovery, link));
            Assert.Single(Discover(discovery, outside));
        }
        finally { Directory.Delete(link); }
    }

    private static string Write(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "ER0000.sl2");
        File.WriteAllBytes(path, EldenRingSaveDiscoveryTests.CreateProfile((0, "Synthetic", 12)));
        return path;
    }

    internal static IReadOnlyList<DiscoveredLocalSave> Discover(Type discovery, string directory)
    {
        MethodInfo? method = discovery.GetMethod("DiscoverInDirectory", [typeof(string)]);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<DiscoveredLocalSave>>(method.Invoke(null, [directory]));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
