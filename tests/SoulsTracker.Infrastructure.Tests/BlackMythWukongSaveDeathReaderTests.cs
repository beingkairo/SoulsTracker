using System.Security.Cryptography;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class BlackMythWukongSaveDeathReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTrackerTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(300)]
    public void ParserReadsKnownTotalsIncludingMultiByteVarints(int deaths)
    {
        Assert.Equal(BlackMythWukongSaveParseOutcome.Success, BlackMythWukongSaveParser.TryReadTotalDeaths(WukongSaveFixture.Create(deaths), out long actual));
        Assert.Equal(deaths, actual);
    }

    [Fact]
    public void ParserTreatsAnOmittedDeathCountAsTheProtobufZeroDefault()
    {
        Assert.Equal(BlackMythWukongSaveParseOutcome.Success, BlackMythWukongSaveParser.TryReadTotalDeaths(WukongSaveFixture.Create(null), out long actual));
        Assert.Equal(0, actual);
    }

    [Fact]
    public void ParserFailsClosedForBadChecksumsUnsupportedMetadataDuplicatesWrongWiresAndOverflows()
    {
        byte[] checksumCorrupt = WukongSaveFixture.Create(8);
        checksumCorrupt[^1] ^= 0x01;
        Assert.Equal(BlackMythWukongSaveParseOutcome.Invalid, BlackMythWukongSaveParser.TryReadTotalDeaths(checksumCorrupt, out _));

        Assert.Equal(BlackMythWukongSaveParseOutcome.Unsupported, BlackMythWukongSaveParser.TryReadTotalDeaths(WukongSaveFixture.Create(8, buildRevision: 23832), out _));

        byte[] duplicateOuterField = WukongSaveFixture.Create(8).Concat(new byte[] { 0x0A, 0x00 }).ToArray();
        Assert.Equal(BlackMythWukongSaveParseOutcome.Invalid, BlackMythWukongSaveParser.TryReadTotalDeaths(duplicateOuterField, out _));

        Assert.Equal(BlackMythWukongSaveParseOutcome.Invalid, BlackMythWukongSaveParser.TryReadTotalDeaths([0x08, 0x01], out _));
        Assert.Equal(BlackMythWukongSaveParseOutcome.Invalid, BlackMythWukongSaveParser.TryReadTotalDeaths([0x0A, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02], out _));
    }

    [Fact]
    public void ParserRejectsEveryTruncationBoundary()
    {
        byte[] archive = WukongSaveFixture.Create(8);
        for (int length = 0; length < archive.Length; length++)
        {
            Assert.Equal(BlackMythWukongSaveParseOutcome.Invalid, BlackMythWukongSaveParser.TryReadTotalDeaths(archive.AsSpan(0, length), out _));
        }
    }

    [Fact]
    public async Task ReaderWaitsForSelectionReadsOnlyTheSelectedArchiveAndPreservesZero()
    {
        var reader = new BlackMythWukongSaveDeathReader();
        Assert.Equal(RuntimeGameReaderStatus.WaitingForSaveFile, (await reader.ReadAsync(default))!.Status);

        string path = WriteArchive("ArchiveSaveFile.1.sav", WukongSaveFixture.Create(null));
        byte[] before = await File.ReadAllBytesAsync(path);
        reader.Configure(new BlackMythWukongSaveConfiguration(path));

        RuntimeGameReadResult result = (await reader.ReadAsync(default))!;
        Assert.Equal(RuntimeGameReaderStatus.Synced, result.Status);
        Assert.Equal(GameId.BlackMythWukong, result.Observation!.GameId);
        Assert.Equal(0, result.Observation.TotalDeaths.Value);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReaderRejectsMissingLockedAndPartialArchivesThenUpdatesOnceAfterACompletedWrite()
    {
        var reader = new BlackMythWukongSaveDeathReader();
        reader.Configure(new BlackMythWukongSaveConfiguration(Path.Combine(root, "ArchiveSaveFile.1.sav")));
        Assert.Null(await reader.ReadAsync(default));

        string path = WriteArchive("ArchiveSaveFile.1.sav", WukongSaveFixture.Create(7));
        reader.Configure(new BlackMythWukongSaveConfiguration(path));
        Assert.Equal(7, (await reader.ReadAsync(default))!.Observation!.TotalDeaths.Value);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(await reader.ReadAsync(default));
            var lockedReader = new BlackMythWukongSaveDeathReader();
            lockedReader.Configure(new BlackMythWukongSaveConfiguration(path));
            Assert.Null(await lockedReader.ReadAsync(default));
        }

        byte[] completed = WukongSaveFixture.Create(8);
        await File.WriteAllBytesAsync(path, completed.AsMemory(0, completed.Length / 2));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.Null(await reader.ReadAsync(default));

        await File.WriteAllBytesAsync(path, completed);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
        RuntimeGameReadResult updated = (await reader.ReadAsync(default))!;
        Assert.Equal(8, updated.Observation!.TotalDeaths.Value);
        Assert.Same(updated, await reader.ReadAsync(default));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private string WriteArchive(string fileName, byte[] contents)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, contents);
        return path;
    }

    /// <summary>Creates synthetic protocol fixtures only; it never contains a player archive.</summary>
    private static class WukongSaveFixture
    {
        private static ReadOnlySpan<byte> XorKey => [0x7B, 0x5C, 0xDA, 0x91, 0x3E, 0xFC, 0xDA, 0x37];
        private static ReadOnlySpan<byte> Salt => "lhx2tkh6lj1wj8jmrgs3k1xb2brusehx"u8;

        public static byte[] Create(int? deaths, int buildRevision = 23831)
        {
            byte[] deathData = deaths is int value ? FieldVarint(1, checked((ulong)value)) : [];
            byte[] persistentBgc = FieldBytes(5, deathData);
            byte[] persistentEcs = FieldBytes(1, persistentBgc);
            byte[] decodedPayload = FieldBytes(6, persistentEcs);
            byte[] encryptedPayload = decodedPayload.ToArray();
            for (int index = 0; index < encryptedPayload.Length; index++) encryptedPayload[index] ^= XorKey[index % XorKey.Length];

            byte[] checksumInput = encryptedPayload.Concat(Salt.ToArray()).ToArray();
#pragma warning disable CA5351 // Synthetic compatibility fixture for the game's existing archive checksum.
            byte[] checksum = Convert.ToHexStringLower(MD5.HashData(checksumInput)).Select(static character => (byte)character).ToArray();
#pragma warning restore CA5351
            byte[] metadata = Concat(
                FieldBytes(1, checksum),
                FieldVarint(7, 14),
                FieldVarint(8, 1),
                FieldVarint(10, checked((ulong)buildRevision)),
                FieldVarint(11, checked((ulong)buildRevision)));
            return Concat(FieldBytes(1, metadata), FieldBytes(2, encryptedPayload));
        }

        private static byte[] FieldBytes(ulong field, ReadOnlySpan<byte> value)
        {
            var result = new List<byte>();
            WriteVarint(result, field << 3 | 2);
            WriteVarint(result, checked((ulong)value.Length));
            result.AddRange(value.ToArray());
            return result.ToArray();
        }

        private static byte[] FieldVarint(ulong field, ulong value)
        {
            var result = new List<byte>();
            WriteVarint(result, field << 3);
            WriteVarint(result, value);
            return result.ToArray();
        }

        private static byte[] Concat(params byte[][] values) => values.SelectMany(static value => value).ToArray();

        private static void WriteVarint(List<byte> target, ulong value)
        {
            do
            {
                byte next = (byte)(value & 0x7F);
                value >>= 7;
                target.Add(value == 0 ? next : (byte)(next | 0x80));
            }
            while (value != 0);
        }
    }
}
