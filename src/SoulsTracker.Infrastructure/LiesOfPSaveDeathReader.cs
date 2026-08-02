using System.Buffers.Binary;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>
/// Reads the selected Lies of P Steam character save without opening the game
/// process or modifying either member of the game's paired save files.
/// </summary>
public sealed class LiesOfPSaveDeathReader : IRuntimeGameDeathReader
{
    private const int RetryCount = 3;
    private const int RetryDelayMilliseconds = 250;
    private LiesOfPSaveConfiguration configuration = LiesOfPSaveConfiguration.Default;

    public GameId GameId => GameId.LiesOfP;

    public void Configure(LiesOfPSaveConfiguration value) =>
        configuration = value ?? throw new ArgumentNullException(nameof(value));

    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.LocalPath is null) return RuntimeGameReadResult.WaitingForSaveFile(GameId);

        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                (string path, byte[] bytes)? newest = await TryReadNewestValidMemberAsync(configuration.LocalPath, cancellationToken).ConfigureAwait(false);
                if (newest is { } save)
                {
                    if (LiesOfPSaveParser.TryReadTotalDeaths(save.bytes, out long totalDeaths) == LiesOfPSaveParseOutcome.Success)
                    {
                        return RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, totalDeaths, DateTimeOffset.UtcNow));
                    }

                    return null;
                }
            }
            catch (IOException) when (attempt + 1 < RetryCount)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        return null;
    }

    private static async Task<(string path, byte[] bytes)?> TryReadNewestValidMemberAsync(string selectedPath, CancellationToken cancellationToken)
    {
        var valid = new List<(string path, byte[] bytes, DateTime lastWriteUtc)>();
        foreach (string path in LiesOfPSaveMembers.ForSelectedPath(selectedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeRegularMember(path)) continue;
            FileInfo before = new(path);
            if (!before.Exists || before.Length is <= 0 or > LiesOfPSaveParser.MaximumSupportedFileBytes) continue;

            byte[] bytes = await ReadSharedReadOnlyAsync(path, checked((int)before.Length), cancellationToken).ConfigureAwait(false);
            FileInfo after = new(path);
            if (!after.Exists || after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc) throw new IOException("The save changed while it was being read.");
            if (LiesOfPSaveParser.TryReadTotalDeaths(bytes, out _) == LiesOfPSaveParseOutcome.Success)
            {
                valid.Add((path, bytes, after.LastWriteTimeUtc));
            }
        }

        return valid
            .OrderByDescending(static candidate => candidate.lastWriteUtc)
            .ThenBy(static candidate => candidate.path, StringComparer.OrdinalIgnoreCase)
            .Select(static candidate => ((string path, byte[] bytes)?)(candidate.path, candidate.bytes))
            .FirstOrDefault();
    }

    private static bool IsSafeRegularMember(string path)
    {
        try
        {
            string canonical = Path.GetFullPath(path);
            if (canonical.StartsWith("\\\\", StringComparison.Ordinal) || BlackMythWukongSaveDiscovery.HasReparsePointInPath(canonical)) return false;

            FileAttributes attributes = File.GetAttributes(canonical);
            return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    private static async Task<byte[]> ReadSharedReadOnlyAsync(string path, int length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        if (stream.Length != length) throw new IOException("The save changed before it could be read.");
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The save changed while it was being read.");
            offset += read;
        }
        return bytes;
    }
}

internal enum LiesOfPSaveParseOutcome { Invalid, Unsupported, Success }

/// <summary>
/// Bounded parser for the observed Unreal GVAS property layout. The marker is
/// optional for a real zero-death character; the stable companion property is
/// required so arbitrary GVAS data never becomes a false zero.
/// </summary>
internal static class LiesOfPSaveParser
{
    internal const int MaximumSupportedFileBytes = 8 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "GVAS"u8;
    private static ReadOnlySpan<byte> DeathName => "YouDieCount"u8;
    private static ReadOnlySpan<byte> AnchorName => "TotalReceiveDamage"u8;
    private static ReadOnlySpan<byte> IntProperty => "IntProperty"u8;

    public static LiesOfPSaveParseOutcome TryReadTotalDeaths(ReadOnlySpan<byte> file, out long totalDeaths)
    {
        totalDeaths = 0;
        if (file.Length is < 64 or > MaximumSupportedFileBytes || !file.StartsWith(Magic)) return LiesOfPSaveParseOutcome.Unsupported;

        int anchorCount = CountOccurrences(file, AnchorName);
        if (anchorCount != 1 || !TryReadObservedIntProperty(file, AnchorName, out _)) return LiesOfPSaveParseOutcome.Unsupported;

        int deathsCount = CountOccurrences(file, DeathName);
        if (deathsCount == 0) return LiesOfPSaveParseOutcome.Success;
        if (deathsCount != 1 || !TryReadObservedIntProperty(file, DeathName, out int value) || value < 0) return LiesOfPSaveParseOutcome.Invalid;

        totalDeaths = value;
        return LiesOfPSaveParseOutcome.Success;
    }

    private static bool TryReadObservedIntProperty(ReadOnlySpan<byte> file, ReadOnlySpan<byte> name, out int value)
    {
        value = 0;
        int nameOffset = FindOccurrence(file, name);
        if (nameOffset < sizeof(int) || nameOffset > file.Length - name.Length - 1) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(file.Slice(nameOffset - sizeof(int), sizeof(int))) != name.Length + 1) return false;
        if (file[nameOffset + name.Length] != 0) return false;

        int offset = nameOffset + name.Length + 1;
        if (offset > file.Length - sizeof(int) - IntProperty.Length - 1 - sizeof(int) - sizeof(int) - 1 - sizeof(int)) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(file.Slice(offset, sizeof(int))) != IntProperty.Length + 1) return false;
        offset += sizeof(int);
        if (!file.Slice(offset, IntProperty.Length).SequenceEqual(IntProperty) || file[offset + IntProperty.Length] != 0) return false;
        offset += IntProperty.Length + 1;
        if (BinaryPrimitives.ReadInt32LittleEndian(file.Slice(offset, sizeof(int))) != sizeof(int)) return false;
        offset += sizeof(int);
        if (BinaryPrimitives.ReadInt32LittleEndian(file.Slice(offset, sizeof(int))) != 0) return false;
        offset += sizeof(int);
        if (file[offset++] != 0) return false; // The observed no-property-GUID flag.
        value = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(offset, sizeof(int)));
        return true;
    }

    private static int CountOccurrences(ReadOnlySpan<byte> file, ReadOnlySpan<byte> needle)
    {
        int count = 0;
        int offset = 0;
        while (offset <= file.Length - needle.Length)
        {
            int found = file[offset..].IndexOf(needle);
            if (found < 0) break;
            count++;
            offset += found + needle.Length;
        }
        return count;
    }

    private static int FindOccurrence(ReadOnlySpan<byte> file, ReadOnlySpan<byte> needle)
    {
        int offset = file.IndexOf(needle);
        return offset;
    }
}

internal static class LiesOfPSaveMembers
{
    private static readonly System.Text.RegularExpressions.Regex FileName = new(
        @"^(?<prefix>SaveData-(?<character>[1-9][0-9]*)_Character_)[12](?<suffix>\.sav)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static IEnumerable<string> ForSelectedPath(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) yield break;
        string canonical;
        try { canonical = Path.GetFullPath(selectedPath); }
        catch { yield break; }
        System.Text.RegularExpressions.Match match = FileName.Match(Path.GetFileName(canonical));
        if (!match.Success) yield break;
        string directory = Path.GetDirectoryName(canonical) ?? string.Empty;
        yield return Path.Combine(directory, $"{match.Groups["prefix"].Value}1{match.Groups["suffix"].Value}");
        yield return Path.Combine(directory, $"{match.Groups["prefix"].Value}2{match.Groups["suffix"].Value}");
    }

    public static int CharacterNumber(string path)
    {
        System.Text.RegularExpressions.Match match = FileName.Match(Path.GetFileName(path));
        return match.Success && int.TryParse(match.Groups["character"].Value, out int character) ? character : int.MaxValue;
    }
}
