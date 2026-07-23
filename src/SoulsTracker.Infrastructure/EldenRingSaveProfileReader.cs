using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Reads only safe character-picker metadata from a selected Elden Ring save file.</summary>
public interface IEldenRingSaveProfileReader
{
    ValueTask<IReadOnlyList<EldenRingCharacterSlotMetadata>> ReadAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken);
}

/// <summary>
/// Local, read-only reader for the profile-summary entry inside ER0000.sl2. It never
/// opens the game process or writes to the selected save file.
/// </summary>
public sealed class EldenRingSaveProfileReader : IEldenRingSaveProfileReader
{
    private const int RetryCount = 3;
    private const int RetryDelayMilliseconds = 250;

    public async ValueTask<IReadOnlyList<EldenRingCharacterSlotMetadata>> ReadAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.LocalPath is null)
        {
            return EldenRingCharacterSlotMetadata.UnavailableSlots;
        }

        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] contents = await ReadSharedReadOnlyAsync(configuration.LocalPath, cancellationToken).ConfigureAwait(false);
                return EldenRingSaveProfileParser.TryReadSlots(contents, out IReadOnlyList<EldenRingCharacterSlotMetadata>? slots)
                    ? slots
                    : EldenRingCharacterSlotMetadata.UnavailableSlots;
            }
            catch (IOException) when (attempt + 1 < RetryCount)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) { return EldenRingCharacterSlotMetadata.UnavailableSlots; }
            catch (UnauthorizedAccessException) { return EldenRingCharacterSlotMetadata.UnavailableSlots; }
            catch (System.Security.SecurityException) { return EldenRingCharacterSlotMetadata.UnavailableSlots; }
        }

        return EldenRingCharacterSlotMetadata.UnavailableSlots;
    }

    private static async Task<byte[]> ReadSharedReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        if (stream.Length is <= 0 or > EldenRingSaveParser.MaximumSupportedFileBytes)
        {
            throw new IOException();
        }

        byte[] contents = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        int offset = 0;
        while (offset < contents.Length)
        {
            int read = await stream.ReadAsync(contents.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException();
            offset += read;
        }

        return contents;
    }
}

/// <summary>Validated, safe-to-display profile metadata for one Elden Ring character slot.</summary>
public sealed record EldenRingCharacterSlotMetadata(int Index, bool IsEmpty, string? Name, int? Level)
{
    public const int SlotCount = 10;

    public static IReadOnlyList<EldenRingCharacterSlotMetadata> UnavailableSlots { get; } = CreateUnavailableSlots();

    private static EldenRingCharacterSlotMetadata[] CreateUnavailableSlots() =>
        Enumerable.Range(0, SlotCount).Select(static index => new EldenRingCharacterSlotMetadata(index, IsEmpty: false, Name: null, Level: null)).ToArray();
}

/// <summary>
/// Bounded parser for USER_DATA_10's profile-summary layout. It is intentionally
/// separate from Total Deaths parsing so a metadata failure cannot alter tracking.
/// </summary>
internal static class EldenRingSaveProfileParser
{
    private const int HeaderSize = 0x40;
    private const int EntryHeaderSize = 0x20;
    private const int ProfileSummaryEntryIndex = 10;
    private const int ProfileSummaryOffset = 0x1964;
    private const int ProfileEntrySize = 0x24C;
    private const int ActiveProfilesSize = EldenRingCharacterSlotMetadata.SlotCount;
    private const int NameSize = 32;
    private const int LevelOffset = 0x22;
    private const int MinimumLevel = 1;
    private const int MaximumLevel = 713;
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);

    public static bool TryReadSlots(ReadOnlySpan<byte> file, out IReadOnlyList<EldenRingCharacterSlotMetadata> slots)
    {
        slots = EldenRingCharacterSlotMetadata.UnavailableSlots;
        if (file.Length < HeaderSize || !file[..4].SequenceEqual("BND4"u8) ||
            !TryReadUInt32(file, 0x0C, out uint entryCount) || entryCount <= ProfileSummaryEntryIndex || entryCount > 64)
        {
            return false;
        }

        long entryOffset = HeaderSize + (long)ProfileSummaryEntryIndex * EntryHeaderSize;
        if (!TryReadUInt64(file, entryOffset + 0x08, out ulong entrySize) ||
            !TryReadUInt32(file, entryOffset + 0x10, out uint entryDataOffset) ||
            entrySize > int.MaxValue)
        {
            return false;
        }

        long entryEnd = (long)entryDataOffset + (long)entrySize;
        long profileStart = (long)entryDataOffset + ProfileSummaryOffset;
        long profileEnd = profileStart + ActiveProfilesSize + (long)EldenRingCharacterSlotMetadata.SlotCount * ProfileEntrySize;
        if (entryEnd > file.Length || profileStart < entryDataOffset || profileEnd > entryEnd)
        {
            return false;
        }

        var parsed = new EldenRingCharacterSlotMetadata[EldenRingCharacterSlotMetadata.SlotCount];
        for (int index = 0; index < parsed.Length; index++)
        {
            int activeOffset = checked((int)profileStart + index);
            if (file[activeOffset] == 0)
            {
                parsed[index] = new EldenRingCharacterSlotMetadata(index, IsEmpty: true, Name: null, Level: null);
                continue;
            }

            int entryStart = checked((int)profileStart + ActiveProfilesSize + index * ProfileEntrySize);
            string? name = TryReadDisplayName(file.Slice(entryStart, NameSize));
            int? level = TryReadLevel(file, entryStart + LevelOffset);
            parsed[index] = new EldenRingCharacterSlotMetadata(index, IsEmpty: false, name, level);
        }

        slots = parsed;
        return true;
    }

    private static string? TryReadDisplayName(ReadOnlySpan<byte> bytes)
    {
        int length = NameSize;
        for (int offset = 0; offset < NameSize; offset += 2)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)) == 0)
            {
                length = offset;
                break;
            }
        }

        if (length == 0)
        {
            return null;
        }

        try
        {
            string value = StrictUtf16LittleEndian.GetString(bytes[..length]).Trim();
            if (string.IsNullOrWhiteSpace(value)) return null;
            foreach (Rune rune in value.EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                {
                    return null;
                }
            }

            return value;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static int? TryReadLevel(ReadOnlySpan<byte> file, int offset)
    {
        if (!TryReadUInt32(file, offset, out uint value) || value is < MinimumLevel or > MaximumLevel)
        {
            return null;
        }

        return checked((int)value);
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, long offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - sizeof(uint)) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)offset, sizeof(uint)));
        return true;
    }

    private static bool TryReadUInt64(ReadOnlySpan<byte> data, long offset, out ulong value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - sizeof(ulong)) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice((int)offset, sizeof(ulong)));
        return true;
    }
}
