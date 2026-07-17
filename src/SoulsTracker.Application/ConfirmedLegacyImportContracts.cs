using SoulsTracker.Domain;

namespace SoulsTracker.Application;

/// <summary>Contains the opaque metadata required to commit one prepared import.</summary>
public sealed class ConfirmedLegacyImportRequest
{
    internal ConfirmedLegacyImportRequest(LegacyStateAnalysis analysis, string sourceFingerprint, string backupFingerprint)
    {
        Analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        SourceFingerprint = sourceFingerprint ?? throw new ArgumentNullException(nameof(sourceFingerprint));
        BackupFingerprint = backupFingerprint ?? throw new ArgumentNullException(nameof(backupFingerprint));
    }

    internal LegacyStateAnalysis Analysis { get; }
    internal string SourceFingerprint { get; }
    internal string BackupFingerprint { get; }

    internal static ConfirmedLegacyImportRequest FromPreparedPreflight(LegacyStateAnalysis analysis, string sourceFingerprint, string backupFingerprint) =>
        new(analysis, sourceFingerprint, backupFingerprint);
}

/// <summary>Application-owned port for the atomic P6-04 commit operation.</summary>
public interface IConfirmedLegacyImportCommitter
{
    Task<ConfirmedLegacyImportCommitOutcome> CommitAsync(
        LegacyProposalApplicationResult applicationResult,
        string sourceFingerprint,
        string backupFingerprint,
        CancellationToken cancellationToken = default);
}

/// <summary>Classifies a confirmed-import operation without source or storage detail.</summary>
public enum ConfirmedLegacyImportCommitOutcome
{
    Committed,
    Refused,
    Unavailable,
}

/// <summary>Safe result of the serialized final-confirmation action.</summary>
public sealed record ConfirmedLegacyImportExecutionResult(
    ConfirmedLegacyImportExecutionStatus Status,
    PersistentTrackerState? CommittedState,
    string? FailureMessage);

public enum ConfirmedLegacyImportExecutionStatus
{
    Committed,
    Refused,
    Unavailable,
    NotInitialized,
}
