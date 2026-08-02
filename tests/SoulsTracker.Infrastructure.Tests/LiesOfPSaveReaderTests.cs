using System.Buffers.Binary;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class LiesOfPSaveReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.LiesOfP", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParserTreatsValidatedOmittedDeathPropertyAsZero()
    {
        Assert.Equal(LiesOfPSaveParseOutcome.Success, LiesOfPSaveParser.TryReadTotalDeaths(Fixture.Create(null), out long total));
        Assert.Equal(0, total);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(203)]
    public void ParserReadsObservedYouDieCountIntProperty(int expected)
    {
        Assert.Equal(LiesOfPSaveParseOutcome.Success, LiesOfPSaveParser.TryReadTotalDeaths(Fixture.Create(expected), out long total));
        Assert.Equal(expected, total);
    }

    [Fact]
    public void ParserFailsClosedForMalformedAmbiguousTruncatedOversizedAndUnrelatedData()
    {
        byte[] duplicate = Fixture.Create(5, duplicateDeaths: true);
        Assert.Equal(LiesOfPSaveParseOutcome.Invalid, LiesOfPSaveParser.TryReadTotalDeaths(duplicate, out _));

        byte[] malformed = Fixture.Create(5);
        int marker = malformed.AsSpan().IndexOf("YouDieCount"u8);
        malformed[marker - sizeof(int)] = 0;
        Assert.Equal(LiesOfPSaveParseOutcome.Invalid, LiesOfPSaveParser.TryReadTotalDeaths(malformed, out _));

        byte[] positive = Fixture.Create(5);
        Assert.Equal(LiesOfPSaveParseOutcome.Unsupported, LiesOfPSaveParser.TryReadTotalDeaths(positive.AsSpan(0, 63), out _));
        Assert.Equal(LiesOfPSaveParseOutcome.Unsupported, LiesOfPSaveParser.TryReadTotalDeaths([.. "GVAS"u8, .. new byte[80]], out _));
        Assert.Equal(LiesOfPSaveParseOutcome.Unsupported, LiesOfPSaveParser.TryReadTotalDeaths(new byte[LiesOfPSaveParser.MaximumSupportedFileBytes + 1], out _));
    }

    [Fact]
    public async Task ReaderUsesNewestSuccessfullyParsedMemberOfOneLogicalCharacterAndNeverWrites()
    {
        string directory = Path.Combine(root, "account");
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "SaveData-1_Character_1.sav");
        string second = Path.Combine(directory, "SaveData-1_Character_2.sav");
        await File.WriteAllBytesAsync(first, Fixture.Create(7));
        await File.WriteAllBytesAsync(second, Fixture.Create(8));
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(second, DateTime.UtcNow);
        byte[] before = await File.ReadAllBytesAsync(second);

        var reader = new LiesOfPSaveDeathReader();
        reader.Configure(new LiesOfPSaveConfiguration(first));
        RuntimeGameReadResult result = (await reader.ReadAsync(default))!;

        Assert.Equal(RuntimeGameReaderStatus.Synced, result.Status);
        Assert.Equal(GameId.LiesOfP, result.Observation!.GameId);
        Assert.Equal(8, result.Observation.TotalDeaths.Value);
        Assert.Equal(before, await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task DiscoveryGroupsPairedMembersAndUsesNewestValidCopy()
    {
        string account = Path.Combine(root, "LiesofP", "Saved", "SaveGames", "6144");
        Directory.CreateDirectory(account);
        string first = Path.Combine(account, "SaveData-1_Character_1.sav");
        string second = Path.Combine(account, "SaveData-1_Character_2.sav");
        string characterTwo = Path.Combine(account, "SaveData-2_Character_2.sav");
        await File.WriteAllBytesAsync(first, Fixture.Create(4));
        await File.WriteAllBytesAsync(second, Fixture.Create(5));
        await File.WriteAllBytesAsync(characterTwo, Fixture.Create(null));
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(-1));

        var discovery = new LiesOfPSaveDiscovery(new TestRoots(root));
        IReadOnlyList<DiscoveredLocalSave> candidates = await discovery.DiscoverAsync(default);

        Assert.Equal(["Character 1", "Character 2"], candidates.Select(static candidate => candidate.Label));
        Assert.Equal(second, candidates[0].LocalPath);
    }

    [Fact]
    public async Task DiscoveryUsesUniqueNonSensitiveLabelsForDuplicateCharactersAcrossAccounts()
    {
        string firstAccount = Path.Combine(root, "LiesofP", "Saved", "SaveGames", "account-a");
        string secondAccount = Path.Combine(root, "LiesofP", "Saved", "SaveGames", "account-z");
        Directory.CreateDirectory(firstAccount);
        Directory.CreateDirectory(secondAccount);
        string first = Path.Combine(firstAccount, "SaveData-1_Character_2.sav");
        string second = Path.Combine(secondAccount, "SaveData-1_Character_2.sav");
        await File.WriteAllBytesAsync(first, Fixture.Create(4));
        await File.WriteAllBytesAsync(second, Fixture.Create(5));

        IReadOnlyList<DiscoveredLocalSave> candidates = await new LiesOfPSaveDiscovery(new TestRoots(root)).DiscoverAsync(default);

        Assert.Equal(["Character 1 (1)", "Character 1 (2)"], candidates.Select(static candidate => candidate.Label));
        Assert.Equal([first, second], candidates.Select(static candidate => candidate.LocalPath));
        Assert.All(candidates, candidate =>
        {
            Assert.DoesNotContain("account", candidate.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, candidate.Label, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ReaderRejectsReparsePointPairMemberAndUsesTheSafeSiblingWhenSupported()
    {
        string account = Path.Combine(root, "account");
        string external = Path.Combine(root, "external.sav");
        Directory.CreateDirectory(account);
        string linked = Path.Combine(account, "SaveData-1_Character_1.sav");
        string safe = Path.Combine(account, "SaveData-1_Character_2.sav");
        await File.WriteAllBytesAsync(external, Fixture.Create(9));
        await File.WriteAllBytesAsync(safe, Fixture.Create(7));
        File.SetLastWriteTimeUtc(external, DateTime.UtcNow);

        try { File.CreateSymbolicLink(linked, external); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.True(exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException);
            return;
        }

        var reader = new LiesOfPSaveDeathReader();
        reader.Configure(new LiesOfPSaveConfiguration(linked));
        RuntimeGameReadResult result = (await reader.ReadAsync(default))!;

        Assert.Equal(RuntimeGameReaderStatus.Synced, result.Status);
        Assert.Equal(7, result.Observation!.TotalDeaths.Value);
    }

    [Fact]
    public async Task DiscoveryRejectsWrongNamedMalformedAndOversizedMembers()
    {
        string account = Path.Combine(root, "LiesofP", "Saved", "SaveGames", "6144");
        Directory.CreateDirectory(account);
        await File.WriteAllBytesAsync(Path.Combine(account, "SaveData-1_Character_3.sav"), Fixture.Create(1));
        await File.WriteAllBytesAsync(Path.Combine(account, "SaveData-2_Character_2.sav"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(account, "SaveData-3_Character_1.sav"), new byte[LiesOfPSaveParser.MaximumSupportedFileBytes + 1]);

        var discovery = new LiesOfPSaveDiscovery(new TestRoots(root));
        Assert.Empty(await discovery.DiscoverAsync(default));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class TestRoots(params string[] roots) : ILiesOfPSteamInstallRootSource
    {
        public IEnumerable<string> GetInstallRoots(CancellationToken cancellationToken) => roots;
    }

    private static class Fixture
    {
        public static byte[] Create(int? deaths, bool duplicateDeaths = false)
        {
            var bytes = new List<byte>("GVAS"u8.ToArray());
            bytes.AddRange(new byte[60]);
            bytes.AddRange(IntProperty("TotalReceiveDamage", 0));
            if (deaths is int value) bytes.AddRange(IntProperty("YouDieCount", value));
            if (duplicateDeaths && deaths is int duplicate) bytes.AddRange(IntProperty("YouDieCount", duplicate));
            return bytes.ToArray();
        }

        private static byte[] IntProperty(string name, int value)
        {
            var result = new List<byte>();
            WriteString(result, name);
            WriteString(result, "IntProperty");
            WriteInt(result, sizeof(int));
            WriteInt(result, 0);
            result.Add(0);
            WriteInt(result, value);
            return result.ToArray();
        }

        private static void WriteString(List<byte> result, string value)
        {
            WriteInt(result, value.Length + 1);
            result.AddRange(System.Text.Encoding.ASCII.GetBytes(value));
            result.Add(0);
        }

        private static void WriteInt(List<byte> result, int value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            result.AddRange(buffer.ToArray());
        }
    }
}
