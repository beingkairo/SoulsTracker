using System.Buffers.Binary;
using System.Diagnostics;

namespace SoulsTracker.Infrastructure;

/// <summary>Validation targets, not a declaration of desktop reader support.</summary>
public enum BloodborneCompatibilityRuntime { ShadPs4, Bbport, LanternEngine }

/// <summary>The location relationship, not merely equality of two current values.</summary>
public enum BloodborneLegacyCounterRelation { Neither, Deaths, TrueDeaths }

/// <summary>Independent result of the released reader's bounded four-byte address check.</summary>
public enum BloodborneLegacyReadStatus { NotAttempted, Unreadable, Implausible, Readable }

/// <summary>Safe outcomes of the shared bounded memory check; diagnostic-only fields are not tracker totals.</summary>
public enum BloodborneCompatibilityStatus
{
    Unavailable,
    AmbiguousProcesses,
    UnmatchedImage,
    AmbiguousImages,
    WaitingForCharacter,
    UnstableRead,
    Sampled,
    AttachmentDenied,
    AttachmentUnavailable,
    GuestRejected,
    CounterUnavailable,
}

/// <summary>The shared read result. Desktop uses only the owner-verified Deaths field after a successful check.</summary>
public sealed record BloodborneCompatibilitySample(
    BloodborneCompatibilityStatus Status,
    uint? Deaths = null,
    uint? TrueDeaths = null,
    uint? LegacyValue = null,
    BloodborneLegacyCounterRelation LegacyRelation = BloodborneLegacyCounterRelation.Neither,
    BloodborneLegacyReadStatus LegacyRead = BloodborneLegacyReadStatus.NotAttempted);

internal interface IReadOnlyProcessMemoryMap
{
    ValueTask<ReadOnlyProcessMemoryRegion?> QueryRegionAsync(nuint address, CancellationToken cancellationToken);
}

internal readonly record struct ReadOnlyProcessMemoryRegion(
    nuint Start, nuint Allocation, nuint Size, uint State, uint Protection, uint Type)
{
    internal bool IsPrivateExecutable => State == 0x1000 && Type == 0x20000 &&
        (Protection & 0x100) == 0 && (Protection & 0xff) is 0x20 or 0x40 or 0x80;
}

/// <summary>
/// Read-only owner-validation harness for the two prepared-image runtimes. Intentionally
/// not an IRuntimeGameDeathReader and not composed into the desktop application.
/// </summary>
public sealed class BloodborneCompatibilityProbe
{
    private readonly IBloodborneProcessEnumerator processes;
    private readonly IReadOnlyProcessAttachmentFactory attachments;
    private readonly BloodborneCompatibilityRuntime runtime;

    public BloodborneCompatibilityProbe(IBloodborneProcessEnumerator processes, IReadOnlyProcessAttachmentFactory attachments,
        BloodborneCompatibilityRuntime runtime = BloodborneCompatibilityRuntime.Bbport)
    {
        this.processes = processes ?? throw new ArgumentNullException(nameof(processes));
        this.attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
        if (!Enum.IsDefined(runtime)) throw new ArgumentOutOfRangeException(nameof(runtime));
        this.runtime = runtime;
    }

    public static BloodborneCompatibilityProbe Create(BloodborneCompatibilityRuntime runtime) => new(
        runtime == BloodborneCompatibilityRuntime.ShadPs4
            ? new ExactNameBloodborneProcessEnumerator()
            : new BloodbornePortProcessEnumerator(runtime),
        new WindowsReadOnlyProcessAttachmentFactory(), runtime);

    public async ValueTask<BloodborneCompatibilitySample> SampleAsync(CancellationToken cancellationToken)
    {
        if (IntPtr.Size != sizeof(ulong) || cancellationToken.IsCancellationRequested)
            return new(BloodborneCompatibilityStatus.Unavailable);

        IReadOnlyList<IBloodborneProcessCandidate> candidates = [];
        try
        {
            candidates = await processes.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count != 1)
                return new(candidates.Count == 0 ? BloodborneCompatibilityStatus.Unavailable : BloodborneCompatibilityStatus.AmbiguousProcesses);
            if (!await candidates[0].IsExpectedGuestActiveAsync(cancellationToken).ConfigureAwait(false))
                return new(BloodborneCompatibilityStatus.GuestRejected);

            ReadOnlyProcessAttachmentResult result = await attachments.AttachAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || result.Attachment is null)
                return new(result.AccessDenied ? BloodborneCompatibilityStatus.AttachmentDenied : BloodborneCompatibilityStatus.AttachmentUnavailable);
            await using IReadOnlyProcessAttachment attachment = result.Attachment;
            return await BloodborneGuestMemoryProbe.SampleAsync(attachment, cancellationToken,
                runtime == BloodborneCompatibilityRuntime.ShadPs4).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or OverflowException ||
            exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return new(BloodborneCompatibilityStatus.Unavailable);
        }
        finally
        {
            foreach (IBloodborneProcessCandidate candidate in candidates)
                await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class BloodbornePortProcessEnumerator(BloodborneCompatibilityRuntime runtime) : IBloodborneProcessEnumerator
{
    // The names only narrow discovery; the memory probe must independently match the guest image.
    internal static IReadOnlyList<string> NamesFor(BloodborneCompatibilityRuntime runtime) => runtime switch
    {
        BloodborneCompatibilityRuntime.Bbport => ["bbport", "bb-probe"],
        BloodborneCompatibilityRuntime.LanternEngine => ["bb-probe"],
        _ => throw new ArgumentOutOfRangeException(nameof(runtime)),
    };

    public ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<IBloodborneProcessCandidate>();
        try
        {
            foreach (string name in NamesFor(runtime))
                foreach (Process process in Process.GetProcessesByName(name))
                    candidates.Add(new Candidate(process));
            return ValueTask.FromResult<IReadOnlyList<IBloodborneProcessCandidate>>(candidates);
        }
        catch
        {
            foreach (Candidate candidate in candidates.Cast<Candidate>()) candidate.Dispose();
            throw;
        }
    }

    private sealed class Candidate(Process process) : IBloodborneProcessCandidate, IDisposable
    {
        public int ProcessId => process.Id;
        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(!cancellationToken.IsCancellationRequested && !process.HasExited &&
                process.MainWindowTitle.Contains("Bloodborne", StringComparison.OrdinalIgnoreCase));
        public void Dispose() => process.Dispose();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal static class BloodborneGuestMemoryProbe
{
    // Sources and address conventions: docs/BLOODBORNE_PC_COMPATIBILITY.md.
    // These instruction fingerprints are immutable guest bytes, not runtime executable offsets.
    // They identify the known layout, not a cryptographic hash of the whole executable.
    // Lantern cheats.cpp stores Binary Ninja addresses; its Matches() subtracts this base.
    // ManagerSlot/NowLoading below are already image offsets and must not be converted again.
    private const nuint PreferredDisassemblerBase = 0x400000;
    private static readonly (nuint Offset, byte[] Bytes)[] Fingerprints =
    [
        (0x231e3d0 - PreferredDisassemblerBase, Convert.FromHexString("554889E5415741564154534189F44989FE31C94585E40F88F2000000")),
        (0x231e7e0 - PreferredDisassemblerBase, Convert.FromHexString("554889E5534883EC28488B1DF0534C03488B03488945F0488B4F08B8630000004885C90F848C0000008B51420FB74146")),
        (0x18d9f60 - PreferredDisassemblerBase, Convert.FromHexString("8B0683F8FF0F84A600000089C14869C9153FEF0348C1E920")),
    ];
    internal const ulong LegacyAddress = 0x2080673B8;
    internal const nuint ManagerSlot = 0x553b130;
    private const nuint NowLoading = 0x556286b;
    private const uint MaximumDeaths = 1_000_000;
    // Both loaders deliberately use 40-bit guest addresses. Out-of-range fallback allocations
    // are unsupported here, rather than guessed. Walk metadata only, never scan whole heaps.
    private static readonly nuint GuestAddressLimit = unchecked((nuint)(1UL << 40));
    private const int MaximumRegions = 65_536;
    private const int MaximumAllocations = 512;

    internal static async ValueTask<BloodborneCompatibilitySample> SampleAsync(IReadOnlyProcessAttachment memory, CancellationToken token,
        bool legacyGuest = false)
    {
        if (token.IsCancellationRequested) return new(BloodborneCompatibilityStatus.Unavailable);
        // Check the legacy address before asking whether the proposed guest-image layout fits.
        // A fingerprint failure must not be mistaken for an unreadable released counter.
        uint? legacy = legacyGuest
            ? await Read32Async(memory, unchecked((nuint)LegacyAddress), token).ConfigureAwait(false)
            : null;
        BloodborneLegacyReadStatus legacyRead = !legacyGuest ? BloodborneLegacyReadStatus.NotAttempted :
            legacy is null ? BloodborneLegacyReadStatus.Unreadable :
            legacy > MaximumDeaths ? BloodborneLegacyReadStatus.Implausible : BloodborneLegacyReadStatus.Readable;
        BloodborneCompatibilitySample sample = await SampleCoreAsync(memory, legacyGuest, includeTrueDeaths: true, token).ConfigureAwait(false);
        return sample with { LegacyValue = legacy, LegacyRead = legacyRead };
    }

    // The owner verified deaths +0x88 changing 0->1 with true_deaths +0x84 staying zero
    // in both target ports. Desktop uses the same image/character guards, reading only deaths.
    internal static ValueTask<BloodborneCompatibilitySample> ReadPreparedDeathsAsync(IReadOnlyProcessAttachment memory,
        CancellationToken token) => SampleCoreAsync(memory, legacyGuest: false, includeTrueDeaths: false, token);

    private static async ValueTask<BloodborneCompatibilitySample> SampleCoreAsync(IReadOnlyProcessAttachment memory, bool legacyGuest, bool includeTrueDeaths,
        CancellationToken token)
    {
        nuint? image = null;
        if (legacyGuest)
        {
            // shadPS4's source-verified executable load base. Its managed backing may be
            // MEM_MAPPED, so do not apply the prepared loaders' private-allocation filter.
            if (await MatchesAsync(memory, 0x400000, token).ConfigureAwait(false)) image = 0x400000;
        }
        else
        {
            if (memory is not IReadOnlyProcessMemoryMap map) return new(BloodborneCompatibilityStatus.Unavailable);
            var bases = new HashSet<nuint>();
            nuint cursor = 0;
            int queries = 0;
            while (cursor < GuestAddressLimit)
            {
                if (token.IsCancellationRequested || ++queries > MaximumRegions)
                    return new(BloodborneCompatibilityStatus.Unavailable);
                ReadOnlyProcessMemoryRegion? query = await map.QueryRegionAsync(cursor, token).ConfigureAwait(false);
                if (query is not { } region || region.Start != cursor || region.Size == 0 || region.Size > nuint.MaxValue - cursor)
                    return new(BloodborneCompatibilityStatus.Unavailable);
                if (region.IsPrivateExecutable && region.Allocation != 0 && region.Allocation <= region.Start &&
                    region.Allocation < GuestAddressLimit - NowLoading - 1)
                    bases.Add(region.Allocation);
                if (bases.Count > MaximumAllocations) return new(BloodborneCompatibilityStatus.Unavailable);
                cursor += region.Size;
            }

            foreach (nuint candidate in bases)
            {
                if (!await MatchesAsync(memory, candidate, token).ConfigureAwait(false)) continue;
                if (image is not null) return new(BloodborneCompatibilityStatus.AmbiguousImages);
                image = candidate;
            }
        }
        if (image is not { } loaded) return new(BloodborneCompatibilityStatus.UnmatchedImage);

        byte[] loading = new byte[1];
        ulong? manager = await Read64Async(memory, loaded + ManagerSlot, token).ConfigureAwait(false);
        if (manager is not > 0 || manager > (ulong)GuestAddressLimit - 0x98 || manager % 8 != 0)
            return new(BloodborneCompatibilityStatus.WaitingForCharacter);
        nuint man = checked((nuint)manager.Value);
        ulong? record = await Read64Async(memory, man + 8, token).ConfigureAwait(false);
        if (record is not > 0 || record > (ulong)GuestAddressLimit - 0x94 || record % 8 != 0 ||
            !await ReadAsync(memory, loaded + NowLoading, loading, token).ConfigureAwait(false) || loading[0] != 0)
            return new(BloodborneCompatibilityStatus.WaitingForCharacter);
        uint? level = await Read32Async(memory, checked((nuint)record.Value) + 0x90, token).ConfigureAwait(false);
        if (level is not > 0) return new(BloodborneCompatibilityStatus.WaitingForCharacter);

        // Diagnostics also show the adjacent candidate; production reads only the verified field.
        byte[] counters = new byte[includeTrueDeaths ? 8 : 4];
        if (!await ReadAsync(memory, man + (includeTrueDeaths ? (nuint)0x84 : 0x88), counters, token).ConfigureAwait(false))
            return new(BloodborneCompatibilityStatus.CounterUnavailable);
        uint? trueDeaths = includeTrueDeaths ? BinaryPrimitives.ReadUInt32LittleEndian(counters) : null;
        uint deaths = BinaryPrimitives.ReadUInt32LittleEndian(counters.AsSpan(includeTrueDeaths ? 4 : 0));
        if (deaths > MaximumDeaths || trueDeaths > MaximumDeaths)
            return new(BloodborneCompatibilityStatus.CounterUnavailable);

        if (manager != await Read64Async(memory, loaded + ManagerSlot, token).ConfigureAwait(false) ||
            record != await Read64Async(memory, man + 8, token).ConfigureAwait(false) ||
            !await ReadAsync(memory, loaded + NowLoading, loading, token).ConfigureAwait(false) || loading[0] != 0 ||
            !await MatchesAsync(memory, loaded, token).ConfigureAwait(false))
            return new(BloodborneCompatibilityStatus.UnstableRead);

        BloodborneLegacyCounterRelation relation = (ulong)man + 0x88 == LegacyAddress ? BloodborneLegacyCounterRelation.Deaths :
            (ulong)man + 0x84 == LegacyAddress ? BloodborneLegacyCounterRelation.TrueDeaths : BloodborneLegacyCounterRelation.Neither;
        if (token.IsCancellationRequested) return new(BloodborneCompatibilityStatus.Unavailable);
        return new(BloodborneCompatibilityStatus.Sampled, deaths, trueDeaths, LegacyRelation: relation);
    }

    private static async ValueTask<bool> MatchesAsync(IReadOnlyProcessAttachment memory, nuint image, CancellationToken token)
    {
        foreach ((nuint offset, byte[] expected) in Fingerprints)
        {
            byte[] actual = new byte[expected.Length];
            if (!await ReadAsync(memory, checked(image + offset), actual, token).ConfigureAwait(false) ||
                !actual.AsSpan().SequenceEqual(expected)) return false;
        }
        return true;
    }

    private static async ValueTask<bool> ReadAsync(IReadOnlyProcessAttachment memory, nuint at, byte[] bytes, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        ReadOnlyMemoryReadResult read = await memory.ReadVirtualMemoryAsync(at, bytes, token).ConfigureAwait(false);
        return !token.IsCancellationRequested && read.Outcome == ReadOnlyMemoryReadOutcome.Succeeded && read.BytesRead == (nuint)bytes.Length;
    }

    private static async ValueTask<ulong?> Read64Async(IReadOnlyProcessAttachment memory, nuint at, CancellationToken token)
    {
        byte[] bytes = new byte[8];
        return await ReadAsync(memory, at, bytes, token).ConfigureAwait(false) ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : null;
    }

    private static async ValueTask<uint?> Read32Async(IReadOnlyProcessAttachment memory, nuint at, CancellationToken token)
    {
        byte[] bytes = new byte[4];
        return await ReadAsync(memory, at, bytes, token).ConfigureAwait(false) ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
    }
}
