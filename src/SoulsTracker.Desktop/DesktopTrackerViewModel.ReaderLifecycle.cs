using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{
    private long saveReaderSelectionEpoch;
    private long saveReaderResolutionVersion;
    private SaveReaderSourceResolution? saveReaderResolution;

    internal long SaveReaderResolutionVersion => saveReaderResolutionVersion;

    internal bool CanPublishSaveReaderResult(long resolutionVersion) =>
        !IsSaveReaderSourcePending && resolutionVersion == saveReaderResolutionVersion;

    internal bool IsSaveReaderSourcePending => saveReaderResolution is { } operation &&
        operation.Game == state?.SelectedGameId && operation.Epoch == saveReaderSelectionEpoch;

    private SaveReaderSourceResolution BeginSaveReaderSourceResolution(GameId game)
    {
        // Only the latest resolution owns readiness; an invalidated search must
        // neither block the current source nor complete a newer search.
        var operation = new SaveReaderSourceResolution(this, game, saveReaderSelectionEpoch);
        saveReaderResolution = operation;
        saveReaderResolutionVersion++;
        return operation;
    }

    private sealed class SaveReaderSourceResolution(DesktopTrackerViewModel owner, GameId game, long epoch) : IDisposable
    {
        internal GameId Game { get; } = game;
        internal long Epoch { get; } = epoch;

        public void Dispose()
        {
            if (owner.saveReaderResolution != this) return;
            owner.saveReaderResolution = null;
            if (Game == owner.state?.SelectedGameId && Epoch == owner.saveReaderSelectionEpoch)
                owner.saveReaderResolutionVersion++;
        }
    }
}
