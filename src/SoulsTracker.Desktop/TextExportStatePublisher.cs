using System.Text;
using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Best-effort local OBS Text-source writer; tracker commits never wait for file I/O.</summary>
internal sealed class TextExportStatePublisher : ITrackerStateChangePublisher
{
    private RuntimeGameObservation? runtimeObservation;

    internal event EventHandler<bool>? WriteCompleted;

    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    {
        RuntimeGameObservation? observation = RuntimeObservationFor(notification.State, Volatile.Read(ref runtimeObservation));
        if (observation is null) Volatile.Write(ref runtimeObservation, null);
        long? displayedTotal = observation?.TotalDeaths.Value;
        QueueWrite(notification.State, displayedTotal);
        return Task.CompletedTask;
    }

    internal void PublishRuntimeObservation(PersistentTrackerState state, RuntimeGameReadResult? result)
    {
        RuntimeGameObservation? observation = result is { Status: RuntimeGameReaderStatus.Synced, Observation: { } candidate }
            ? RuntimeObservationFor(state, candidate)
            : null;
        Volatile.Write(ref runtimeObservation, observation);
        QueueWrite(state, observation?.TotalDeaths.Value);
    }

    private static RuntimeGameObservation? RuntimeObservationFor(PersistentTrackerState state, RuntimeGameObservation? observation) =>
        observation?.GameId == state.SelectedGameId &&
        (state.SelectedGameId != GameId.BlackMythWukong || state.BlackMythWukongSave.LocalPath is not null)
            ? observation
            : null;

    private void QueueWrite(PersistentTrackerState state, long? displayedTotal) =>
        _ = Task.Run(async () => WriteCompleted?.Invoke(this, await WriteAsync(state, displayedTotal).ConfigureAwait(false)), CancellationToken.None);

    internal static Task<bool> WriteAsync(PersistentTrackerState state) => WriteAsync(state, displayedTotal: null);

    internal static async Task<bool> WriteAsync(PersistentTrackerState state, long? displayedTotal)
    {
        TextExportConfiguration config = state.TextExports;
        bool succeeded = true;
        bool hasDisplayedDeathTotal = GameCatalog.GetRequired(state.SelectedGameId).TrackingMode == GameTrackingMode.ManualOnly || displayedTotal.HasValue;
        if (config.DeathsEnabled && config.DeathsPath is not null && hasDisplayedDeathTotal)
        {
            long total = displayedTotal ?? state.GetManualDeathCounter(state.SelectedGameId).Value;
            succeeded &= await AtomicWriteAsync(config.DeathsPath, $"Total Deaths: {total}").ConfigureAwait(false);
        }
        else if (config.DeathsEnabled && config.DeathsPath is not null && state.SelectedGameId == GameId.BlackMythWukong)
        {
            succeeded &= await AtomicWriteAsync(config.DeathsPath, string.Empty).ConfigureAwait(false);
        }
        if (config.BossListEnabled && config.BossListPath is not null)
        {
            GameDefinition game = GameCatalog.GetRequired(state.SelectedGameId);
            IEnumerable<BossDefinition> bosses = BossCatalogDisplayFilter.Apply(game, state.BossListScope);
            string content = game.DisplayName + Environment.NewLine + string.Join(Environment.NewLine, bosses.Select(b => state.BossProgress.IsDefeated(game.Id, b.Id) ? $"[x] {b.DisplayName}" : $"[ ] {b.DisplayName}"));
            succeeded &= await AtomicWriteAsync(config.BossListPath, content).ConfigureAwait(false);
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
