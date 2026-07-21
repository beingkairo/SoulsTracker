using System.Diagnostics;

namespace SoulsTracker.Infrastructure;

/// <summary>
/// Enumerates only processes whose executable name is exactly Elden Ring's
/// documented Windows executable name. Discovery alone does not authorize a
/// memory read or declare product support.
/// </summary>
public interface IEldenRingProcessEnumerator
{
    ValueTask<IReadOnlyList<IEldenRingProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one disposable Elden Ring process-discovery candidate.</summary>
public interface IEldenRingProcessCandidate : IAsyncDisposable
{
    int ProcessId { get; }
}

/// <summary>
/// Uses the exact <c>eldenring.exe</c> process name. It intentionally does
/// not attach to a process, read memory, or inspect save data.
/// </summary>
public sealed class ExactNameEldenRingProcessEnumerator : IEldenRingProcessEnumerator
{
    private const string ExecutableNameWithoutExtension = "eldenring";

    public ValueTask<IReadOnlyList<IEldenRingProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process[] processes = Process.GetProcessesByName(ExecutableNameWithoutExtension);
        return ValueTask.FromResult<IReadOnlyList<IEldenRingProcessCandidate>>(
            Array.ConvertAll(processes, static process => new Candidate(process)));
    }

    private sealed class Candidate(Process process) : IEldenRingProcessCandidate
    {
        public int ProcessId => process.Id;

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Validates an attached Elden Ring process against the strict executable and
/// product identity used by a future reader. This validation is not a reader
/// binding.
/// </summary>
public interface IEldenRingIdentityValidator
{
    ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken);
}

/// <summary>
/// Fails closed until a reader binding is separately verified through a live
/// character session. This is the only validator permitted for an unverified
/// executable version.
/// </summary>
public sealed class UnverifiedEldenRingIdentityValidator : IEldenRingIdentityValidator
{
    public ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}

/// <summary>
/// Requires Elden Ring's exact executable and product names, but deliberately
/// does not hard-lock file version or hash. A future reader must separately
/// prove its bounded read path and all value invariants before it can surface a
/// death total.
/// </summary>
public sealed class EldenRingProductIdentityValidator : IEldenRingIdentityValidator
{
    internal const string ExecutableFileName = "eldenring.exe";
    internal const string ProductName = "ELDEN RING™";

    public async ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        ReadOnlyModuleIdentityResult actual = await attachment.QueryMainModuleIdentityAsync(cancellationToken).ConfigureAwait(false);
        return actual.Outcome == ReadOnlyModuleIdentityOutcome.Available && actual.Identity is not null &&
            string.Equals(actual.Identity.ExecutableFileName, ExecutableFileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(actual.Identity.ProductName, ProductName, StringComparison.Ordinal);
    }
}
