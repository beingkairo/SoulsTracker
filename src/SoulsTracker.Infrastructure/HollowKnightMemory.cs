using System.Buffers.Binary;
using System.Text;

namespace SoulsTracker.Infrastructure;

/// <summary>Bounded read-only discovery of the original game's x64 Mono GameManager singleton.</summary>
internal static class HollowKnightMemory
{
    // JIT instruction shapes and layout facts verified against the pinned autosplitter reference.
    private static readonly byte?[][] Signatures =
    [
        [0x41,0xff,0xd3,0xe9,0x63,0,0,0,0x48,0xb8,null,null,null,null,null,null,null,null,0x48,0x8b,0x10,0x48,0x8b,0xce,0x48,0x8d,0x64,0x24,0,0x90,0x49,0xbb],
        [0x48,0x8b,0xce,0x49,0xbb,null,null,null,null,null,null,null,null,0x41,0xff,0xd3,0xe9,null,0,0,0,0x48,0x8b,0x14,0x25,null,null,null,null]
    ];

    internal static bool IsOriginalProcessName(string name) =>
        name.Equals("hollow_knight", StringComparison.OrdinalIgnoreCase) || name.Equals("Hollow Knight", StringComparison.OrdinalIgnoreCase);

    internal static async ValueTask<nuint> FindRootAsync(IReadOnlyProcessAttachment memory, CancellationToken token)
    {
        if (memory is not IReadOnlyProcessMemoryMap map) return 0;
        var roots = new HashSet<nuint>();
        nuint cursor = 0;
        int scanned = 0;
        bool completed = false;
        for (int regions = 0; regions < 8192; regions++)
        {
            ReadOnlyProcessMemoryRegion? query = await map.QueryRegionAsync(cursor, token).ConfigureAwait(false);
            if (query is not { } region) { completed = true; break; }
            if (region.Size == 0 || region.Start + region.Size <= cursor) return 0;
            cursor = checked(region.Start + region.Size);
            if (!region.IsPrivateExecutable) continue;
            // At most 128 MiB of executable JIT memory per discovery attempt, in bounded chunks.
            for (nuint offset = 0; offset < region.Size;)
            {
                int count = (int)Math.Min((ulong)(region.Size - offset), 65536UL);
                scanned += count;
                if (scanned > 128 * 1024 * 1024) return 0;
                byte[] block = new byte[count];
                if (!await ReadAsync(memory, region.Start + offset, block, token).ConfigureAwait(false)) return 0;
                for (int p = 0; p < block.Length; p++)
                {
                    for (int s = 0; s < Signatures.Length; s++)
                    {
                        byte?[] pattern = Signatures[s];
                        if (p + pattern.Length > block.Length || !Matches(block, p, pattern)) continue;
                        nuint root = s == 0 ? (nuint)BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(p + 10))
                            : BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(p + 25));
                        if (ValidPointer(root) && await SampleAsync(memory, root, token).ConfigureAwait(false) is not null)
                            roots.Add(root);
                        if (roots.Count > 1) return 0;
                    }
                }
                if (offset + (nuint)count == region.Size) break;
                offset += (nuint)Math.Max(1, count - 31);
            }
        }
        return completed && roots.Count == 1 ? roots.Single() : 0;
    }

    private static bool Matches(byte[] data, int at, byte?[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i] is byte expected && data[at + i] != expected) return false;
        return true;
    }

    internal static async ValueTask<HollowKnightSample?> SampleAsync(IReadOnlyProcessAttachment memory, nuint root, CancellationToken token)
    {
        nuint manager = await PointerAsync(memory, root, token).ConfigureAwait(false);
        if (manager == 0) return null;
        // Runtime debug version, not PlayerData.version (which belongs to the saved game).
        bool modern;
        string? version = await VersionAsync(memory, manager, false, token).ConfigureAwait(false);
        if (version is "1.5.68.11808" or "1.5.78.11833") modern = false;
        else
        {
            version = await VersionAsync(memory, manager, true, token).ConfigureAwait(false);
            if (version != "1.5.12459") return null;
            modern = true;
        }
        nuint playerOffset = modern ? (nuint)0xd0 : 0xc8;
        nuint stateOffset = modern ? (nuint)0x18c : 0x174;
        nuint heroOffset = modern ? (nuint)0x90 : 0x88;
        nuint player = await PointerAsync(memory, manager + playerOffset, token).ConfigureAwait(false);
        nuint hero = await PointerAsync(memory, manager + heroOffset, token).ConfigureAwait(false);
        // Unity object shells may survive destruction; m_CachedPtr must remain nonzero.
        if (player == 0 || hero == 0) return null;
        nuint nativeHero = await PointerAsync(memory, hero + 0x10, token).ConfigureAwait(false);
        if (nativeHero == 0) return null;
        int? state = await IntAsync(memory, manager + stateOffset, token).ConfigureAwait(false);
        int? health = await IntAsync(memory, player + (modern ? (nuint)0x198 : 0x190), token).ConfigureAwait(false);
        int? maxHealth = await IntAsync(memory, player + (modern ? (nuint)0x19c : 0x194), token).ConfigureAwait(false);
        // Reject partial reads, unsupported enums and torn samples. Never translate read failure into zero.
        if (state is null or < 0 or > 8 || health is null or < 0 or > 99 || maxHealth is null or < 1 or > 99 ||
            await IntAsync(memory, player + (modern ? (nuint)0x198 : 0x190), token).ConfigureAwait(false) != health ||
            await IntAsync(memory, manager + stateOffset, token).ConfigureAwait(false) != state ||
            await PointerAsync(memory, manager + playerOffset, token).ConfigureAwait(false) != player ||
            await PointerAsync(memory, manager + heroOffset, token).ConfigureAwait(false) != hero ||
            await PointerAsync(memory, hero + 0x10, token).ConfigureAwait(false) != nativeHero ||
            await PointerAsync(memory, root, token).ConfigureAwait(false) != manager) return null;
        return new(manager, player, hero, state.Value, health.Value);
    }

    private static async ValueTask<string?> VersionAsync(IReadOnlyProcessAttachment memory, nuint manager, bool modern, CancellationToken token)
    {
        nuint input = await PointerAsync(memory, manager + (modern ? (nuint)0x48 : 0x40), token).ConfigureAwait(false);
        nuint debug = input == 0 ? 0 : await PointerAsync(memory, input + (modern ? (nuint)0x68 : 0x60), token).ConfigureAwait(false);
        nuint text = debug == 0 ? 0 : await PointerAsync(memory, debug + (modern ? (nuint)0x40 : 0x38), token).ConfigureAwait(false);
        if (text == 0) return null;
        int? length = await IntAsync(memory, text + 0x10, token).ConfigureAwait(false);
        if (length is null or < 5 or > 32) return null;
        byte[] chars = new byte[length.Value * 2];
        return await ReadAsync(memory, text + 0x14, chars, token).ConfigureAwait(false) ? Encoding.Unicode.GetString(chars) : null;
    }

    private static bool ValidPointer(nuint address) => address >= 0x10000 && (ulong)address < 0x00007ffffff00000 && address % 8 == 0;

    private static async ValueTask<nuint> PointerAsync(IReadOnlyProcessAttachment memory, nuint address, CancellationToken token)
    {
        byte[] data = new byte[8];
        if (!await ReadAsync(memory, address, data, token).ConfigureAwait(false)) return 0;
        nuint value = (nuint)BinaryPrimitives.ReadUInt64LittleEndian(data);
        return ValidPointer(value) ? value : 0;
    }

    private static async ValueTask<int?> IntAsync(IReadOnlyProcessAttachment memory, nuint address, CancellationToken token)
    {
        byte[] data = new byte[4];
        return await ReadAsync(memory, address, data, token).ConfigureAwait(false) ? BinaryPrimitives.ReadInt32LittleEndian(data) : null;
    }

    private static async ValueTask<bool> ReadAsync(IReadOnlyProcessAttachment memory, nuint address, byte[] data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReadOnlyMemoryReadResult read = await memory.ReadVirtualMemoryAsync(address, data, token).ConfigureAwait(false);
        return read.Outcome == ReadOnlyMemoryReadOutcome.Succeeded && read.BytesRead == (nuint)data.Length;
    }
}
