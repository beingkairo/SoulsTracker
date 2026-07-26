using System.Buffers.Binary;
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
    public void ParserReadsVerifiedCharacterMetadataTypesAndMultiByteLevel()
    {
        const ulong unixSeconds = 1_720_000_000;
        byte[] archive = WukongSaveFixture.Create(
            12,
            levelField: WukongSaveFixture.FieldVarint(4, 300),
            playTimeField: WukongSaveFixture.FieldFixed32(2, BitConverter.SingleToUInt32Bits(7_740f)),
            timestampField: WukongSaveFixture.FieldVarint(5, unixSeconds));

        Assert.Equal(
            BlackMythWukongSaveParseOutcome.Success,
            BlackMythWukongSaveParser.TryRead(archive, out long deaths, out BlackMythWukongSaveMetadata? metadata));
        Assert.Equal(12, deaths);
        Assert.NotNull(metadata);
        Assert.Equal(300, metadata.Level);
        Assert.Equal(TimeSpan.FromMinutes(129), metadata.TotalPlayTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds), metadata.LastSaved);
    }

    [Fact]
    public void ParserTreatsEveryCharacterMetadataFieldAsIndependentlyOptional()
    {
        byte[] archive = WukongSaveFixture.Create(
            4,
            levelField: WukongSaveFixture.Concat(
                WukongSaveFixture.FieldVarint(4, 8),
                WukongSaveFixture.FieldVarint(4, 9)),
            playTimeField: WukongSaveFixture.FieldFixed32(2, BitConverter.SingleToUInt32Bits(90f)),
            timestampField: WukongSaveFixture.FieldVarint(5, 1_720_000_000));

        Assert.Equal(
            BlackMythWukongSaveParseOutcome.Success,
            BlackMythWukongSaveParser.TryRead(archive, out long deaths, out BlackMythWukongSaveMetadata? metadata));
        Assert.Equal(4, deaths);
        Assert.NotNull(metadata);
        Assert.Null(metadata.Level);
        Assert.Equal(TimeSpan.FromSeconds(90), metadata.TotalPlayTime);
        Assert.NotNull(metadata.LastSaved);

        Assert.Equal(
            BlackMythWukongSaveParseOutcome.Success,
            BlackMythWukongSaveParser.TryRead(WukongSaveFixture.Create(4), out _, out metadata));
        Assert.Null(metadata);
    }

    [Theory]
    [MemberData(nameof(InvalidMetadataFields))]
    public void ParserOmitsInvalidMetadataWithoutChangingDeathSemantics(
        byte[]? levelField,
        byte[]? playTimeField,
        byte[]? timestampField)
    {
        byte[] archive = WukongSaveFixture.Create(
            19,
            levelField: levelField,
            playTimeField: playTimeField,
            timestampField: timestampField);

        Assert.Equal(
            BlackMythWukongSaveParseOutcome.Success,
            BlackMythWukongSaveParser.TryRead(archive, out long deaths, out BlackMythWukongSaveMetadata? metadata));
        Assert.Equal(19, deaths);
        Assert.Null(metadata);
    }

    public static TheoryData<byte[]?, byte[]?, byte[]?> InvalidMetadataFields => new()
    {
        { WukongSaveFixture.FieldVarint(4, 0), null, null },
        { WukongSaveFixture.FieldVarint(4, (ulong)int.MaxValue + 1), null, null },
        { WukongSaveFixture.FieldFixed32(4, 1), null, null },
        { WukongSaveFixture.Concat(WukongSaveFixture.FieldVarint(4, 1), WukongSaveFixture.FieldVarint(4, 2)), null, null },
        { [0x20, 0x80], null, null },
        { null, WukongSaveFixture.FieldVarint(2, 1), null },
        { null, WukongSaveFixture.Concat(WukongSaveFixture.FieldFixed32(2, 1), WukongSaveFixture.FieldFixed32(2, 2)), null },
        { null, WukongSaveFixture.FieldFixed32(2, BitConverter.SingleToUInt32Bits(float.NaN)), null },
        { null, WukongSaveFixture.FieldFixed32(2, BitConverter.SingleToUInt32Bits(float.PositiveInfinity)), null },
        { null, WukongSaveFixture.FieldFixed32(2, BitConverter.SingleToUInt32Bits(-1f)), null },
        { null, WukongSaveFixture.FieldFixed32(2, BitConverter.SingleToUInt32Bits(1e20f)), null },
        { null, [0x15, 0x00, 0x00, 0x00], null },
        { null, null, WukongSaveFixture.FieldBytes(5, []) },
        { null, null, WukongSaveFixture.Concat(WukongSaveFixture.FieldVarint(5, 1), WukongSaveFixture.FieldVarint(5, 2)) },
        { null, null, WukongSaveFixture.FieldVarint(5, 253_402_300_800UL) },
    };

    [Fact]
    public void ParserFailsClosedForBadChecksumsUnsupportedMetadataDuplicatesWrongWiresAndOverflows()
    {
        byte[] checksumCorrupt = WukongSaveFixture.Create(8);
        checksumCorrupt[^1] ^= 0x01;
        Assert.Equal(BlackMythWukongSaveParseOutcome.Invalid, BlackMythWukongSaveParser.TryReadTotalDeaths(checksumCorrupt, out _));

        Assert.Equal(BlackMythWukongSaveParseOutcome.Unsupported, BlackMythWukongSaveParser.TryReadTotalDeaths(WukongSaveFixture.Create(8, buildRevision: 23832), out _));
        Assert.Equal(
            BlackMythWukongSaveParseOutcome.Unsupported,
            BlackMythWukongSaveParser.TryRead(
                WukongSaveFixture.Create(
                    8,
                    buildRevision: 23832,
                    levelField: WukongSaveFixture.FieldVarint(4, 10)),
                out _,
                out BlackMythWukongSaveMetadata? unsupportedMetadata));
        Assert.Null(unsupportedMetadata);

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

        string path = WriteArchive(
            "ArchiveSaveFile.1.sav",
            WukongSaveFixture.Create(null, levelField: WukongSaveFixture.FieldVarint(4, 22)));
        byte[] before = await File.ReadAllBytesAsync(path);
        reader.Configure(new BlackMythWukongSaveConfiguration(path));

        RuntimeGameReadResult result = (await reader.ReadAsync(default))!;
        Assert.Equal(RuntimeGameReaderStatus.Synced, result.Status);
        Assert.Equal(GameId.BlackMythWukong, result.Observation!.GameId);
        Assert.Equal(0, result.Observation.TotalDeaths.Value);
        Assert.Equal(22, result.BlackMythWukongSaveMetadata!.Level);
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

        public static byte[] Create(
            int? deaths,
            int buildRevision = 23831,
            byte[]? levelField = null,
            byte[]? playTimeField = null,
            byte[]? timestampField = null)
        {
            byte[] deathData = deaths is int value ? FieldVarint(1, checked((ulong)value)) : [];
            byte[] persistentBgc = FieldBytes(5, deathData);
            byte[] persistentEcs = FieldBytes(1, persistentBgc);
            var archiveDataFields = new List<byte[]>();
            if (levelField is not null)
            {
                archiveDataFields.Add(FieldBytes(1, FieldBytes(1, levelField)));
            }
            if (playTimeField is not null)
            {
                archiveDataFields.Add(FieldBytes(2, FieldBytes(1, FieldBytes(1, playTimeField))));
            }
            byte[] archiveData = Concat(archiveDataFields.ToArray());
            byte[] decodedPayload = archiveData.Length == 0
                ? FieldBytes(6, persistentEcs)
                : Concat(FieldBytes(1, archiveData), FieldBytes(6, persistentEcs));
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
            return timestampField is null
                ? Concat(FieldBytes(1, metadata), FieldBytes(2, encryptedPayload))
                : Concat(FieldBytes(1, metadata), FieldBytes(2, encryptedPayload), timestampField);
        }

        public static byte[] FieldBytes(ulong field, ReadOnlySpan<byte> value)
        {
            var result = new List<byte>();
            WriteVarint(result, field << 3 | 2);
            WriteVarint(result, checked((ulong)value.Length));
            result.AddRange(value.ToArray());
            return result.ToArray();
        }

        public static byte[] FieldVarint(ulong field, ulong value)
        {
            var result = new List<byte>();
            WriteVarint(result, field << 3);
            WriteVarint(result, value);
            return result.ToArray();
        }

        public static byte[] FieldFixed32(ulong field, uint value)
        {
            byte[] result = new byte[5];
            result[0] = checked((byte)(field << 3 | 5));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1), value);
            return result;
        }

        public static byte[] Concat(params byte[][] values) => values.SelectMany(static value => value).ToArray();

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
