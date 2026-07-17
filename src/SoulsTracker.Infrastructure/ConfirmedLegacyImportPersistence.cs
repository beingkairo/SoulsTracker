namespace SoulsTracker.Infrastructure;

/// <summary>
/// Supplies the minimum opaque provenance retained for a confirmed legacy
/// import. It intentionally has no source location or source content field.
/// </summary>
public sealed class ConfirmedLegacyImportAuditMetadata
{
    /// <summary>The only P6 analyzer/preflight contract revision currently accepted.</summary>
    public const int CurrentContractVersion = 1;

    public ConfirmedLegacyImportAuditMetadata(
        int contractVersion,
        LegacyImportPreflightOutcome preflightOutcome,
        string sourceFingerprint,
        string backupFingerprint)
    {
        ContractVersion = contractVersion;
        PreflightOutcome = preflightOutcome;
        SourceFingerprint = sourceFingerprint;
        BackupFingerprint = backupFingerprint;
    }

    public int ContractVersion { get; }

    public LegacyImportPreflightOutcome PreflightOutcome { get; }

    public string SourceFingerprint { get; }

    public string BackupFingerprint { get; }

    internal bool IsValid =>
        ContractVersion == CurrentContractVersion &&
        PreflightOutcome == LegacyImportPreflightOutcome.Prepared &&
        IsFingerprint(SourceFingerprint) &&
        IsFingerprint(BackupFingerprint);

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            (character >= '0' && character <= '9') ||
            (character >= 'A' && character <= 'F'));
}

/// <summary>
/// Provides a secret-safe outcome for one confirmed legacy-import persistence
/// attempt. It deliberately exposes neither the state nor audit provenance.
/// </summary>
public sealed class ConfirmedLegacyImportCommitResult
{
    private ConfirmedLegacyImportCommitResult(
        ConfirmedLegacyImportCommitOutcome outcome,
        string? importId,
        DateTimeOffset? committedAtUtc)
    {
        Outcome = outcome;
        ImportId = importId;
        CommittedAtUtc = committedAtUtc;
    }

    public ConfirmedLegacyImportCommitOutcome Outcome { get; }

    /// <summary>Gets a generated opaque identifier only for committed imports.</summary>
    public string? ImportId { get; }

    /// <summary>Gets the generated UTC commit timestamp only for committed imports.</summary>
    public DateTimeOffset? CommittedAtUtc { get; }

    internal static ConfirmedLegacyImportCommitResult Committed(string importId, DateTimeOffset committedAtUtc) =>
        new(ConfirmedLegacyImportCommitOutcome.Committed, importId, committedAtUtc);

    internal static ConfirmedLegacyImportCommitResult Refused(ConfirmedLegacyImportCommitOutcome outcome) =>
        new(outcome, null, null);
}

/// <summary>Classifies a confirmed-import attempt without filesystem or exception detail.</summary>
public enum ConfirmedLegacyImportCommitOutcome
{
    Committed,
    InvalidCandidate,
    InvalidAuditMetadata,
    DestinationHasSelectedGame,
    DestinationHasDefeatedBossProgress,
    DestinationHasManualBloodborneDeaths,
    StorageUnavailable,
}
