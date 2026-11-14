using System.Buffers.Binary;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

public interface IDarkSoulsIIIProcessEnumerator
{
    ValueTask<IReadOnlyList<IDarkSoulsIIIProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one disposable DS3 process candidate.</summary>
public interface IDarkSoulsIIIProcessCandidate : IAsyncDisposable
{
    int ProcessId { get; }
}
public sealed class ExactNameDarkSoulsIIIProcessEnumerator : IDarkSoulsIIIProcessEnumerator
{
    public ValueTask<IReadOnlyList<IDarkSoulsIIIProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<IDarkSoulsIIIProcessCandidate>>(Array.ConvertAll(Process.GetProcessesByName("DarkSoulsIII"), static process => new Candidate(process)));
    }
    private sealed class Candidate(Process process) : IDarkSoulsIIIProcessCandidate
    {
        public int ProcessId => process.Id;
        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Validates one attached DS3 process against an approved exact profile.</summary>
public interface IDarkSoulsIIIIdentityValidator
{
    ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken);
}

/// <summary>Fails closed until QA supplies DS3 identity evidence.</summary>
public sealed class UnverifiedDarkSoulsIIIIdentityValidator : IDarkSoulsIIIIdentityValidator
{
    public ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>Performs ordinal exact identity comparison without normalization or fallback.</summary>
public sealed class ExactDarkSoulsIIIIdentityValidator(ProcessModuleFileIdentity expected) : IDarkSoulsIIIIdentityValidator
{
    private readonly ProcessModuleFileIdentity expected = expected ?? throw new ArgumentNullException(nameof(expected));
    public async ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken)
    {
        ReadOnlyModuleIdentityResult actual = await attachment.QueryMainModuleIdentityAsync(cancellationToken).ConfigureAwait(false);
        return actual.Outcome == ReadOnlyModuleIdentityOutcome.Available && actual.Identity is not null &&
            string.Equals(actual.Identity.ExecutableFileName, expected.ExecutableFileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(actual.Identity.FileVersion, expected.FileVersion, StringComparison.Ordinal) &&
            string.Equals(actual.Identity.ProductVersion, expected.ProductVersion, StringComparison.Ordinal) &&
            string.Equals(actual.Identity.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>DS3's frozen one-pointer active-character reader; unavailable until its exact profile is configured.</summary>
public sealed class DarkSoulsIIIActiveCharacterDeathReader : IRuntimeGameDeathReader
{
    private const nuint PointerOffset = 0x47572B8;
    private const nuint ValueOffset = 0x98;
    private readonly IDarkSoulsIIIProcessEnumerator enumerator;
    private readonly IReadOnlyProcessAttachmentFactory factory;
    private readonly IDarkSoulsIIIIdentityValidator validator;

    public DarkSoulsIIIActiveCharacterDeathReader(IDarkSoulsIIIProcessEnumerator enumerator, IReadOnlyProcessAttachmentFactory factory, IDarkSoulsIIIIdentityValidator validator)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }
    public GameId GameId => GameId.Ds3;
    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || IntPtr.Size != sizeof(ulong)) return null;
        IReadOnlyList<IDarkSoulsIIIProcessCandidate> candidates;
        try { candidates = await enumerator.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (Unavailable(ex, cancellationToken)) { return null; }
        if (candidates is null) return null;
        try
        {
            if (candidates.Count != 1) return null;
            ReadOnlyProcessAttachmentResult result = await factory.AttachAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || result.Attachment is null) return null;
            await using IReadOnlyProcessAttachment attachment = result.Attachment;
            if (!await validator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false)) return null;
            ReadOnlyMainModuleBaseResult module = await attachment.QueryMainModuleBaseAsync(cancellationToken).ConfigureAwait(false);
            if (module.Outcome != ReadOnlyMainModuleBaseOutcome.Available) return null;
            byte[] pointerBytes = new byte[8];
            ReadOnlyMemoryReadResult pointerRead = await attachment.ReadVirtualMemoryAsync(checked(module.BaseAddress + PointerOffset), pointerBytes, cancellationToken).ConfigureAwait(false);
            if (pointerRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || pointerRead.BytesRead != 8) return null;
            nuint pointer = checked((nuint)BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes));
            if (pointer == 0) return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            if (!await validator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false)) return null;
            byte[] valueBytes = new byte[4];
            ReadOnlyMemoryReadResult valueRead = await attachment.ReadVirtualMemoryAsync(checked(pointer + ValueOffset), valueBytes, cancellationToken).ConfigureAwait(false);
            if (valueRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || valueRead.BytesRead != 4) return null;
            int value = BinaryPrimitives.ReadInt32LittleEndian(valueBytes);
            return value is < 0 or > 1_000_000
                ? null
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow, GameId.Value));
        }
        catch (Exception ex) when (Unavailable(ex, cancellationToken)) { return null; }
        finally
        {
            foreach (IDarkSoulsIIIProcessCandidate candidate in candidates) await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }
    private static bool Unavailable(Exception ex, CancellationToken token) => ex is OperationCanceledException && token.IsCancellationRequested || ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or OverflowException;
}

/// <summary>Enumerates exact Sekiro process candidates.</summary>
