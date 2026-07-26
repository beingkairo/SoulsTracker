using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>
/// Reads one user-selected Black Myth: Wukong archive only. It never opens the
/// game process, writes to the archive, or sends archive data anywhere.
/// </summary>
public sealed class BlackMythWukongSaveDeathReader : IRuntimeGameDeathReader
{
    private const int RetryCount = 3;
    private const int RetryDelayMilliseconds = 250;
    private BlackMythWukongSaveConfiguration configuration = BlackMythWukongSaveConfiguration.Default;
    private SaveFingerprint? lastFingerprint;
    private RuntimeGameReadResult? lastResult;

    public GameId GameId => GameId.BlackMythWukong;

    /// <summary>Updates only the private local selection used by the next poll.</summary>
    public void Configure(BlackMythWukongSaveConfiguration value)
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

        try
        {
            EnsureReadable(configuration.LocalPath);
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

                BlackMythWukongSaveParseOutcome outcome = BlackMythWukongSaveParser.TryRead(
                    bytes,
                    out long totalDeaths,
                    out BlackMythWukongSaveMetadata? saveMetadata);
                RuntimeGameReadResult? result = outcome == BlackMythWukongSaveParseOutcome.Success
                    ? RuntimeGameReadResult.Synced(
                        new RuntimeGameObservation(GameId, totalDeaths, DateTimeOffset.UtcNow),
                        saveMetadata)
                    : null;
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
        if (stream.Length is <= 0 or > BlackMythWukongSaveParser.MaximumSupportedFileBytes)
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

    private static void EnsureReadable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

internal enum BlackMythWukongSaveParseOutcome
{
    Invalid,
    Unsupported,
    Success,
}

/// <summary>Bounded BCL-only parser for the currently supported Wukong archive wire format.</summary>
internal static class BlackMythWukongSaveParser
{
    internal const int MaximumSupportedFileBytes = 8 * 1024 * 1024;
    private const ulong CurrentProtocolTag = 14;
    private const ulong CurrentBuildRevision = 23831;
    private static ReadOnlySpan<byte> XorKey => [0x7B, 0x5C, 0xDA, 0x91, 0x3E, 0xFC, 0xDA, 0x37];
    private static ReadOnlySpan<byte> ChecksumSalt => "lhx2tkh6lj1wj8jmrgs3k1xb2brusehx"u8;

    public static BlackMythWukongSaveParseOutcome TryReadTotalDeaths(ReadOnlySpan<byte> file, out long totalDeaths)
        => TryRead(file, out totalDeaths, out _);

    public static BlackMythWukongSaveParseOutcome TryRead(
        ReadOnlySpan<byte> file,
        out long totalDeaths,
        out BlackMythWukongSaveMetadata? saveMetadata)
    {
        totalDeaths = 0;
        saveMetadata = null;
        if (file.IsEmpty || file.Length > MaximumSupportedFileBytes ||
            !TryReadRequiredLengthDelimitedField(file, 1, out ReadOnlySpan<byte> metadata) ||
            !TryReadRequiredLengthDelimitedField(file, 2, out ReadOnlySpan<byte> encryptedPayload))
        {
            return BlackMythWukongSaveParseOutcome.Invalid;
        }

        if (!TryReadArchiveMetadata(metadata, out ReadOnlySpan<byte> checksum))
        {
            return BlackMythWukongSaveParseOutcome.Unsupported;
        }

        if (!VerifyChecksum(checksum, encryptedPayload))
        {
            return BlackMythWukongSaveParseOutcome.Invalid;
        }

        byte[] payload = encryptedPayload.ToArray();
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] ^= XorKey[index % XorKey.Length];
        }

        if (!TryReadRequiredLengthDelimitedField(payload, 6, out ReadOnlySpan<byte> persistentEcs) ||
            !TryReadRequiredLengthDelimitedField(persistentEcs, 1, out ReadOnlySpan<byte> persistentBgc) ||
            !TryReadRequiredLengthDelimitedField(persistentBgc, 5, out ReadOnlySpan<byte> playerDeathData) ||
            !TryReadOptionalVarintField(playerDeathData, 1, out bool hasDeathCount, out ulong rawDeathCount))
        {
            return BlackMythWukongSaveParseOutcome.Invalid;
        }

        if (hasDeathCount && rawDeathCount > int.MaxValue)
        {
            return BlackMythWukongSaveParseOutcome.Invalid;
        }

        totalDeaths = hasDeathCount ? (long)rawDeathCount : 0;
        saveMetadata = ReadOptionalSaveMetadata(file, payload);
        return BlackMythWukongSaveParseOutcome.Success;
    }

    private static BlackMythWukongSaveMetadata? ReadOptionalSaveMetadata(
        ReadOnlySpan<byte> archive,
        ReadOnlySpan<byte> payload)
    {
        int? level = TryReadLevel(payload);
        TimeSpan? playTime = TryReadPlayTime(payload);
        DateTimeOffset? lastSaved = TryReadLastSaved(archive);
        return level is null && playTime is null && lastSaved is null
            ? null
            : new BlackMythWukongSaveMetadata(level, playTime, lastSaved);
    }

    private static int? TryReadLevel(ReadOnlySpan<byte> payload)
    {
        if (!TryReadRequiredLengthDelimitedField(payload, 1, out ReadOnlySpan<byte> archiveData) ||
            !TryReadRequiredLengthDelimitedField(archiveData, 1, out ReadOnlySpan<byte> roleData) ||
            !TryReadRequiredLengthDelimitedField(roleData, 1, out ReadOnlySpan<byte> roleBase) ||
            !TryReadOptionalVarintField(roleBase, 4, out bool found, out ulong value) ||
            !found ||
            value is 0 or > int.MaxValue)
        {
            return null;
        }

        return checked((int)value);
    }

    private static TimeSpan? TryReadPlayTime(ReadOnlySpan<byte> payload)
    {
        if (!TryReadRequiredLengthDelimitedField(payload, 1, out ReadOnlySpan<byte> archiveData) ||
            !TryReadRequiredLengthDelimitedField(archiveData, 2, out ReadOnlySpan<byte> playData) ||
            !TryReadRequiredLengthDelimitedField(playData, 1, out ReadOnlySpan<byte> playDataState) ||
            !TryReadRequiredLengthDelimitedField(playDataState, 1, out ReadOnlySpan<byte> playDataValue) ||
            !TryReadOptionalFixed32Field(playDataValue, 2, out bool found, out uint rawSeconds) ||
            !found)
        {
            return null;
        }

        float seconds = BitConverter.Int32BitsToSingle(unchecked((int)rawSeconds));
        if (!float.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadLastSaved(ReadOnlySpan<byte> archive)
    {
        if (!TryReadOptionalVarintField(archive, 5, out bool found, out ulong unixSeconds) ||
            !found ||
            unixSeconds > 253_402_300_799UL)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(checked((long)unixSeconds));
    }

    private static bool TryReadArchiveMetadata(ReadOnlySpan<byte> metadata, out ReadOnlySpan<byte> checksum)
    {
        checksum = default;
        if (!TryReadRequiredLengthDelimitedField(metadata, 1, out checksum) || checksum.Length != 32 ||
            !IsLowerHex(checksum) ||
            !TryReadRequiredVarintField(metadata, 7, out ulong protocolTag) || protocolTag != CurrentProtocolTag ||
            !TryReadRequiredVarintField(metadata, 8, out ulong encryptEnabled) || encryptEnabled != 1 ||
            !TryReadRequiredVarintField(metadata, 10, out ulong createBuildRevision) || createBuildRevision != CurrentBuildRevision ||
            !TryReadRequiredVarintField(metadata, 11, out ulong saveBuildRevision) || saveBuildRevision != CurrentBuildRevision)
        {
            checksum = default;
            return false;
        }

        return true;
    }

    private static bool VerifyChecksum(ReadOnlySpan<byte> checksum, ReadOnlySpan<byte> encryptedPayload)
    {
        byte[] input = GC.AllocateUninitializedArray<byte>(checked(encryptedPayload.Length + ChecksumSalt.Length));
        encryptedPayload.CopyTo(input);
        ChecksumSalt.CopyTo(input.AsSpan(encryptedPayload.Length));
#pragma warning disable CA5351 // Compatibility verification of the game's existing archive checksum.
        Span<byte> actual = stackalloc byte[16];
        MD5.HashData(input, actual);
#pragma warning restore CA5351
        Span<byte> expected = stackalloc byte[16];
        for (int index = 0; index < expected.Length; index++)
        {
            int high = HexValue(checksum[index * 2]);
            int low = HexValue(checksum[index * 2 + 1]);
            if (high < 0 || low < 0) return false;
            expected[index] = (byte)((high << 4) | low);
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsLowerHex(ReadOnlySpan<byte> value)
    {
        foreach (byte character in value)
        {
            if ((character < (byte)'0' || character > (byte)'9') &&
                (character < (byte)'a' || character > (byte)'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static int HexValue(byte value) => value is >= (byte)'0' and <= (byte)'9'
        ? value - (byte)'0'
        : value is >= (byte)'a' and <= (byte)'f'
            ? value - (byte)'a' + 10
            : -1;

    private static bool TryReadRequiredLengthDelimitedField(ReadOnlySpan<byte> message, ulong requestedField, out ReadOnlySpan<byte> value)
    {
        value = default;
        bool found = false;
        int offset = 0;
        while (offset < message.Length)
        {
            if (!TryReadField(message, ref offset, out ulong field, out ulong wire, out int valueOffset, out int valueLength, out ulong number)) return false;
            if (field != requestedField) continue;
            if (wire != 2 || found) return false;
            value = message.Slice(valueOffset, valueLength);
            found = true;
        }

        return found;
    }

    private static bool TryReadRequiredVarintField(ReadOnlySpan<byte> message, ulong requestedField, out ulong value)
    {
        value = 0;
        bool found = false;
        int offset = 0;
        while (offset < message.Length)
        {
            if (!TryReadField(message, ref offset, out ulong field, out ulong wire, out _, out _, out ulong number)) return false;
            if (field != requestedField) continue;
            if (wire != 0 || found) return false;
            value = number;
            found = true;
        }

        return found;
    }

    private static bool TryReadOptionalVarintField(ReadOnlySpan<byte> message, ulong requestedField, out bool found, out ulong value)
    {
        found = false;
        value = 0;
        int offset = 0;
        while (offset < message.Length)
        {
            if (!TryReadField(message, ref offset, out ulong field, out ulong wire, out _, out _, out ulong number)) return false;
            if (field != requestedField) continue;
            if (wire != 0 || found) return false;
            value = number;
            found = true;
        }

        return true;
    }

    private static bool TryReadOptionalFixed32Field(ReadOnlySpan<byte> message, ulong requestedField, out bool found, out uint value)
    {
        found = false;
        value = 0;
        int offset = 0;
        while (offset < message.Length)
        {
            if (!TryReadField(message, ref offset, out ulong field, out ulong wire, out _, out _, out ulong number)) return false;
            if (field != requestedField) continue;
            if (wire != 5 || found || number > uint.MaxValue) return false;
            value = checked((uint)number);
            found = true;
        }

        return true;
    }

    private static bool TryReadField(ReadOnlySpan<byte> data, ref int offset, out ulong field, out ulong wire, out int valueOffset, out int valueLength, out ulong number)
    {
        field = 0;
        wire = 0;
        valueOffset = 0;
        valueLength = 0;
        number = 0;
        if (!TryReadVarint(data, ref offset, out ulong tag) || tag == 0) return false;
        field = tag >> 3;
        wire = tag & 0x07;
        if (field == 0) return false;

        switch (wire)
        {
            case 0:
                return TryReadVarint(data, ref offset, out number);
            case 1:
                return TrySkip(data, ref offset, sizeof(ulong));
            case 2:
                if (!TryReadVarint(data, ref offset, out ulong length) || length > (ulong)(data.Length - offset)) return false;
                valueOffset = offset;
                valueLength = checked((int)length);
                offset += valueLength;
                return true;
            case 5:
                if (offset > data.Length - sizeof(uint)) return false;
                number = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
                return TrySkip(data, ref offset, sizeof(uint));
            default:
                return false;
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        for (int index = 0; index < 10 && offset < data.Length; index++)
        {
            byte next = data[offset++];
            if (index == 9 && next > 1) return false;
            value |= (ulong)(next & 0x7F) << (index * 7);
            if ((next & 0x80) == 0) return true;
        }

        return false;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        if (length < 0 || offset > data.Length - length) return false;
        offset += length;
        return true;
    }
}
