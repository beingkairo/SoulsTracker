using System.Buffers.Binary;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

public interface IDarkSoulsRemasteredProcessEnumerator
{
    ValueTask<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken);
}

public interface IDarkSoulsRemasteredProcessCandidate : IAsyncDisposable
{
    int ProcessId { get; }
}

public sealed class ExactNameDarkSoulsRemasteredProcessEnumerator : IDarkSoulsRemasteredProcessEnumerator
{
    private const string ExecutableNameWithoutExtension = "DarkSoulsRemastered";

    public ValueTask<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process[] processes = Process.GetProcessesByName(ExecutableNameWithoutExtension);
        return ValueTask.FromResult<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>>(Array.ConvertAll(processes, static process => new Candidate(process)));
    }

    private sealed class Candidate(Process process) : IDarkSoulsRemasteredProcessCandidate
    {
        public int ProcessId => process.Id;
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>DS1 Remastered's frozen, identity-gated, one-pointer active-character reader.</summary>
public sealed class DarkSoulsRemasteredActiveCharacterDeathReader : IRuntimeGameDeathReader
{
    private const nuint ModuleRelativePointerOffset = 0x1C8A530;
    private const nuint FinalValueOffset = 0x98;
    private const int PointerSize = sizeof(ulong);
    private const int ValueSize = sizeof(int);
    private const int MaximumPlausibleValue = 1_000_000;
    private readonly IDarkSoulsRemasteredProcessEnumerator processEnumerator;
    private readonly IReadOnlyProcessAttachmentFactory attachmentFactory;

    public DarkSoulsRemasteredActiveCharacterDeathReader(IDarkSoulsRemasteredProcessEnumerator processEnumerator, IReadOnlyProcessAttachmentFactory attachmentFactory)
    {
        this.processEnumerator = processEnumerator ?? throw new ArgumentNullException(nameof(processEnumerator));
        this.attachmentFactory = attachmentFactory ?? throw new ArgumentNullException(nameof(attachmentFactory));
    }

    public GameId GameId => GameId.Ds1;

    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || IntPtr.Size != PointerSize) return null;
        IReadOnlyList<IDarkSoulsRemasteredProcessCandidate> candidates;
        try { candidates = await processEnumerator.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken)) { return null; }
        if (candidates is null) return null;
        try
        {
            if (candidates.Count != 1) return null;
            ReadOnlyProcessAttachmentResult attachmentResult = await attachmentFactory.AttachAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
            if (attachmentResult.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || attachmentResult.Attachment is null) return null;
            await using IReadOnlyProcessAttachment attachment = attachmentResult.Attachment;
            if ((await DarkSoulsRemasteredCandidateIdentityValidator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false)).Outcome != DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate) return null;
            ReadOnlyMainModuleBaseResult module = await attachment.QueryMainModuleBaseAsync(cancellationToken).ConfigureAwait(false);
            if (module.Outcome != ReadOnlyMainModuleBaseOutcome.Available) return null;
            nuint pointerAddress = checked(module.BaseAddress + ModuleRelativePointerOffset);
            byte[] pointerBytes = new byte[PointerSize];
            ReadOnlyMemoryReadResult pointerRead = await attachment.ReadVirtualMemoryAsync(pointerAddress, pointerBytes, cancellationToken).ConfigureAwait(false);
            if (pointerRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || pointerRead.BytesRead != (nuint)PointerSize) return null;
            nuint pointer = checked((nuint)BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes));
            if (pointer == 0) return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            if ((await DarkSoulsRemasteredCandidateIdentityValidator.ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false)).Outcome != DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate) return null;
            byte[] valueBytes = new byte[ValueSize];
            ReadOnlyMemoryReadResult valueRead = await attachment.ReadVirtualMemoryAsync(checked(pointer + FinalValueOffset), valueBytes, cancellationToken).ConfigureAwait(false);
            if (valueRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || valueRead.BytesRead != (nuint)ValueSize) return null;
            int value = BinaryPrimitives.ReadInt32LittleEndian(valueBytes);
            return value is < 0 or > MaximumPlausibleValue
                ? null
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow, GameId.Value));
        }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken)) { return null; }
        finally { foreach (IDarkSoulsRemasteredProcessCandidate candidate in candidates) await candidate.DisposeAsync().ConfigureAwait(false); }
    }

    private static bool IsExpectedUnavailable(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested ||
        exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or OverflowException;
}

/// <summary>Enumerates only the DS2 Scholar executable name and disposes every process object.</summary>
