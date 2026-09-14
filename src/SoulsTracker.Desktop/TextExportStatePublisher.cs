using System.Text;
using System.IO;
using System.Threading.Channels;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Best-effort local OBS Text-source writer; tracker commits never wait for file I/O.</summary>
internal sealed class TextExportStatePublisher : ITrackerStateChangePublisher, IAsyncDisposable
{
    private RuntimeGameObservation? runtimeObservation;
    private readonly Channel<(PersistentTrackerState State, long? Total)> writes = Channel.CreateUnbounded<(PersistentTrackerState, long?)>();
    private readonly Task worker;

    internal TextExportStatePublisher() => worker = ProcessWritesAsync();

    internal event EventHandler<bool>? WriteCompleted;

    public async Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    {
        if (notification.CommandType is TrackerCommandType.UpdateEldenRingSaveConfiguration or TrackerCommandType.UpdateLiesOfPSaveConfiguration)
        {
            Volatile.Write(ref runtimeObservation, null);
        }
        RuntimeGameObservation? observation = RuntimeObservationFor(notification.State, Volatile.Read(ref runtimeObservation));
        if (observation is null) Volatile.Write(ref runtimeObservation, null);
        long? displayedTotal = TotalDeathsDisplayProjection.Combine(notification.State, observation);
        await QueueWrite(notification.State, displayedTotal).ConfigureAwait(false);
    }

    internal async void PublishRuntimeObservation(PersistentTrackerState state, RuntimeGameReadResult? result)
    {
        RuntimeGameObservation? observation = result is { Observation: { } candidate }
            ? RuntimeObservationFor(state, candidate)
            : null;
        Volatile.Write(ref runtimeObservation, observation);
        await QueueWrite(state, TotalDeathsDisplayProjection.Combine(state, observation)).ConfigureAwait(false);
    }

    private static RuntimeGameObservation? RuntimeObservationFor(PersistentTrackerState state, RuntimeGameObservation? observation) =>
        observation?.GameId == state.SelectedGameId &&
        (state.SelectedGameId != GameId.BlackMythWukong || state.BlackMythWukongSave.LocalPath is not null) &&
        (state.SelectedGameId != GameId.LiesOfP || state.LiesOfPSave.LocalPath is not null)
            ? observation
            : null;

    private ValueTask QueueWrite(PersistentTrackerState state, long? displayedTotal) => writes.Writer.WriteAsync((state, displayedTotal));

    private async Task ProcessWritesAsync()
    {
        await foreach (var write in writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool succeeded = await WriteAsync(write.State, write.Total).ConfigureAwait(false);
            WriteCompleted?.Invoke(this, succeeded);
        }
    }

    public async ValueTask DisposeAsync()
    {
        writes.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal static Task<bool> WriteAsync(PersistentTrackerState state) => WriteAsync(state, displayedTotal: null);

    internal static async Task<bool> WriteAsync(PersistentTrackerState state, long? displayedTotal)
    {
        displayedTotal ??= TotalDeathsDisplayProjection.Combine(state, observation: null);
        TextExportConfiguration config = state.TextExports;
        bool succeeded = true;
        bool hasDisplayedDeathTotal = GameCatalog.GetRequired(state.SelectedGameId).TrackingMode == GameTrackingMode.ManualOnly || displayedTotal.HasValue;
        if (config.DeathsEnabled && config.DeathsPath is not null && hasDisplayedDeathTotal)
        {
            long total = displayedTotal ?? state.GetManualDeathCounter(state.SelectedGameId).Value;
            succeeded &= await AtomicWriteAsync(config.DeathsPath, $"Total Deaths: {total}").ConfigureAwait(false);
        }
        else if (config.DeathsEnabled && config.DeathsPath is not null &&
            (state.SelectedGameId == GameId.BlackMythWukong || state.SelectedGameId == GameId.LiesOfP))
        {
            succeeded &= await AtomicWriteAsync(config.DeathsPath, string.Empty).ConfigureAwait(false);
        }
        return succeeded;
    }

    internal static async Task<bool> AtomicWriteAsync(string selectedPath, string content)
    {
        string? temporary = null;
        try
        {
            string? directory = Path.GetDirectoryName(selectedPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            temporary = Path.Combine(directory, $".{Path.GetFileName(selectedPath)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
            File.Move(temporary, selectedPath, overwrite: true);
            return true;
        }
        catch { return false; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }
}
