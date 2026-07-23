using System.Buffers.Binary;
using System.Text;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class EldenRingSaveProfileReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTrackerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParserReadsValidatedNameAndLevelFromCurrentLayout()
    {
        byte[] fixture = EldenRingProfileFixture.Create(
            new SlotFixture(0, Active: true, "Kairo", 125),
            new SlotFixture(1, Active: false, null, null));

        Assert.True(EldenRingSaveProfileParser.TryReadSlots(fixture, out IReadOnlyList<EldenRingCharacterSlotMetadata>? slots));
        Assert.Equal("Kairo", slots[0].Name);
        Assert.Equal(125, slots[0].Level);
        Assert.False(slots[0].IsEmpty);
        Assert.True(slots[1].IsEmpty);
        Assert.Null(slots[1].Name);
        Assert.Null(slots[1].Level);
    }

    [Fact]
    public void ParserSafelyAllowsDuplicateNamesWithoutChangingSlotIdentity()
    {
        byte[] fixture = EldenRingProfileFixture.Create(
            new SlotFixture(0, Active: true, "Tarnished", 50),
            new SlotFixture(1, Active: true, "Tarnished", 100));

        Assert.True(EldenRingSaveProfileParser.TryReadSlots(fixture, out IReadOnlyList<EldenRingCharacterSlotMetadata>? slots));
        Assert.Equal("Tarnished", slots[0].Name);
        Assert.Equal("Tarnished", slots[1].Name);
        Assert.Equal(0, slots[0].Index);
        Assert.Equal(1, slots[1].Index);
        Assert.Equal(50, slots[0].Level);
        Assert.Equal(100, slots[1].Level);
    }

    [Fact]
    public void ParserReturnsOnlyIndividuallyValidatedMetadata()
    {
        byte[] fixture = EldenRingProfileFixture.Create(
            new SlotFixture(0, Active: true, "Valid", 0),
            new SlotFixture(1, Active: true, "\u0001", 25),
            new SlotFixture(2, Active: true, null, 714),
            new SlotFixture(3, Active: true, null, null));

        Assert.True(EldenRingSaveProfileParser.TryReadSlots(fixture, out IReadOnlyList<EldenRingCharacterSlotMetadata>? slots));
        Assert.Equal("Valid", slots[0].Name);
        Assert.Null(slots[0].Level);
        Assert.Null(slots[1].Name);
        Assert.Equal(25, slots[1].Level);
        Assert.Null(slots[2].Name);
        Assert.Null(slots[2].Level);
        Assert.Null(slots[3].Name);
        Assert.Null(slots[3].Level);
    }

    [Fact]
    public void ParserFailsClosedForMissingOrMalformedProfileSummary()
    {
        Assert.False(EldenRingSaveProfileParser.TryReadSlots([1, 2, 3], out _));

        byte[] malformed = EldenRingProfileFixture.Create(new SlotFixture(0, Active: true, "Valid", 20));
        BinaryPrimitives.WriteUInt64LittleEndian(malformed.AsSpan(0x40 + 10 * 0x20 + 0x08), 0x100);
        Assert.False(EldenRingSaveProfileParser.TryReadSlots(malformed, out _));
    }

    [Fact]
    public async Task ReaderReadsSharedFixtureWithoutWritingIt()
    {
        string path = WriteFixture(EldenRingProfileFixture.Create(new SlotFixture(0, Active: true, "Seluvis", 80)));
        byte[] before = await File.ReadAllBytesAsync(path);
        var reader = new EldenRingSaveProfileReader();

        IReadOnlyList<EldenRingCharacterSlotMetadata> slots = await reader.ReadAsync(new EldenRingSaveConfiguration(path, 0), default);

        Assert.Equal("Seluvis", slots[0].Name);
        Assert.Equal(80, slots[0].Level);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private string WriteFixture(byte[] contents)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "ER0000.sl2");
        File.WriteAllBytes(path, contents);
        return path;
    }

    private sealed record SlotFixture(int Index, bool Active, string? Name, uint? Level);

    /// <summary>Synthetic profile-summary fixture only; it contains no user save data.</summary>
    private static class EldenRingProfileFixture
    {
        private const int HeaderSize = 0x40;
        private const int EntryHeaderSize = 0x20;
        private const int ProfileEntryIndex = 10;
        private const int EntryDataOffset = 0x1000;
        private const int EntrySize = 0x5000;
        private const int ProfileSummaryOffset = 0x1964;
        private const int ProfileEntrySize = 0x24C;

        public static byte[] Create(params SlotFixture[] profiles)
        {
            byte[] file = new byte[EntryDataOffset + EntrySize];
            "BND4"u8.CopyTo(file);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), 11);
            int entryOffset = HeaderSize + ProfileEntryIndex * EntryHeaderSize;
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(entryOffset + 0x08), EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entryOffset + 0x10), EntryDataOffset);

            foreach (SlotFixture profile in profiles)
            {
                int activeOffset = EntryDataOffset + ProfileSummaryOffset + profile.Index;
                file[activeOffset] = profile.Active ? (byte)1 : (byte)0;
                if (!profile.Active) continue;

                int profileOffset = EntryDataOffset + ProfileSummaryOffset + 10 + profile.Index * ProfileEntrySize;
                if (profile.Name is not null)
                {
                    byte[] nameBytes = Encoding.Unicode.GetBytes(profile.Name);
                    nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 32)).CopyTo(file.AsSpan(profileOffset, 32));
                }
                if (profile.Level is not null)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(profileOffset + 0x22), profile.Level.Value);
                }
            }

            return file;
        }
    }
}
