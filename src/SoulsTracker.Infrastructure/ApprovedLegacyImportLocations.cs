using System.Collections.ObjectModel;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Safe display labels for the only approved legacy import locations.</summary>
public enum LegacyImportSourceLabel
{
    SoulsTrackerLegacySettings,
    SoulslikeTrackerLegacySettings,
    DeathGambitLegacySettings,
    GambaDeckLegacySettings,
}

/// <summary>An opaque approved candidate handle. It never exposes a filesystem path.</summary>
public sealed class LegacyImportCandidate
{
    internal LegacyImportCandidate(LegacyImportSourceLabel label, string? approvedPath) { Label = label; ApprovedPath = approvedPath; }
    public LegacyImportCandidate(LegacyImportSourceLabel label) : this(label, null) { }
    public LegacyImportSourceLabel Label { get; }
    public string DisplayLabel => Label switch
    {
        LegacyImportSourceLabel.SoulsTrackerLegacySettings => "SoulsTracker legacy settings",
        LegacyImportSourceLabel.SoulslikeTrackerLegacySettings => "Soulslike Tracker legacy settings",
        LegacyImportSourceLabel.DeathGambitLegacySettings => "DeathGambit legacy settings",
        LegacyImportSourceLabel.GambaDeckLegacySettings => "GambaDeck legacy settings",
        _ => "Legacy settings",
    };
    internal string? ApprovedPath { get; }
}

/// <summary>Metadata-only approved-location discovery. It never reads candidate content.</summary>
public interface IApprovedLegacyImportLocationLocator
{
    IReadOnlyList<LegacyImportCandidate> Discover();
    bool IsStillApproved(LegacyImportCandidate candidate);
}

/// <summary>Preflights only an opaque approved handle after revalidating its boundary.</summary>
public interface IApprovedLegacyImportPreflight
{
    LegacyImportPreflightReview Prepare(LegacyImportCandidate candidate);
}

/// <summary>Safe review projection of a preflight result. It intentionally omits raw analysis and fingerprints.</summary>
public sealed class LegacyImportPreflightReview
{
    internal LegacyImportPreflightReview(LegacyImportPreflightOutcome outcome, string? selectedGameLabel, IReadOnlyList<string> recognizedBossNames, BossListVisibilityMode? bossListVisibilityMode, int warningCount, ConfirmedLegacyImportRequest? confirmedRequest)
    {
        Outcome = outcome;
        SelectedGameLabel = selectedGameLabel;
        RecognizedBossNames = recognizedBossNames;
        BossListVisibilityMode = bossListVisibilityMode;
        WarningCount = warningCount;
        ConfirmedRequest = confirmedRequest;
    }
    public LegacyImportPreflightOutcome Outcome { get; }
    public string? SelectedGameLabel { get; }
    public IReadOnlyList<string> RecognizedBossNames { get; }
    public BossListVisibilityMode? BossListVisibilityMode { get; }
    public int WarningCount { get; }
    /// <summary>Returns an opaque Application capability only when P6-02 prepared this review.</summary>
    public bool TryGetConfirmedRequest(out ConfirmedLegacyImportRequest? request)
    {
        request = ConfirmedRequest;
        return request is not null;
    }
    internal ConfirmedLegacyImportRequest? ConfirmedRequest { get; }
}

public sealed class ApprovedLegacyImportLocationLocator : IApprovedLegacyImportLocationLocator
{
    private static readonly (LegacyImportSourceLabel Label, string Directory)[] Locations =
    [
        (LegacyImportSourceLabel.SoulsTrackerLegacySettings, "SoulsTracker"),
        (LegacyImportSourceLabel.SoulslikeTrackerLegacySettings, "Soulslike Tracker"),
        (LegacyImportSourceLabel.DeathGambitLegacySettings, "DeathGambit"),
        (LegacyImportSourceLabel.GambaDeckLegacySettings, "GambaDeck"),
    ];
    private readonly IApprovedLegacyImportFileSystem fileSystem;

    public ApprovedLegacyImportLocationLocator() : this(new WindowsApprovedLegacyImportFileSystem()) { }
    internal ApprovedLegacyImportLocationLocator(IApprovedLegacyImportFileSystem fileSystem) => this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public IReadOnlyList<LegacyImportCandidate> Discover()
    {
        List<LegacyImportCandidate> candidates = [];
        foreach ((LegacyImportSourceLabel label, string directory) in Locations)
        {
            if (TryGetApprovedPath(directory, out string? path)) candidates.Add(new LegacyImportCandidate(label, path));
        }
        return new ReadOnlyCollection<LegacyImportCandidate>(candidates);
    }

    public bool IsStillApproved(LegacyImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        (LegacyImportSourceLabel Label, string Directory)? location = Locations.SingleOrDefault(item => item.Label == candidate.Label);
        if (location is null || !TryGetApprovedPath(location.Value.Directory, out string? currentPath)) return false;
        return candidate.ApprovedPath is not null && string.Equals(candidate.ApprovedPath, currentPath, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetApprovedPath(string directory, out string? candidatePath)
    {
        candidatePath = null;
        try
        {
            string root = fileSystem.GetFullPath(fileSystem.ApplicationDataRoot);
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            string parent = Path.Combine(root, directory);
            string path = fileSystem.GetFullPath(Path.Combine(parent, "state.json"));
            if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) return false;
            if (!IsOrdinaryDirectory(root) || !IsOrdinaryDirectory(parent) || !IsOrdinaryFile(path)) return false;
            candidatePath = path;
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private bool IsOrdinaryDirectory(string path)
    {
        FileAttributes attributes = fileSystem.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
    }

    private bool IsOrdinaryFile(string path)
    {
        FileAttributes attributes = fileSystem.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }
}

internal interface IApprovedLegacyImportFileSystem
{
    string ApplicationDataRoot { get; }
    string GetFullPath(string path);
    FileAttributes GetAttributes(string path);
}

internal sealed class WindowsApprovedLegacyImportFileSystem : IApprovedLegacyImportFileSystem
{
    public string ApplicationDataRoot => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string GetFullPath(string path) => Path.GetFullPath(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
}

/// <summary>Bridges opaque candidates to P6-02 without leaking their paths to Desktop.</summary>
public sealed class ApprovedLegacyImportPreflight : IApprovedLegacyImportPreflight
{
    private readonly IApprovedLegacyImportLocationLocator locator;
    private readonly LegacyImportPreflight preflight;
    public ApprovedLegacyImportPreflight(IApprovedLegacyImportLocationLocator locator, LegacyImportPreflight? preflight = null)
    {
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.preflight = preflight ?? new LegacyImportPreflight();
    }
    public LegacyImportPreflightReview Prepare(LegacyImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!locator.IsStillApproved(candidate) || candidate.ApprovedPath is null)
        {
            return new LegacyImportPreflightReview(LegacyImportPreflightOutcome.Unavailable, null, [], null, 0, null);
        }
        LegacyImportPreflightResult result = preflight.Prepare(candidate.ApprovedPath);
        LegacyImportProposal? proposal = result.Outcome == LegacyImportPreflightOutcome.Prepared ? result.Analysis?.Proposal : null;
        ConfirmedLegacyImportRequest? request = proposal is not null && result.Analysis is not null && result.SourceFingerprint is not null && result.BackupFingerprint is not null
            ? ConfirmedLegacyImportRequest.FromPreparedPreflight(result.Analysis, result.SourceFingerprint, result.BackupFingerprint)
            : null;
        string? gameLabel = proposal?.SelectedGameId is GameId gameId ? GameCatalog.GetRequired(gameId).DisplayName : null;
        IReadOnlyList<string> bossNames = proposal is null ? [] : proposal.DefeatedBossesByGame.SelectMany(pair => pair.Value.Select(boss => GameCatalog.GetRequiredBoss(pair.Key, boss).DisplayName)).ToArray();
        return new LegacyImportPreflightReview(result.Outcome, gameLabel, bossNames, proposal?.BossListVisibilityMode, result.Analysis?.Report.Issues.Count ?? 0, request);
    }
}

/// <summary>Infrastructure adapter for the Application-owned confirmed-commit port.</summary>
public sealed class SqliteConfirmedLegacyImportCommitter(SqliteTrackerStateRepository repository) : IConfirmedLegacyImportCommitter
{
    private readonly SqliteTrackerStateRepository repository = repository ?? throw new ArgumentNullException(nameof(repository));
    public async Task<Application.ConfirmedLegacyImportCommitOutcome> CommitAsync(LegacyProposalApplicationResult applicationResult, string sourceFingerprint, string backupFingerprint, CancellationToken cancellationToken = default)
    {
        var metadata = new ConfirmedLegacyImportAuditMetadata(ConfirmedLegacyImportAuditMetadata.CurrentContractVersion, LegacyImportPreflightOutcome.Prepared, sourceFingerprint, backupFingerprint);
        ConfirmedLegacyImportCommitResult result = await repository.CommitConfirmedLegacyImportAsync(applicationResult, metadata, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            ConfirmedLegacyImportCommitOutcome.Committed => Application.ConfirmedLegacyImportCommitOutcome.Committed,
            ConfirmedLegacyImportCommitOutcome.StorageUnavailable => Application.ConfirmedLegacyImportCommitOutcome.Unavailable,
            _ => Application.ConfirmedLegacyImportCommitOutcome.Refused,
        };
    }
}
