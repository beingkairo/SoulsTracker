using System.Buffers.Binary;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

/// <summary>
/// Test-only, user-confirmed validation of one frozen legacy-reference
/// hypothesis. It is not a product reader or support declaration.
/// </summary>
internal sealed class DarkSoulsRemasteredSingleIncrementValidationHarness
{
    internal const string ConfirmationEnvironmentVariable = "SOULSTRACKER_DSR_SINGLE_INCREMENT_CONFIRM";
    internal const string RequiredConfirmation = "I_CONFIRM_OFFLINE_DARK_SOULS_REMASTERED_IS_OPEN_AND_I_WILL_MANUALLY_DIE_ONCE";

    private const nuint ModuleRelativePointerOffset = 0x1C8A530;
    private const nuint FinalValueOffset = 0x98;
    private const int PointerSize = sizeof(ulong);
    private const int ValueSize = sizeof(int);
    private const int MaximumPlausibleValue = 1_000_000;
    private const int MaximumPollsAfterBaseline = 90;
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(2);

    private readonly IDarkSoulsRemasteredProcessEnumerator processEnumerator;
    private readonly IReadOnlyProcessAttachmentFactory attachmentFactory;
    private readonly IValidationDelay delay;

    internal DarkSoulsRemasteredSingleIncrementValidationHarness(
        IDarkSoulsRemasteredProcessEnumerator processEnumerator,
        IReadOnlyProcessAttachmentFactory attachmentFactory,
        IValidationDelay delay)
    {
        this.processEnumerator = processEnumerator ?? throw new ArgumentNullException(nameof(processEnumerator));
        this.attachmentFactory = attachmentFactory ?? throw new ArgumentNullException(nameof(attachmentFactory));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    internal async ValueTask<DarkSoulsRemasteredSingleIncrementValidationResult?> RunIfConfirmedAsync(
        string? confirmation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmation, RequiredConfirmation, StringComparison.Ordinal))
        {
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
        }

        IReadOnlyList<IDarkSoulsRemasteredProcessCandidate> candidates;
        try
        {
            candidates = await processEnumerator.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
        }
        catch (IOException)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
        }
        catch (UnauthorizedAccessException)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
        }
        catch (InvalidOperationException)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
        }
        catch (System.Security.SecurityException)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
        }

        if (candidates is null)
        {
            return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
            }

            if (candidates.Count != 1 || IntPtr.Size != PointerSize)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }

            ReadOnlyProcessAttachmentResult attachmentResult;
            try
            {
                attachmentResult = await attachmentFactory.AttachAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
            }
            catch (IOException)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }
            catch (UnauthorizedAccessException)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }
            catch (InvalidOperationException)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }
            catch (System.Security.SecurityException)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }

            if (attachmentResult.Outcome == ReadOnlyProcessAttachmentOutcome.Cancelled)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
            }

            if (attachmentResult.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || attachmentResult.Attachment is null)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }

            await using IReadOnlyProcessAttachment attachment = attachmentResult.Attachment;
            Observation baseline = await ReadObservationAsync(attachment, cancellationToken).ConfigureAwait(false);
            if (baseline.Outcome == ObservationOutcome.Cancelled)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
            }

            if (baseline.Outcome != ObservationOutcome.Available)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }

            int expectedIncrement;
            try
            {
                expectedIncrement = checked(baseline.Value + 1);
            }
            catch (OverflowException)
            {
                return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
            }

            for (int poll = 0; poll < MaximumPollsAfterBaseline; poll++)
            {
                try
                {
                    await delay.DelayAsync(MinimumPollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
                }

                Observation current = await ReadObservationAsync(attachment, cancellationToken).ConfigureAwait(false);
                if (current.Outcome == ObservationOutcome.Cancelled)
                {
                    return DarkSoulsRemasteredSingleIncrementValidationResult.Cancelled();
                }

                if (current.Outcome != ObservationOutcome.Available)
                {
                    return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
                }

                if (current.Value == expectedIncrement)
                {
                    return DarkSoulsRemasteredSingleIncrementValidationResult.ObservedSingleIncrement();
                }

                if (current.Value != baseline.Value)
                {
                    return DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported();
                }
            }

            return DarkSoulsRemasteredSingleIncrementValidationResult.TimedOut();
        }
        finally
        {
            foreach (IDarkSoulsRemasteredProcessCandidate candidate in candidates)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static DarkSoulsRemasteredSingleIncrementValidationHarness CreateForManualQaRun() => new(
        new ExactNameDarkSoulsRemasteredProcessEnumerator(),
        new WindowsReadOnlyProcessAttachmentFactory(),
        new SystemValidationDelay());

    private static async ValueTask<Observation> ReadObservationAsync(
        IReadOnlyProcessAttachment attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            DarkSoulsRemasteredCandidateIdentityValidationResult firstIdentity = await DarkSoulsRemasteredCandidateIdentityValidator
                .ValidateAttachedAsync(attachment, cancellationToken)
                .ConfigureAwait(false);
            if (firstIdentity.Outcome == DarkSoulsRemasteredCandidateIdentityValidationOutcome.Cancelled)
            {
                return Observation.Cancelled();
            }

            if (firstIdentity.Outcome != DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate)
            {
                return Observation.Unsupported();
            }

            ReadOnlyMainModuleBaseResult moduleBase = await attachment.QueryMainModuleBaseAsync(cancellationToken).ConfigureAwait(false);
            if (moduleBase.Outcome == ReadOnlyMainModuleBaseOutcome.Cancelled)
            {
                return Observation.Cancelled();
            }

            if (moduleBase.Outcome != ReadOnlyMainModuleBaseOutcome.Available)
            {
                return Observation.Unsupported();
            }

            nuint pointerAddress = checked(moduleBase.BaseAddress + ModuleRelativePointerOffset);
            byte[] pointerBytes = new byte[PointerSize];
            ReadOnlyMemoryReadResult pointerRead = await attachment
                .ReadVirtualMemoryAsync(pointerAddress, pointerBytes, cancellationToken)
                .ConfigureAwait(false);
            if (pointerRead.Outcome == ReadOnlyMemoryReadOutcome.Cancelled)
            {
                return Observation.Cancelled();
            }

            if (pointerRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || pointerRead.BytesRead != (nuint)pointerBytes.Length)
            {
                return Observation.Unsupported();
            }

            nuint pointer = checked((nuint)BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes));
            if (pointer == 0)
            {
                return Observation.Unsupported();
            }

            DarkSoulsRemasteredCandidateIdentityValidationResult secondIdentity = await DarkSoulsRemasteredCandidateIdentityValidator
                .ValidateAttachedAsync(attachment, cancellationToken)
                .ConfigureAwait(false);
            if (secondIdentity.Outcome == DarkSoulsRemasteredCandidateIdentityValidationOutcome.Cancelled)
            {
                return Observation.Cancelled();
            }

            if (secondIdentity.Outcome != DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate)
            {
                return Observation.Unsupported();
            }

            nuint valueAddress = checked(pointer + FinalValueOffset);
            byte[] valueBytes = new byte[ValueSize];
            ReadOnlyMemoryReadResult valueRead = await attachment
                .ReadVirtualMemoryAsync(valueAddress, valueBytes, cancellationToken)
                .ConfigureAwait(false);
            if (valueRead.Outcome == ReadOnlyMemoryReadOutcome.Cancelled)
            {
                return Observation.Cancelled();
            }

            if (valueRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || valueRead.BytesRead != (nuint)valueBytes.Length)
            {
                return Observation.Unsupported();
            }

            int value = BinaryPrimitives.ReadInt32LittleEndian(valueBytes);
            return value is < 0 or > MaximumPlausibleValue
                ? Observation.Unsupported()
                : Observation.Available(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Observation.Cancelled();
        }
        catch (IOException)
        {
            return Observation.Unsupported();
        }
        catch (UnauthorizedAccessException)
        {
            return Observation.Unsupported();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Observation.Unsupported();
        }
        catch (InvalidOperationException)
        {
            return Observation.Unsupported();
        }
        catch (System.Security.SecurityException)
        {
            return Observation.Unsupported();
        }
        catch (OverflowException)
        {
            return Observation.Unsupported();
        }
    }

    private readonly record struct Observation(ObservationOutcome Outcome, int Value)
    {
        public static Observation Available(int value) => new(ObservationOutcome.Available, value);

        public static Observation Unsupported() => new(ObservationOutcome.Unsupported, 0);

        public static Observation Cancelled() => new(ObservationOutcome.Cancelled, 0);
    }

    private enum ObservationOutcome
    {
        Available,
        Unsupported,
        Cancelled,
    }
}

internal sealed class DarkSoulsRemasteredSingleIncrementValidationResult
{
    private DarkSoulsRemasteredSingleIncrementValidationResult(DarkSoulsRemasteredSingleIncrementValidationOutcome outcome)
    {
        Outcome = outcome;
    }

    public DarkSoulsRemasteredSingleIncrementValidationOutcome Outcome { get; }

    internal static DarkSoulsRemasteredSingleIncrementValidationResult ObservedSingleIncrement() =>
        new(DarkSoulsRemasteredSingleIncrementValidationOutcome.ObservedSingleIncrement);

    internal static DarkSoulsRemasteredSingleIncrementValidationResult TimedOut() =>
        new(DarkSoulsRemasteredSingleIncrementValidationOutcome.TimedOut);

    internal static DarkSoulsRemasteredSingleIncrementValidationResult Unsupported() =>
        new(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported);

    internal static DarkSoulsRemasteredSingleIncrementValidationResult Cancelled() =>
        new(DarkSoulsRemasteredSingleIncrementValidationOutcome.Cancelled);
}

internal enum DarkSoulsRemasteredSingleIncrementValidationOutcome
{
    ObservedSingleIncrement,
    TimedOut,
    Unsupported,
    Cancelled,
}

internal interface IValidationDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemValidationDelay : IValidationDelay
{
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
