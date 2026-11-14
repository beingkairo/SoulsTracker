using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Net.Http;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{

    public Task IncrementManualDeathsAsync(CancellationToken cancellationToken = default) =>
        !IsManualGameSelected || !ControlsEnabled
            ? Task.CompletedTask
            : SubmitManualCounterAsync(new IncrementManualDeathsCommand(), cancellationToken);

    public Task DecrementManualDeathsAsync(CancellationToken cancellationToken = default) =>
        !CanDecrementManualDeaths
            ? Task.CompletedTask
            : SubmitManualCounterAsync(new DecrementManualDeathsCommand(), cancellationToken);

    /// <summary>Routes the shared global increment binding to the active supported counter.</summary>
    public Task IncrementGlobalTrackedDeathsAsync(CancellationToken cancellationToken = default) =>
        IsManualGameSelected
            ? IncrementManualDeathsAsync(cancellationToken)
            : CanAdjustEldenRingMissedDeaths
                ? IncrementEldenRingMissedDeathsAsync(cancellationToken)
                : Task.CompletedTask;

    /// <summary>Routes the shared global decrement binding to the active supported counter.</summary>
    public Task DecrementGlobalTrackedDeathsAsync(CancellationToken cancellationToken = default) =>
        IsManualGameSelected
            ? DecrementManualDeathsAsync(cancellationToken)
            : CanDecrementEldenRingMissedDeaths
                ? DecrementEldenRingMissedDeathsAsync(cancellationToken)
                : Task.CompletedTask;

    public Task IncrementEldenRingMissedDeathsAsync(CancellationToken cancellationToken = default) =>
        !CanAdjustEldenRingMissedDeaths
            ? Task.CompletedTask
            : SubmitEldenRingMissedDeathsAsync(new AdjustEldenRingMissedDeathsCommand(Increment: true), cancellationToken);

    public Task DecrementEldenRingMissedDeathsAsync(CancellationToken cancellationToken = default) =>
        !CanDecrementEldenRingMissedDeaths
            ? Task.CompletedTask
            : SubmitEldenRingMissedDeathsAsync(new AdjustEldenRingMissedDeathsCommand(Increment: false), cancellationToken);

    private Task SubmitEldenRingMissedDeathsAsync(AdjustEldenRingMissedDeathsCommand command, CancellationToken cancellationToken) =>
        SubmitCounterAsync(command, () => EldenRingMissedDeaths,
            nameof(EldenRingMissedDeaths), () => CanDecrementEldenRingMissedDeaths,
            nameof(CanDecrementEldenRingMissedDeaths), cancellationToken);

    private Task SubmitOverlayPresentationAsync(
        bool totalDeathsEnabled,
        bool showGameName,
        CancellationToken cancellationToken) =>
        SubmitAsync(new UpdateOverlayPresentationCommand(totalDeathsEnabled, showGameName), cancellationToken);

    private Task SubmitManualCounterAsync(ITrackerCommand command, CancellationToken cancellationToken) =>
        SubmitCounterAsync(command, () => ManualDeaths,
            nameof(ManualDeaths), () => CanDecrementManualDeaths, nameof(CanDecrementManualDeaths), cancellationToken);

    private async Task SubmitCounterAsync(ITrackerCommand command,
        Func<long> getCount, string countProperty, Func<bool> canDecrement, string canDecrementProperty,
        CancellationToken cancellationToken, Func<bool>? canIncrement = null, string? canIncrementProperty = null)
    {
        ErrorMessage = null;
        try
        {
            TrackerCommandExecutionResult result = await coordinator.SubmitAsync(command, cancellationToken);
            if (result.CommittedState is not null)
            {
                long previousDeaths = getCount();
                bool previousCanDecrement = canDecrement();
                bool previousCanIncrement = canIncrement?.Invoke() ?? false;
                state = result.CommittedState;
                UpdateTotalDeathsText();
                if (getCount() != previousDeaths) OnPropertyChanged(countProperty);
                if (canDecrement() != previousCanDecrement) OnPropertyChanged(canDecrementProperty);
                if (canIncrement is not null && canIncrementProperty is not null && canIncrement() != previousCanIncrement)
                    OnPropertyChanged(canIncrementProperty);
            }
            if (result.Status is TrackerCommandExecutionStatus.SaveFailed or TrackerCommandExecutionStatus.NotInitialized)
                ErrorMessage = "SoulsTracker could not save the requested tracker change. The displayed state was not changed.";
            else if (result.Status == TrackerCommandExecutionStatus.DeliveryFailed)
                ErrorMessage = "The tracker change was saved, but a local update could not be delivered.";
        }
        catch
        {
            ErrorMessage = "SoulsTracker could not apply the requested tracker change. Try again.";
        }
    }

    private async Task SubmitAsync(ITrackerCommand command, CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            TrackerCommandExecutionResult result = await coordinator.SubmitAsync(command, cancellationToken);
            if (result.CommittedState is not null)
            {
                ApplyCommittedState(result.CommittedState);
            }

            if (result.Status is TrackerCommandExecutionStatus.SaveFailed or TrackerCommandExecutionStatus.NotInitialized)
            {
                ErrorMessage = "SoulsTracker could not save the requested tracker change. The displayed state was not changed.";
            }
            else if (result.Status == TrackerCommandExecutionStatus.DeliveryFailed)
            {
                ErrorMessage = "The tracker change was saved, but a local update could not be delivered.";
            }
        }
        catch
        {
            ErrorMessage = "SoulsTracker could not apply the requested tracker change. Try again.";
        }
        finally
        {
            IsBusy = false;
            NotifyTrackerProperties();
        }
    }
}
