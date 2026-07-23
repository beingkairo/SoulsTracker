using System.Buffers.Binary;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class EldenRingSaveDeathReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTrackerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParserReadsOnlyTheRequestedSlotFromCurrentV252Fixture()
    {
        byte[] fixture = EldenRingSaveFixture.Create((0, 37), (1, 104));

        Assert.Equal(EldenRingSaveParseOutcome.Success, EldenRingSaveParser.TryReadTotalDeaths(fixture, 0, out long first));
        Assert.Equal(EldenRingSaveParseOutcome.Success, EldenRingSaveParser.TryReadTotalDeaths(fixture, 1, out long second));

        Assert.Equal(37, first);
        Assert.Equal(104, second);
    }

    [Fact]
    public void ParserFailsClosedForMalformedOrEmptyFixtureSlots()
    {
        Assert.Equal(EldenRingSaveParseOutcome.Invalid, EldenRingSaveParser.TryReadTotalDeaths([1, 2, 3], 0, out _));

        byte[] empty = EldenRingSaveFixture.Create((0, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(EldenRingSaveFixture.FirstSlotDataOffset + 0x10), 0);
        Assert.Equal(EldenRingSaveParseOutcome.EmptySlot, EldenRingSaveParser.TryReadTotalDeaths(empty, 0, out _));
    }

    [Fact]
    public async Task ReaderWaitsForSelectionReadsSharedFixtureAndDoesNotWriteIt()
    {
        var reader = new EldenRingSaveDeathReader();
        Assert.Equal(RuntimeGameReaderStatus.WaitingForSaveFile, (await reader.ReadAsync(default))!.Status);

        string path = WriteFixture("ER0000.sl2", EldenRingSaveFixture.Create((0, 22)));
        byte[] before = await File.ReadAllBytesAsync(path);
        reader.Configure(new EldenRingSaveConfiguration(path, 0));

        RuntimeGameReadResult result = (await reader.ReadAsync(default))!;
        byte[] after = await File.ReadAllBytesAsync(path);

        Assert.Equal(RuntimeGameReaderStatus.Synced, result.Status);
        Assert.Equal(GameId.EldenRing, result.Observation!.GameId);
        Assert.Equal(22, result.Observation.TotalDeaths.Value);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ReaderDetectsAChangedSaveAndKeepsEldenStateIndependent()
    {
        string path = WriteFixture("ER0000.sl2", EldenRingSaveFixture.Create((0, 5), (1, 88)));
        var reader = new EldenRingSaveDeathReader();
        reader.Configure(new EldenRingSaveConfiguration(path, 1));
        Assert.Equal(88, (await reader.ReadAsync(default))!.Observation!.TotalDeaths.Value);

        await File.WriteAllBytesAsync(path, EldenRingSaveFixture.Create((0, 6), (1, 89)));
        RuntimeGameReadResult changed = (await reader.ReadAsync(default))!;

        Assert.Equal(89, changed.Observation!.TotalDeaths.Value);
        Assert.Equal(GameId.EldenRing, changed.Observation.GameId);
    }

    [Fact]
    public async Task ReaderFailsClosedForMissingMalformedAndLockedFixtureFiles()
    {
        var reader = new EldenRingSaveDeathReader();
        reader.Configure(new EldenRingSaveConfiguration(Path.Combine(root, "ER0000.sl2"), 0));
        Assert.Null(await reader.ReadAsync(default));

        string malformed = WriteFixture("ER0000.sl2", [0, 1, 2, 3]);
        reader.Configure(new EldenRingSaveConfiguration(malformed, 0));
        Assert.Null(await reader.ReadAsync(default));

        await File.WriteAllBytesAsync(malformed, EldenRingSaveFixture.Create((0, 2)));
        using var lockStream = new FileStream(malformed, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(await reader.ReadAsync(default));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private string WriteFixture(string fileName, byte[] contents)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, contents);
        return path;
    }

    /// <summary>Creates synthetic test fixtures only; it never includes a user save.</summary>
    private static class EldenRingSaveFixture
    {
        internal const int FirstSlotDataOffset = 0x1000;
        private const int HeaderSize = 0x40;
        private const int EntrySize = 0x20;
        private const int SlotDataLength = 400_000;

        public static byte[] Create(params (int Slot, uint Deaths)[] values)
        {
            int fileLength = FirstSlotDataOffset + SlotDataLength * 2 + 0x10;
            byte[] file = new byte[fileLength];
            "BND4"u8.CopyTo(file);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), 10);
            foreach ((int slot, uint deaths) in values)
            {
                int dataOffset = FirstSlotDataOffset + slot * SlotDataLength;
                int entryOffset = HeaderSize + slot * EntrySize;
                BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(entryOffset + 0x08), (ulong)(SlotDataLength + 0x10));
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entryOffset + 0x10), (uint)dataOffset);
                Span<byte> slotData = file.AsSpan(dataOffset + 0x10, SlotDataLength);
                // Version 252 is the current observed PC layout. The fixture contains
                // no user data and verifies the fixed sections before Total Deaths.
                BinaryPrimitives.WriteUInt32LittleEndian(slotData, 252);
                int deathOffset = CalculateDeathOffset(slotData);
                BinaryPrimitives.WriteUInt32LittleEndian(slotData[deathOffset..], deaths);
            }
            return file;
        }

        private static int CalculateDeathOffset(Span<byte> slot)
        {
            int position = 32;
            position += 0x1400 * 8;
            position += 0x1B0 + 13 * 16 + 88 + 28 + 88 + 88;
            position += 4 + 0xA80 * 12 + 4 + 0x180 * 12 + 4 + 4;
            position += 14 * 8 + 4 + 10 * 8 + 4 + 6 * 8 + 4 + 4 + 6 * 4;
            BinaryPrimitives.WriteUInt32LittleEndian(slot[position..], 0); position += 4;
            position += 39 * 4 + 3 * 4 + 0x12F;
            position += 4 + 0x780 * 12 + 4 + 0x80 * 12 + 4 + 4 + 64 * 4;
            BinaryPrimitives.WriteUInt32LittleEndian(slot[position..], 0); position += 4;
            position += 40 + 1 + 0x44 + 8;
            BinaryPrimitives.WriteUInt32LittleEndian(slot[(position + 4)..], 0); position += 8;
            position += 0x34 + 8 + 7000 * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(slot[(position + 4)..], 0); position += 8;
            position += 3;
            return position;
        }
    }
}
