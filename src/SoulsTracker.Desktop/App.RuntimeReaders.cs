using System.ComponentModel;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public partial class App
{
    private RuntimeGameReaderCoordinator? runtimeReaders;
    private EldenRingSaveDeathReader? eldenRingSaveReader;
    private BlackMythWukongSaveDeathReader? blackMythWukongSaveReader;
    private LiesOfPSaveDeathReader? liesOfPSaveReader;
    private HollowKnightRecordReader? hollowKnightReader;
    private CancellationTokenSource? runtimeReaderCancellation;
    private Task? runtimeReaderPollingTask;
    private readonly RuntimePublicationSession runtimePublication = new();

    private async Task PollRuntimeReadersAsync(DesktopTrackerViewModel viewModel, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await Dispatcher.InvokeAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PersistentTrackerState currentState = runtimePublication.CurrentState!;
                    var ticket = runtimePublication.BeginRead(currentState);
                    eldenRingSaveReader?.Configure(currentState.EldenRingSave,
                        currentState.SelectedGameId == GameId.EldenRing && runtimePublication.RequiresFreshSaveRead);
                    blackMythWukongSaveReader?.Configure(currentState.BlackMythWukongSave,
                        currentState.SelectedGameId == GameId.BlackMythWukong && runtimePublication.RequiresFreshSaveRead);
                    liesOfPSaveReader?.Configure(currentState.LiesOfPSave);
                    hollowKnightReader?.Configure(currentState.HollowKnight);
                    return (Ticket: ticket, currentState.SelectedGameId,
                        Ready: !viewModel.IsSaveReaderSourcePending, ResolutionVersion: viewModel.SaveReaderResolutionVersion,
                        SourceIdentity: EffectiveDeathTotalResult.SourceIdentityFor(currentState));
                });
                if (read.Ready)
                {
                    RuntimeGameReadResult? result = await runtimeReaders!
                        .PollAsync(read.SelectedGameId, cancellationToken, read.SourceIdentity)
                        .ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (mainWindowCloseRequested || !viewModel.CanPublishSaveReaderResult(read.ResolutionVersion)) return;
                        PersistentTrackerState currentState = viewModel.CurrentState!;
                        runtimePublication.CompleteRead(read.Ticket, currentState, result,
                            viewModel.ApplyRuntimeReaderResult, publication =>
                            {
                                textExportPublisher?.PublishRuntimeObservation(runtimePublication.CurrentState!, publication);
                                hostedConnection?.PublishAccepted(runtimePublication.CurrentState!, publication);
                            }, cancellationToken);
                    });
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
