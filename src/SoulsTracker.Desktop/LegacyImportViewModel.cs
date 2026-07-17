using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Passive desktop presenter for the two deliberate legacy-import actions.</summary>
public sealed class LegacyImportViewModel : INotifyPropertyChanged
{
    private readonly LegacyImportWorkflow workflow;
    private readonly Action<PersistentTrackerState> applyCommittedState;
    private bool offerVisible;
    private bool reviewVisible;
    private bool canImport;
    private string? statusMessage;
    private LegacyImportReviewResult? review;

    public LegacyImportViewModel(LegacyImportWorkflow workflow, Action<PersistentTrackerState> applyCommittedState)
    {
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.applyCommittedState = applyCommittedState ?? throw new ArgumentNullException(nameof(applyCommittedState));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<LegacyImportCandidate> Candidates { get; } = [];
    public bool OfferVisible { get => offerVisible; private set => SetField(ref offerVisible, value); }
    public bool ReviewVisible { get => reviewVisible; private set => SetField(ref reviewVisible, value); }
    public bool CanImport { get => canImport; private set => SetField(ref canImport, value); }
    public string? StatusMessage { get => statusMessage; private set => SetField(ref statusMessage, value); }
    public LegacyImportReviewResult? Review { get => review; private set => SetField(ref review, value); }

    public void OfferIfEligible(PersistentTrackerState state)
    {
        Candidates.Clear();
        foreach (LegacyImportCandidate candidate in workflow.OfferIfEligible(state)) Candidates.Add(candidate);
        OfferVisible = Candidates.Count > 0;
    }

    public async Task ReviewAsync(LegacyImportCandidate? candidate, CancellationToken cancellationToken = default)
    {
        LegacyImportReviewResult result = await workflow.ReviewAsync(candidate, cancellationToken);
        if (result.Outcome == LegacyImportPreflightOutcome.Unavailable && Candidates.Count > 1 && candidate is null)
        {
            StatusMessage = "Choose one legacy settings source to review.";
            return;
        }
        Review = result;
        ReviewVisible = true;
        OfferVisible = false;
        workflow.MarkReviewRendered();
        CanImport = result.Outcome == LegacyImportPreflightOutcome.Prepared;
        StatusMessage = result.Outcome == LegacyImportPreflightOutcome.Prepared
            ? "Review the recognized settings. Legacy global death counts are not imported. A same-directory safety backup was created."
            : "Legacy settings could not be prepared for import.";
    }

    public async Task ImportReviewedSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanImport) return;
        CanImport = false;
        ConfirmedLegacyImportExecutionResult result = await workflow.ImportReviewedSettingsAsync(cancellationToken);
        if (result.Status == ConfirmedLegacyImportExecutionStatus.Committed && result.CommittedState is not null) applyCommittedState(result.CommittedState);
        StatusMessage = result.Status == ConfirmedLegacyImportExecutionStatus.Committed
            ? "Reviewed legacy settings were imported."
            : "Reviewed legacy settings were not imported.";
    }

    public void Cancel() { workflow.Cancel(); OfferVisible = false; ReviewVisible = false; CanImport = false; StatusMessage = "Legacy import was cancelled."; }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true; }
}
