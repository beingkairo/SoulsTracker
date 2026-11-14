using System.Buffers.Binary;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

public interface IBloodborneProcessEnumerator
{
    ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one disposable Bloodborne runtime process candidate.</summary>
public interface IBloodborneProcessCandidate : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>Confirms the active game identity without exposing its title to callers.</summary>
    ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken);
}

/// <summary>Finds the exact host process used by the validated Bloodborne reader.</summary>
public sealed class ExactNameBloodborneProcessEnumerator : IBloodborneProcessEnumerator
{
    // This identifier is an implementation-only attachment detail. It is never surfaced by the app.
    private const string HostProcessNameWithoutExtension = "shadPS4";
    private const string ExpectedTitleProductCode = "CUSA03173";
    private const string ExpectedTitleGameName = "Bloodborne";

    public ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<IBloodborneProcessCandidate>>(
            Array.ConvertAll(Process.GetProcessesByName(HostProcessNameWithoutExtension), static process => new Candidate(process)));
    }

    private sealed class Candidate(Process process) : IBloodborneProcessCandidate
    {
        public int ProcessId => process.Id;

        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(false);
            }

            try
            {
                string title = process.MainWindowTitle;
                bool matches = IsExpectedGuestTitle(title);
                return ValueTask.FromResult(matches && !cancellationToken.IsCancellationRequested);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return ValueTask.FromResult(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal static bool IsExpectedGuestTitle(string? value)
    {
        return value is not null &&
            ContainsExactTitleToken(value, ExpectedTitleProductCode) &&
            value.Contains(ExpectedTitleGameName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsExactTitleToken(string value, string token)
    {
        int index = value.IndexOf(token, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        int end = index + token.Length;
        return (index == 0 || !char.IsLetterOrDigit(value[index - 1])) &&
            (end == value.Length || !char.IsLetterOrDigit(value[end]));
    }
}

/// <summary>
/// Reads Bloodborne's validated, guest-addressed cumulative death total through
/// the read-only attachment primitive after the title-gated active-guest
/// verification. No other guest memory is inspected.
/// </summary>
public sealed class BloodborneActiveCharacterDeathReader : IRuntimeGameDeathReader
{
    private static readonly nuint CumulativeDeathAddress = unchecked((nuint)0x0000002080673B8UL);
    private const int ValueSize = sizeof(uint);
    private const uint MaximumPlausibleValue = 1_000_000;
    private readonly IBloodborneProcessEnumerator processEnumerator;
    private readonly IReadOnlyProcessAttachmentFactory attachmentFactory;

    public BloodborneActiveCharacterDeathReader(
        IBloodborneProcessEnumerator processEnumerator,
        IReadOnlyProcessAttachmentFactory attachmentFactory)
    {
        this.processEnumerator = processEnumerator ?? throw new ArgumentNullException(nameof(processEnumerator));
        this.attachmentFactory = attachmentFactory ?? throw new ArgumentNullException(nameof(attachmentFactory));
    }

    public GameId GameId => GameId.Bloodborne;

    public async ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || IntPtr.Size != sizeof(ulong))
        {
            return null;
        }

        IReadOnlyList<IBloodborneProcessCandidate> candidates;
        try
        {
            candidates = await processEnumerator.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false);
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

            if (!await candidates[0].IsExpectedGuestActiveAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            ReadOnlyProcessAttachmentResult attachmentResult = await attachmentFactory
                .AttachAsync(candidates[0].ProcessId, cancellationToken)
                .ConfigureAwait(false);
            if (attachmentResult.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || attachmentResult.Attachment is null)
            {
                return null;
            }

            await using IReadOnlyProcessAttachment attachment = attachmentResult.Attachment;
            byte[] valueBytes = new byte[ValueSize];
            ReadOnlyMemoryReadResult valueRead = await attachment.ReadVirtualMemoryAsync(
                CumulativeDeathAddress,
                valueBytes,
                cancellationToken).ConfigureAwait(false);
            if (valueRead.Outcome != ReadOnlyMemoryReadOutcome.Succeeded || valueRead.BytesRead != ValueSize)
            {
                // A loaded host with no readable validated value is a title/loading/no-character
                // state, not a zero-death character.
                return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            }

            uint value = BinaryPrimitives.ReadUInt32LittleEndian(valueBytes);
            if (value > MaximumPlausibleValue)
            {
                return RuntimeGameReadResult.WaitingForActiveCharacter(GameId);
            }

            RuntimeGameObservation observation = new(GameId, value, DateTimeOffset.UtcNow, GameId.Value);
            return value == 0
                ? RuntimeGameReadResult.NoDeathsRecorded(observation)
                : RuntimeGameReadResult.Synced(observation);
        }
        catch (Exception exception) when (IsExpectedUnavailable(exception, cancellationToken))
        {
            return null;
        }
        finally
        {
            foreach (IBloodborneProcessCandidate candidate in candidates)
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
