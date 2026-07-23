using System.Buffers.Binary;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>
/// Reads a user-selected ER0000.sl2 file only. It never opens, queries, or changes
/// the Elden Ring process, and never writes to the save.
/// </summary>
public sealed class EldenRingSaveDeathReader : IRuntimeGameDeathReader
{
    private const int RetryCount = 3;
    private const int RetryDelayMilliseconds = 250;
    private EldenRingSaveConfiguration configuration = EldenRingSaveConfiguration.Default;
    private SaveFingerprint? lastFingerprint;
    private RuntimeGameReadResult? lastResult;

    public GameId GameId => GameId.EldenRing;

    /// <summary>Updates only the private local selection used by the next poll.</summary>
    public void Configure(EldenRingSaveConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (configuration != value)
        {
            configuration = value;
            lastFingerprint = null;
            lastResult = null;
        }
    }

    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.LocalPath is null)
        {
            return RuntimeGameReadResult.WaitingForSaveFile(GameId);
        }

        SaveFingerprint fingerprint;
        try
        {
            fingerprint = SaveFingerprint.From(configuration.LocalPath);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }

        if (lastFingerprint == fingerprint && lastResult is not null)
        {
            return lastResult;
        }

        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] bytes = await ReadSharedReadOnlyAsync(configuration.LocalPath, cancellationToken).ConfigureAwait(false);
                SaveFingerprint afterRead = SaveFingerprint.From(configuration.LocalPath);
                if (afterRead != fingerprint)
                {
                    fingerprint = afterRead;
                    await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                EldenRingSaveParseOutcome outcome = EldenRingSaveParser.TryReadTotalDeaths(bytes, configuration.SlotIndex, out long totalDeaths);
                RuntimeGameReadResult? result = outcome switch
                {
                    EldenRingSaveParseOutcome.Success => RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, totalDeaths, DateTimeOffset.UtcNow)),
                    EldenRingSaveParseOutcome.EmptySlot => RuntimeGameReadResult.WaitingForActiveCharacter(GameId),
                    _ => null,
                };
                lastFingerprint = fingerprint;
                lastResult = result;
                return result;
            }
            catch (IOException)
            {
                if (attempt + 1 < RetryCount)
                {
                    await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        return null;
    }

    private static async Task<byte[]> ReadSharedReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        if (stream.Length is <= 0 or > EldenRingSaveParser.MaximumSupportedFileBytes)
        {
            throw new IOException("Selected save file is outside the supported size range.");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("Selected save file changed while it was being read.");
            offset += read;
        }

        return bytes;
    }

    private readonly record struct SaveFingerprint(long Length, DateTime LastWriteUtc)
    {
        public static SaveFingerprint From(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new IOException("Selected save file is unavailable.");
            return new(info.Length, info.LastWriteTimeUtc);
        }
    }
}

internal enum EldenRingSaveParseOutcome
{
    Invalid,
    EmptySlot,
    Success,
}

/// <summary>
/// Bounded parser for the documented PC ER0000.sl2 BND4 layout. The implementation
/// is original; format references and MIT notices are recorded in THIRD_PARTY_NOTICES.md.
/// </summary>
internal static class EldenRingSaveParser
{
    internal const int MaximumSupportedFileBytes = 64 * 1024 * 1024;
    private const int HeaderSize = 0x40;
    private const int EntryHeaderSize = 0x20;
    private const int SlotChecksumSize = 0x10;
    private const int Slots = 10;
    private const int MaxEntries = 64;
    private const int MaxVariableEntries = 10_000;
    private const int MaxVariableSectionSize = 0x10000;

    public static EldenRingSaveParseOutcome TryReadTotalDeaths(ReadOnlySpan<byte> file, int slotIndex, out long totalDeaths)
    {
        totalDeaths = 0;
        if (slotIndex is < 0 or >= Slots || file.Length is < HeaderSize or > MaximumSupportedFileBytes ||
            !file[..4].SequenceEqual("BND4"u8))
        {
            return EldenRingSaveParseOutcome.Invalid;
        }

        if (!TryReadUInt32(file, 0x0C, out uint entryCount) || entryCount is < Slots or > MaxEntries)
        {
            return EldenRingSaveParseOutcome.Invalid;
        }

        long entryOffset = HeaderSize + (long)slotIndex * EntryHeaderSize;
        if (!TryReadUInt64(file, entryOffset + 0x08, out ulong entrySize) ||
            !TryReadUInt32(file, entryOffset + 0x10, out uint entryDataOffset) ||
            entrySize > int.MaxValue || entrySize < SlotChecksumSize + 32)
        {
            return EldenRingSaveParseOutcome.Invalid;
        }

        long start = (long)entryDataOffset + SlotChecksumSize;
        long end = (long)entryDataOffset + (long)entrySize;
        if (start < 0 || end > file.Length || start >= end)
        {
            return EldenRingSaveParseOutcome.Invalid;
        }

        ReadOnlySpan<byte> slot = file[(int)start..(int)end];
        if (!TryReadUInt32(slot, 0, out uint version)) return EldenRingSaveParseOutcome.Invalid;
        if (version == 0) return EldenRingSaveParseOutcome.EmptySlot;

        int position = 32;
        int gaitemCount = version > 81 ? 0x1400 : 0x13FE;
        for (int index = 0; index < gaitemCount; index++)
        {
            if (!TryReadUInt32(slot, position, out uint handle) || !TryAdvance(ref position, 8, slot.Length)) return EldenRingSaveParseOutcome.Invalid;
            uint handleType = handle & 0xF0000000;
            if (handle != 0 && handleType != 0xC0000000)
            {
                if (!TryAdvance(ref position, 8, slot.Length)) return EldenRingSaveParseOutcome.Invalid;
                if (handleType == 0x80000000 && !TryAdvance(ref position, 5, slot.Length)) return EldenRingSaveParseOutcome.Invalid;
            }
        }

        if (!TryAdvance(ref position, 0x1B0 + 13 * 16 + 88 + 28 + 88, slot.Length) ||
            !TryAdvance(ref position, 4 + 0xA80 * 12 + 4 + 0x180 * 12 + 4 + 4, slot.Length) ||
            !TryAdvance(ref position, 14 * 8 + 4 + 10 * 8 + 4 + 6 * 8 + 4 + 4 + 6 * 4, slot.Length)) return EldenRingSaveParseOutcome.Invalid;

        if (!TryReadUInt32(slot, position, out uint projectileCount) || projectileCount > MaxVariableEntries || !TryAdvance(ref position, 4 + checked((int)projectileCount * 8), slot.Length)) return EldenRingSaveParseOutcome.Invalid;
        if (!TryAdvance(ref position, 39 * 4 + 3 * 4 + 0x12F, slot.Length) ||
            !TryAdvance(ref position, 4 + 0x780 * 12 + 4 + 0x80 * 12 + 4 + 4 + 64 * 4, slot.Length)) return EldenRingSaveParseOutcome.Invalid;

        if (!TryReadUInt32(slot, position, out uint regionCount) || regionCount > MaxVariableEntries || !TryAdvance(ref position, 4 + checked((int)regionCount * 4), slot.Length)) return EldenRingSaveParseOutcome.Invalid;
        if (!TryAdvance(ref position, 40 + 1 + 0x44 + 8, slot.Length) || !TryVariableSection(ref position, slot)) return EldenRingSaveParseOutcome.Invalid;
        if (!TryAdvance(ref position, 0x34 + 8 + 7000 * 16, slot.Length) || !TryVariableSection(ref position, slot)) return EldenRingSaveParseOutcome.Invalid;
        if (!TryAdvance(ref position, 3, slot.Length) || !TryReadUInt32(slot, position, out uint value)) return EldenRingSaveParseOutcome.Invalid;

        totalDeaths = value;
        return EldenRingSaveParseOutcome.Success;
    }

    private static bool TryVariableSection(ref int position, ReadOnlySpan<byte> data)
    {
        if (!TryReadUInt32(data, position + 4L, out uint sectionSize) || sectionSize > MaxVariableSectionSize) return false;
        return TryAdvance(ref position, 8 + checked((int)sectionSize), data.Length);
    }

    private static bool TryAdvance(ref int position, int amount, int length)
    {
        if (amount < 0 || position > length - amount) return false;
        position += amount;
        return true;
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
