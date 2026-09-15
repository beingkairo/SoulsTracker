using System.Threading.Channels;
using SoulsTracker.Domain;

namespace SoulsTracker.Application;

public sealed class SerializedTrackerCoordinator : IAsyncDisposable
{
    private readonly ITrackerStateRepository repository;
    private readonly ITrackerStateChangePublisher publisher;
    private readonly IConfirmedLegacyImportCommitter? confirmedImportCommitter;
    private readonly Channel<CoordinatorRequest> requests = Channel.CreateUnbounded<CoordinatorRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task processor;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private PersistentTrackerState? committedState;
    private bool initialized;

    public SerializedTrackerCoordinator(ITrackerStateRepository repository, ITrackerStateChangePublisher publisher, IConfirmedLegacyImportCommitter? confirmedImportCommitter = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.confirmedImportCommitter = confirmedImportCommitter;
        processor = ProcessAsync();
    }

    /// <summary>Queues the separately confirmed import against the latest committed state.</summary>
    public Task<ConfirmedLegacyImportExecutionResult> ImportReviewedLegacySettingsAsync(ConfirmedLegacyImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var coordinatorRequest = new LegacyImportRequest(request, cancellationToken);
        if (!requests.Writer.TryWrite(coordinatorRequest)) ObjectDisposedException.ThrowIf(true, this);
        return coordinatorRequest.Completion.Task;
    }

    public async Task<TrackerStateLoadResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return TrackerStateLoadResult.Loaded(committedState!);
            TrackerStateLoadResult result = await repository.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess) { committedState = result.State; initialized = true; }
            return result;
        }
        finally { mutationGate.Release(); }
    }

    public Task<TrackerCommandExecutionResult> SubmitAsync(ITrackerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = new CommandRequest(command, cancellationToken);
        if (!requests.Writer.TryWrite(request))
        {
            ObjectDisposedException.ThrowIf(true, this);
        }
        return request.Completion.Task;
    }

    /// <summary>Persists the assigned loopback endpoint through the same serialized commit path.</summary>
    public Task<PersistentTrackerState> SetOverlayEndpointAsync(OverlayEndpointConfiguration endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var request = new EndpointRequest(endpoint, cancellationToken);
        if (!requests.Writer.TryWrite(request)) ObjectDisposedException.ThrowIf(true, this);
        return request.Completion.Task;
    }
    public Task<PersistentTrackerState> SetGlobalHotkeysAsync(GlobalHotkeyConfiguration hotkeys, CancellationToken cancellationToken = default)
    {
        var request = new HotkeyRequest(hotkeys, cancellationToken); if (!requests.Writer.TryWrite(request)) ObjectDisposedException.ThrowIf(true, this); return request.Completion.Task;
    }

    public Task<PersistentTrackerState> SetTextExportConfigurationAsync(TextExportConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var request = new TextExportRequest(configuration, cancellationToken); if (!requests.Writer.TryWrite(request)) ObjectDisposedException.ThrowIf(true, this); return request.Completion.Task;
    }
    public Task<PersistentTrackerState> SetEldenRingSaveConfigurationAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var request = new EldenRingSaveRequest(configuration, cancellationToken); if (!requests.Writer.TryWrite(request)) ObjectDisposedException.ThrowIf(true, this); return request.Completion.Task;
    }
    public Task<PersistentTrackerState> SetBlackMythWukongSaveConfigurationAsync(BlackMythWukongSaveConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var request = new BlackMythWukongSaveRequest(configuration, cancellationToken); if (!requests.Writer.TryWrite(request)) ObjectDisposedException.ThrowIf(true, this); return request.Completion.Task;
    }
    public Task<PersistentTrackerState> SetLiesOfPSaveConfigurationAsync(LiesOfPSaveConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var request = new LiesOfPSaveRequest(configuration, cancellationToken); if (!requests.Writer.TryWrite(request)) ObjectDisposedException.ThrowIf(true, this); return request.Completion.Task;
    }

    private async Task ProcessAsync()
    {
        await foreach (CoordinatorRequest request in requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
            if (!initialized) { request.RejectNotInitialized(); continue; }
            if (request is EndpointRequest endpointRequest)
            {
                // Endpoint configuration is startup-only and does not change the overlay snapshot, so this branch intentionally does not publish.
                try
                {
                    PersistentTrackerState updated = WithEndpoint(committedState!, endpointRequest.Endpoint);
                    if (!ReferenceEquals(updated, committedState)) await repository.SaveAsync(updated, endpointRequest.CancellationToken).ConfigureAwait(false);
                    committedState = updated;
                    endpointRequest.Completion.TrySetResult(committedState);
                }
                catch (OperationCanceledException) { endpointRequest.Completion.TrySetCanceled(endpointRequest.CancellationToken); }
                catch (Exception) { endpointRequest.Completion.TrySetException(new InvalidOperationException("The local overlay endpoint could not be saved.")); }
                continue;
            }
            if (request is HotkeyRequest hotkeyRequest)
            {
                // Manual hotkeys affect command input only and do not change overlay/TXT output, so this branch intentionally does not publish.
                try
                {
                    PersistentTrackerState current = committedState!;
                    PersistentTrackerState updated = new(
                        current.SchemaVersion,
                        current.SelectedGameId,
                        current.OverlayConfiguration,
                        hotkeyRequest.Hotkeys,

                        current.TextExports, current.ManualDemonsSoulsDeathCounter, current.EldenRingNoticeAcknowledged, current.EldenRingSave, current.BlackMythWukongSave, current.EldenRingMissedDeathAdjustments, current.LiesOfPSave);
                    await repository.SaveAsync(updated, hotkeyRequest.CancellationToken).ConfigureAwait(false);
                    committedState = updated;
                    hotkeyRequest.Completion.TrySetResult(updated);
                }
                catch (OperationCanceledException) { hotkeyRequest.Completion.TrySetCanceled(hotkeyRequest.CancellationToken); }
                catch
                {
                    hotkeyRequest.Completion.TrySetException(new InvalidOperationException("The manual hotkeys could not be saved."));
                }

                continue;
            }

            if (request is TextExportRequest exportRequest)
            {
                try { PersistentTrackerState current = committedState!; PersistentTrackerState updated = new(current.SchemaVersion, current.SelectedGameId, current.OverlayConfiguration, current.GlobalHotkeys, exportRequest.Configuration, current.ManualDemonsSoulsDeathCounter, current.EldenRingNoticeAcknowledged, current.EldenRingSave, current.BlackMythWukongSave, current.EldenRingMissedDeathAdjustments, current.LiesOfPSave); await repository.SaveAsync(updated, exportRequest.CancellationToken).ConfigureAwait(false); committedState = updated; await publisher.PublishAsync(new TrackerStateChanged(updated, TrackerCommandType.UpdateTextExports), exportRequest.CancellationToken).ConfigureAwait(false); exportRequest.Completion.TrySetResult(updated); }
                catch { exportRequest.Completion.TrySetException(new InvalidOperationException("The text export settings could not be saved.")); }
                continue;
            }
            if (request is EldenRingSaveRequest eldenRingSaveRequest)
            {
                try { PersistentTrackerState current = committedState!; PersistentTrackerState updated = new(current.SchemaVersion, current.SelectedGameId, current.OverlayConfiguration, current.GlobalHotkeys, current.TextExports, current.ManualDemonsSoulsDeathCounter, current.EldenRingNoticeAcknowledged, eldenRingSaveRequest.Configuration, current.BlackMythWukongSave, current.EldenRingMissedDeathAdjustments, current.LiesOfPSave); await repository.SaveAsync(updated, eldenRingSaveRequest.CancellationToken).ConfigureAwait(false); committedState = updated; await publisher.PublishAsync(new TrackerStateChanged(updated, TrackerCommandType.UpdateEldenRingSaveConfiguration), eldenRingSaveRequest.CancellationToken).ConfigureAwait(false); eldenRingSaveRequest.Completion.TrySetResult(updated); }
                catch { eldenRingSaveRequest.Completion.TrySetException(new InvalidOperationException("The Elden Ring save selection could not be saved.")); }
                continue;
            }
            if (request is BlackMythWukongSaveRequest blackMythWukongSaveRequest)
            {
                try { PersistentTrackerState current = committedState!; PersistentTrackerState updated = new(current.SchemaVersion, current.SelectedGameId, current.OverlayConfiguration, current.GlobalHotkeys, current.TextExports, current.ManualDemonsSoulsDeathCounter, current.EldenRingNoticeAcknowledged, current.EldenRingSave, blackMythWukongSaveRequest.Configuration, current.EldenRingMissedDeathAdjustments, current.LiesOfPSave); await repository.SaveAsync(updated, blackMythWukongSaveRequest.CancellationToken).ConfigureAwait(false); committedState = updated; await publisher.PublishAsync(new TrackerStateChanged(updated, TrackerCommandType.UpdateBlackMythWukongSaveConfiguration), blackMythWukongSaveRequest.CancellationToken).ConfigureAwait(false); blackMythWukongSaveRequest.Completion.TrySetResult(updated); }
                catch { blackMythWukongSaveRequest.Completion.TrySetException(new InvalidOperationException("The Black Myth: Wukong save selection could not be saved.")); }
                continue;
            }
            if (request is LiesOfPSaveRequest liesOfPSaveRequest)
            {
                try { PersistentTrackerState current = committedState!; PersistentTrackerState updated = new(current.SchemaVersion, current.SelectedGameId, current.OverlayConfiguration, current.GlobalHotkeys, current.TextExports, current.ManualDemonsSoulsDeathCounter, current.EldenRingNoticeAcknowledged, current.EldenRingSave, current.BlackMythWukongSave, current.EldenRingMissedDeathAdjustments, liesOfPSaveRequest.Configuration); await repository.SaveAsync(updated, liesOfPSaveRequest.CancellationToken).ConfigureAwait(false); committedState = updated; await publisher.PublishAsync(new TrackerStateChanged(updated, TrackerCommandType.UpdateLiesOfPSaveConfiguration), liesOfPSaveRequest.CancellationToken).ConfigureAwait(false); liesOfPSaveRequest.Completion.TrySetResult(updated); }
                catch { liesOfPSaveRequest.Completion.TrySetException(new InvalidOperationException("The Lies of P save selection could not be saved.")); }
                continue;
            }
            if (request is LegacyImportRequest importRequest)
            {
                await ProcessLegacyImportAsync(importRequest).ConfigureAwait(false);
                continue;
            }
            CommandRequest commandRequest = (CommandRequest)request;
            try
            {
                TrackerTransitionResult transition = TrackerStateTransitionService.Apply(committedState!, commandRequest.Command);
                if (!transition.StateChanged) { commandRequest.Completion.TrySetResult(new(TrackerCommandExecutionStatus.NoChange, committedState, null)); continue; }
                try { await repository.SaveAsync(transition.State, commandRequest.CancellationToken).ConfigureAwait(false); }
                catch (Exception) { commandRequest.Completion.TrySetResult(new(TrackerCommandExecutionStatus.SaveFailed, committedState, "The tracker state could not be saved. No change was committed.")); continue; }
                committedState = transition.State;
                try { await publisher.PublishAsync(new TrackerStateChanged(committedState, transition.CommandType), commandRequest.CancellationToken).ConfigureAwait(false); commandRequest.Completion.TrySetResult(new(TrackerCommandExecutionStatus.Applied, committedState, null)); }
                catch (OperationCanceledException) { commandRequest.Completion.TrySetResult(new(TrackerCommandExecutionStatus.Applied, committedState, "The tracker state was saved, but update delivery was canceled.")); }
                catch (Exception) { commandRequest.Completion.TrySetResult(new(TrackerCommandExecutionStatus.DeliveryFailed, committedState, "The tracker state was saved, but the update could not be delivered.")); }
            }
            catch (Exception ex) { commandRequest.Completion.TrySetException(ex); }
            }
            finally { mutationGate.Release(); }
            }
    }

    private async Task ProcessLegacyImportAsync(LegacyImportRequest request)
    {
        if (confirmedImportCommitter is null)
        {
            request.Completion.TrySetResult(new(ConfirmedLegacyImportExecutionStatus.Unavailable, committedState, "Legacy import is unavailable."));
            return;
        }

        LegacyProposalApplicationResult applicationResult = ConfirmedLegacyProposalApplication.Apply(request.Import.Analysis, committedState!);
        if (applicationResult.Outcome != LegacyProposalApplicationOutcome.Applied || applicationResult.CandidateState is null)
        {
            request.Completion.TrySetResult(new(ConfirmedLegacyImportExecutionStatus.Refused, committedState, "The reviewed import could not be applied to the current tracker state."));
            return;
        }

        ConfirmedLegacyImportCommitOutcome outcome;
        try
        {
            outcome = await confirmedImportCommitter.CommitAsync(applicationResult, request.Import.SourceFingerprint, request.Import.BackupFingerprint, request.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            outcome = ConfirmedLegacyImportCommitOutcome.Unavailable;
        }

        if (outcome != ConfirmedLegacyImportCommitOutcome.Committed)
        {
            request.Completion.TrySetResult(new(outcome == ConfirmedLegacyImportCommitOutcome.Refused ? ConfirmedLegacyImportExecutionStatus.Refused : ConfirmedLegacyImportExecutionStatus.Unavailable, committedState, "The reviewed import was not committed."));
            return;
        }

        committedState = applicationResult.CandidateState;
        try
        {
            await publisher.PublishAsync(new TrackerStateChanged(committedState, TrackerCommandType.LegacyImport), request.CancellationToken).ConfigureAwait(false);
            request.Completion.TrySetResult(new(ConfirmedLegacyImportExecutionStatus.Committed, committedState, null));
        }
        catch (Exception)
        {
            request.Completion.TrySetResult(new(ConfirmedLegacyImportExecutionStatus.Committed, committedState, "The import was saved, but a local update could not be delivered."));
        }
    }

    public async ValueTask DisposeAsync() { requests.Writer.TryComplete(); await processor.ConfigureAwait(false); await repository.DisposeAsync().ConfigureAwait(false); }
    private static PersistentTrackerState WithEndpoint(PersistentTrackerState state, OverlayEndpointConfiguration endpoint) =>
        state.OverlayConfiguration.Endpoint.Equals(endpoint) ? state : new PersistentTrackerState(state.SchemaVersion, state.SelectedGameId, new OverlayConfiguration(state.OverlayConfiguration.SchemaVersion, endpoint, state.OverlayConfiguration.TotalDeaths), state.GlobalHotkeys, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave, state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave);

    private abstract class CoordinatorRequest(CancellationToken cancellationToken) { public CancellationToken CancellationToken { get; } = cancellationToken; public abstract void RejectNotInitialized(); }
    private sealed class CommandRequest(ITrackerCommand command, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public ITrackerCommand Command { get; } = command; public TaskCompletionSource<TrackerCommandExecutionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetResult(new(TrackerCommandExecutionStatus.NotInitialized, null, "Tracker state has not loaded.")); }
    private sealed class EndpointRequest(OverlayEndpointConfiguration endpoint, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public OverlayEndpointConfiguration Endpoint { get; } = endpoint; public TaskCompletionSource<PersistentTrackerState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetException(new InvalidOperationException("Tracker state has not loaded.")); }
    private sealed class HotkeyRequest(GlobalHotkeyConfiguration hotkeys, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public GlobalHotkeyConfiguration Hotkeys { get; } = hotkeys; public TaskCompletionSource<PersistentTrackerState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetException(new InvalidOperationException("Tracker state has not loaded.")); }

    private sealed class TextExportRequest(TextExportConfiguration configuration, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public TextExportConfiguration Configuration { get; } = configuration; public TaskCompletionSource<PersistentTrackerState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetException(new InvalidOperationException("Tracker state has not loaded.")); }
    private sealed class EldenRingSaveRequest(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public EldenRingSaveConfiguration Configuration { get; } = configuration; public TaskCompletionSource<PersistentTrackerState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetException(new InvalidOperationException("Tracker state has not loaded.")); }
    private sealed class BlackMythWukongSaveRequest(BlackMythWukongSaveConfiguration configuration, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public BlackMythWukongSaveConfiguration Configuration { get; } = configuration; public TaskCompletionSource<PersistentTrackerState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetException(new InvalidOperationException("Tracker state has not loaded.")); }
    private sealed class LiesOfPSaveRequest(LiesOfPSaveConfiguration configuration, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public LiesOfPSaveConfiguration Configuration { get; } = configuration; public TaskCompletionSource<PersistentTrackerState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetException(new InvalidOperationException("Tracker state has not loaded.")); }
    private sealed class LegacyImportRequest(ConfirmedLegacyImportRequest import, CancellationToken cancellationToken) : CoordinatorRequest(cancellationToken) { public ConfirmedLegacyImportRequest Import { get; } = import; public TaskCompletionSource<ConfirmedLegacyImportExecutionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public override void RejectNotInitialized() => Completion.TrySetResult(new(ConfirmedLegacyImportExecutionStatus.NotInitialized, null, "Tracker state has not loaded.")); }
}
