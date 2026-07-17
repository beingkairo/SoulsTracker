using System.Buffers.Binary;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Reads a game-provided active-character death total without changing the target process.</summary>
public interface IRuntimeGameDeathReader
{
    GameId GameId { get; }

    ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Describes the safe runtime state of the selected automated reader.</summary>
public enum RuntimeGameReaderStatus
{
    Unavailable,
    WaitingForActiveCharacter,
    Synced,
}

/// <summary>Contains only a safe reader status and, when synced, its runtime-only observation.</summary>
public sealed record RuntimeGameReadResult
{
    private RuntimeGameReadResult(GameId gameId, RuntimeGameReaderStatus status, RuntimeGameObservation? observation)
    {
        GameId = gameId ?? throw new ArgumentNullException(nameof(gameId));
        Status = status;
        Observation = observation;
    }

    public GameId GameId { get; }

    public RuntimeGameReaderStatus Status { get; }

    public RuntimeGameObservation? Observation { get; }

    public static RuntimeGameReadResult WaitingForActiveCharacter(GameId gameId) =>
        new(gameId, RuntimeGameReaderStatus.WaitingForActiveCharacter, null);

    public static RuntimeGameReadResult Synced(RuntimeGameObservation observation) =>
        new(observation?.GameId ?? throw new ArgumentNullException(nameof(observation)), RuntimeGameReaderStatus.Synced, observation);
}

/// <summary>Coordinates the small set of approved runtime-only game readers.</summary>
public sealed class RuntimeGameReaderCoordinator
{
    private readonly Dictionary<GameId, IRuntimeGameDeathReader> readers;

    public RuntimeGameReaderCoordinator(IEnumerable<IRuntimeGameDeathReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        IRuntimeGameDeathReader[] supplied = readers.ToArray();
        if (supplied.Any(static reader => reader is null) || supplied.GroupBy(static reader => reader.GameId).Any(static group => group.Count() != 1))
        {
            throw new ArgumentException("Readers must be non-null and unique per game.", nameof(readers));
        }

        this.readers = supplied.ToDictionary(static reader => reader.GameId);
    }

    /// <summary>Gets the latest observation for presentation only. It is never persisted.</summary>
    public RuntimeGameReadResult? CurrentResult { get; private set; }

    public RuntimeGameReaderStatus CurrentStatus => CurrentResult?.Status ?? RuntimeGameReaderStatus.Unavailable;

    public RuntimeGameObservation? CurrentObservation => CurrentResult?.Observation;

    public event EventHandler<RuntimeGameReadResult?>? ObservationChanged;

    /// <summary>Polls only the selected approved reader and clears stale observations on every unavailable or selection change.</summary>
    public async ValueTask<RuntimeGameReadResult?> PollAsync(GameId? selectedGameId, CancellationToken cancellationToken)
    {
        RuntimeGameReadResult? result = null;
        if (selectedGameId is not null && readers.TryGetValue(selectedGameId, out IRuntimeGameDeathReader? reader))
        {
            result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Equals(CurrentResult, result))
        {
            CurrentResult = result;
            ObservationChanged?.Invoke(this, result);
        }

        return result;
    }
}

/// <summary>Enumerates only the exact Dark Souls Remastered executable name and disposes every process object.</summary>
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
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken)) { return null; }
        finally { foreach (IDarkSoulsRemasteredProcessCandidate candidate in candidates) await candidate.DisposeAsync().ConfigureAwait(false); }
    }

    private static bool IsExpectedUnavailable(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested ||
        exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or OverflowException;
}

/// <summary>Enumerates only the DS2 Scholar executable name and disposes every process object.</summary>
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

/// <summary>Uses exact, QA-supplied DS2 Scholar identity fields without normalization or fallback.</summary>
public sealed class ExactDarkSoulsIIScholarIdentityValidator(ProcessModuleFileIdentity expected) : IDarkSoulsIIScholarIdentityValidator
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
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow));
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
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow));
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
                : RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId, value, DateTimeOffset.UtcNow));
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
