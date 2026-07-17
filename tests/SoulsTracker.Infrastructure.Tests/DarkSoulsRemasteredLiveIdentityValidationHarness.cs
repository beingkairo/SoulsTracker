using System.Diagnostics;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

/// <summary>
/// Test-only, user-confirmed entry point for the separately coordinated offline
/// identity check. A missing or incorrect confirmation performs no process work.
/// </summary>
internal sealed class DarkSoulsRemasteredLiveIdentityValidationHarness
{
    internal const string ConfirmationEnvironmentVariable = "SOULSTRACKER_DSR_LIVE_IDENTITY_CONFIRM";
    internal const string RequiredConfirmation = "I_CONFIRM_OFFLINE_DARK_SOULS_REMASTERED_IS_OPEN";

    private readonly IDarkSoulsRemasteredProcessEnumerator processEnumerator;
    private readonly DarkSoulsRemasteredCandidateIdentityValidator identityValidator;

    internal DarkSoulsRemasteredLiveIdentityValidationHarness(
        IDarkSoulsRemasteredProcessEnumerator processEnumerator,
        DarkSoulsRemasteredCandidateIdentityValidator identityValidator)
    {
        this.processEnumerator = processEnumerator ?? throw new ArgumentNullException(nameof(processEnumerator));
        this.identityValidator = identityValidator ?? throw new ArgumentNullException(nameof(identityValidator));
    }

    internal async ValueTask<DarkSoulsRemasteredCandidateIdentityValidationResult?> RunIfConfirmedAsync(
        string? confirmation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmation, RequiredConfirmation, StringComparison.Ordinal))
        {
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
        }

        IReadOnlyList<IDarkSoulsRemasteredProcessCandidate> candidates;
        try
        {
            candidates = await processEnumerator.EnumerateExactCandidatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
        }
        catch (IOException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (UnauthorizedAccessException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (InvalidOperationException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (System.Security.SecurityException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }

        if (candidates is null)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }

        await using (new AsyncDisposalScope(candidates))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
            }

            if (candidates.Count != 1)
            {
                return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
            }

            return await identityValidator.ValidateAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static DarkSoulsRemasteredLiveIdentityValidationHarness CreateForManualQaRun() => new(
        new ExactNameDarkSoulsRemasteredProcessEnumerator(),
        new DarkSoulsRemasteredCandidateIdentityValidator(new WindowsReadOnlyProcessAttachmentFactory()));

    private sealed class AsyncDisposalScope : IAsyncDisposable
    {
        private readonly IReadOnlyList<IDarkSoulsRemasteredProcessCandidate> candidates;

        public AsyncDisposalScope(IReadOnlyList<IDarkSoulsRemasteredProcessCandidate> candidates)
        {
            this.candidates = candidates;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (IDarkSoulsRemasteredProcessCandidate candidate in candidates)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

internal interface IDarkSoulsRemasteredProcessEnumerator
{
    ValueTask<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> EnumerateExactCandidatesAsync(
        CancellationToken cancellationToken);
}

internal interface IDarkSoulsRemasteredProcessCandidate : IAsyncDisposable
{
    int ProcessId { get; }
}

/// <summary>
/// This enumeration is reachable only after the explicit per-run confirmation
/// is accepted by <see cref="DarkSoulsRemasteredLiveIdentityValidationHarness"/>.
/// </summary>
internal sealed class ExactNameDarkSoulsRemasteredProcessEnumerator : IDarkSoulsRemasteredProcessEnumerator
{
    private const string ExecutableNameWithoutExtension = "DarkSoulsRemastered";

    public ValueTask<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> EnumerateExactCandidatesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Process[] processes = Process.GetProcessesByName(ExecutableNameWithoutExtension);
        IDarkSoulsRemasteredProcessCandidate[] candidates = Array.ConvertAll(
            processes,
            static process => new ProcessCandidate(process));
        return ValueTask.FromResult<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>>(candidates);
    }

    private sealed class ProcessCandidate : IDarkSoulsRemasteredProcessCandidate
    {
        private readonly Process process;

        public ProcessCandidate(Process process)
        {
            this.process = process;
        }

        public int ProcessId => process.Id;

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
