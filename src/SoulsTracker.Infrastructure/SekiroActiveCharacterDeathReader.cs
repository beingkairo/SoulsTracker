using System.Buffers.Binary;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

public interface ISekiroProcessEnumerator
{
    ValueTask<IReadOnlyList<ISekiroProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one disposable Sekiro process candidate.</summary>
public interface ISekiroProcessCandidate : IAsyncDisposable
{
    int ProcessId { get; }
}

/// <summary>Enumerates only processes whose executable name is exactly Sekiro.</summary>
public sealed class ExactNameSekiroProcessEnumerator : ISekiroProcessEnumerator
{
    public ValueTask<IReadOnlyList<ISekiroProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ISekiroProcessCandidate>>(
            Array.ConvertAll(Process.GetProcessesByName("Sekiro"), static process => new Candidate(process)));
    }

    private sealed class Candidate(Process process) : ISekiroProcessCandidate
    {
        public int ProcessId => process.Id;

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Validates one attached Sekiro process against an approved exact profile.</summary>
public interface ISekiroIdentityValidator
{
    ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken);
}

/// <summary>Fails closed until QA supplies Sekiro identity evidence.</summary>
public sealed class UnverifiedSekiroIdentityValidator : ISekiroIdentityValidator
{
    public ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>Performs ordinal exact identity comparison without normalization or fallback.</summary>
public sealed class ExactSekiroIdentityValidator(ProcessModuleFileIdentity expected) : ISekiroIdentityValidator
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

/// <summary>Sekiro's frozen one-pointer active-character reader; unavailable until its exact profile is configured.</summary>
public sealed class SekiroActiveCharacterDeathReader : IRuntimeGameDeathReader
{
    private const nuint PointerOffset = 0x3D5AAC0;
    private const nuint ValueOffset = 0x90;
    private const int PointerSize = sizeof(ulong);
    private const int ValueSize = sizeof(int);
    private const int MaximumPlausibleValue = 1_000_000;
    private readonly ISekiroProcessEnumerator enumerator;
    private readonly IReadOnlyProcessAttachmentFactory factory;
    private readonly ISekiroIdentityValidator validator;

    public SekiroActiveCharacterDeathReader(
        ISekiroProcessEnumerator enumerator,
        IReadOnlyProcessAttachmentFactory factory,
        ISekiroIdentityValidator validator)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public GameId GameId => GameId.Sekiro;

    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || IntPtr.Size != PointerSize)
        {
            return null;
        }

        IReadOnlyList<ISekiroProcessCandidate> candidates;
        try
        {
            candidates = await enumerator.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken))
        {
            return null;
        }

        if (candidates is null)
        {
            return null;
        }

        try
        {
            if (candidates.Count != 1)
            {
                return null;
            }

            ReadOnlyProcessAttachmentResult attachmentResult = await factory.AttachAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
            if (attachmentResult.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || attachmentResult.Attachment is null)
            {
                return null;
            }

            await using IReadOnlyProcessAttachment attachment = attachmentResult.Attachment;
            if (!await validator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            ReadOnlyMainModuleBaseResult module = await attachment.QueryMainModuleBaseAsync(cancellationToken).ConfigureAwait(false);
            if (module.Outcome != ReadOnlyMainModuleBaseOutcome.Available)
            {
                return null;
            }

            byte[] pointerBytes = new byte[PointerSize];
            ReadOnlyMemoryReadResult pointerRead = await attachment.ReadVirtualMemoryAsync(
                checked(module.BaseAddress + PointerOffset),
                pointerBytes,
                cancellationToken).ConfigureAwait(false);
            if (pointerRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || pointerRead.BytesRead != PointerSize)
            {
                return null;
            }

            nuint pointer = checked((nuint)BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes));
            if (pointer == 0)
            {
                return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            }

            if (!await validator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            byte[] valueBytes = new byte[ValueSize];
            ReadOnlyMemoryReadResult valueRead = await attachment.ReadVirtualMemoryAsync(
                checked(pointer + ValueOffset),
                valueBytes,
                cancellationToken).ConfigureAwait(false);
            if (valueRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || valueRead.BytesRead != ValueSize)
            {
                return null;
            }

            int value = BinaryPrimitives.ReadInt32LittleEndian(valueBytes);
            return value is < 0 or > MaximumPlausibleValue
                ? null
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow, GameId.Value));
        }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken))
        {
            return null;
        }
        finally
        {
            foreach (ISekiroProcessCandidate candidate in candidates)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsExpectedUnavailable(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested ||
        exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
        InvalidOperationException or System.Security.SecurityException or OverflowException;
}

/// <summary>Enumerates only the verified Bloodborne runtime host process.</summary>
