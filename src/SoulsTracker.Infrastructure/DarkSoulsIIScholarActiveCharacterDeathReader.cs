using System.Buffers.Binary;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

public interface IDarkSoulsIIScholarProcessEnumerator
{
    ValueTask<IReadOnlyList<IDarkSoulsIIScholarProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken);
}

public interface IDarkSoulsIIScholarProcessCandidate : IAsyncDisposable
{
    int ProcessId { get; }
}

public sealed class ExactNameDarkSoulsIIScholarProcessEnumerator : IDarkSoulsIIScholarProcessEnumerator
{
    public ValueTask<IReadOnlyList<IDarkSoulsIIScholarProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<IDarkSoulsIIScholarProcessCandidate>>(Array.ConvertAll(Process.GetProcessesByName("DarkSoulsII"), static process => new Candidate(process)));
    }

    private sealed class Candidate(Process process) : IDarkSoulsIIScholarProcessCandidate
    {
        public int ProcessId => process.Id;
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Validates a DS2 Scholar profile only against QA-recorded exact identity evidence.</summary>
public interface IDarkSoulsIIScholarIdentityValidator
{
    ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken);
}

/// <summary>Fails closed until QA supplies an exact installed DS2 Scholar identity profile.</summary>
public sealed class UnverifiedDarkSoulsIIScholarIdentityValidator : IDarkSoulsIIScholarIdentityValidator
{
    public ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>Uses exact, QA-supplied DS2 Scholar identity profiles without normalization or fallback.</summary>
public sealed class ExactDarkSoulsIIScholarIdentityValidator : IDarkSoulsIIScholarIdentityValidator
{
    private readonly ProcessModuleFileIdentity[] expectedProfiles;

    public ExactDarkSoulsIIScholarIdentityValidator(params ProcessModuleFileIdentity[] expectedProfiles)
    {
        ArgumentNullException.ThrowIfNull(expectedProfiles);
        if (expectedProfiles.Length == 0 || expectedProfiles.Any(static profile => profile is null))
        {
            throw new ArgumentException("At least one non-null exact identity profile is required.", nameof(expectedProfiles));
        }

        this.expectedProfiles = [.. expectedProfiles];
    }

    public async ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken)
    {
        ReadOnlyModuleIdentityResult actual = await attachment.QueryMainModuleIdentityAsync(cancellationToken).ConfigureAwait(false);
        return actual.Outcome == ReadOnlyModuleIdentityOutcome.Available && actual.Identity is not null &&
            expectedProfiles.Any(expected =>
                string.Equals(actual.Identity.ExecutableFileName, expected.ExecutableFileName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(actual.Identity.FileVersion, expected.FileVersion, StringComparison.Ordinal) &&
                string.Equals(actual.Identity.ProductVersion, expected.ProductVersion, StringComparison.Ordinal) &&
                string.Equals(actual.Identity.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>DS2 Scholar 64-bit's frozen three-pointer active-character reader.</summary>
public sealed class DarkSoulsIIScholarActiveCharacterDeathReader : IRuntimeGameDeathReader
{
    private const nuint ModuleRelativePointerOffset = 0x16148F0;
    private const nuint FirstPointerOffset = 0xD0;
    private const nuint SecondPointerOffset = 0x490;
    private const nuint FinalValueOffset = 0x104;
    private const int PointerSize = sizeof(ulong);
    private const int ValueSize = sizeof(int);
    private const int MaximumPlausibleValue = 1_000_000;
    private readonly IDarkSoulsIIScholarProcessEnumerator processEnumerator;
    private readonly IReadOnlyProcessAttachmentFactory attachmentFactory;
    private readonly IDarkSoulsIIScholarIdentityValidator identityValidator;

    public DarkSoulsIIScholarActiveCharacterDeathReader(IDarkSoulsIIScholarProcessEnumerator processEnumerator, IReadOnlyProcessAttachmentFactory attachmentFactory, IDarkSoulsIIScholarIdentityValidator identityValidator)
    {
        this.processEnumerator = processEnumerator ?? throw new ArgumentNullException(nameof(processEnumerator));
        this.attachmentFactory = attachmentFactory ?? throw new ArgumentNullException(nameof(attachmentFactory));
        this.identityValidator = identityValidator ?? throw new ArgumentNullException(nameof(identityValidator));
    }

    public GameId GameId => GameId.Ds2;

    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || IntPtr.Size != PointerSize) return null;
        IReadOnlyList<IDarkSoulsIIScholarProcessCandidate> candidates;
        try { candidates = await processEnumerator.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken)) { return null; }
        if (candidates is null) return null;
        try
        {
            if (candidates.Count != 1) return null;
            ReadOnlyProcessAttachmentResult attachmentResult = await attachmentFactory.AttachAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
            if (attachmentResult.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || attachmentResult.Attachment is null) return null;
            await using IReadOnlyProcessAttachment attachment = attachmentResult.Attachment;
            if (!await identityValidator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false)) return null;
            ReadOnlyMainModuleBaseResult module = await attachment.QueryMainModuleBaseAsync(cancellationToken).ConfigureAwait(false);
            if (module.Outcome != ReadOnlyMainModuleBaseOutcome.Available) return null;
            nuint? first = await ReadPointerAsync(attachment, checked(module.BaseAddress + ModuleRelativePointerOffset), cancellationToken).ConfigureAwait(false);
            if (!first.HasValue) return null;
            if (first.Value == 0) return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            nuint? second = await ReadPointerAsync(attachment, checked(first.Value + FirstPointerOffset), cancellationToken).ConfigureAwait(false);
            if (!second.HasValue) return null;
            if (second.Value == 0) return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            nuint? third = await ReadPointerAsync(attachment, checked(second.Value + SecondPointerOffset), cancellationToken).ConfigureAwait(false);
            if (!third.HasValue) return null;
            if (third.Value == 0) return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            if (!await identityValidator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false)) return null;
            byte[] valueBytes = new byte[ValueSize];
            ReadOnlyMemoryReadResult valueRead = await attachment.ReadVirtualMemoryAsync(checked(third.Value + FinalValueOffset), valueBytes, cancellationToken).ConfigureAwait(false);
            if (valueRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || valueRead.BytesRead != (nuint)ValueSize) return null;
            int value = BinaryPrimitives.ReadInt32LittleEndian(valueBytes);
            return value is < 0 or > MaximumPlausibleValue
                ? null
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow, GameId.Value));
        }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken)) { return null; }
        finally { foreach (IDarkSoulsIIScholarProcessCandidate candidate in candidates) await candidate.DisposeAsync().ConfigureAwait(false); }
    }

    private static async ValueTask<nuint?> ReadPointerAsync(IReadOnlyProcessAttachment attachment, nuint address, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[PointerSize];
        ReadOnlyMemoryReadResult read = await attachment.ReadVirtualMemoryAsync(address, bytes, cancellationToken).ConfigureAwait(false);
        return read.Outcome == ReadOnlyMemoryReadOutcome.Succeeded && read.BytesRead == (nuint)PointerSize
            ? checked((nuint)BinaryPrimitives.ReadUInt64LittleEndian(bytes))
            : null;
    }

    private static bool IsExpectedUnavailable(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested ||
        exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or OverflowException;
}

/// <summary>Enumerates exact DS3 process candidates.</summary>
