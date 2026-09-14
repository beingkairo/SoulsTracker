using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

/// <summary>Owns persistence of game-specific save configuration changes.</summary>
internal sealed class SaveGameConfigurationWorkflow(SerializedTrackerCoordinator coordinator)
{
    public Task<PersistentTrackerState> SaveEldenRingAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetEldenRingSaveConfigurationAsync(configuration, cancellationToken);

    public Task<PersistentTrackerState> SaveWukongAsync(BlackMythWukongSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetBlackMythWukongSaveConfigurationAsync(configuration, cancellationToken);

    public Task<PersistentTrackerState> SaveLiesOfPAsync(LiesOfPSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetLiesOfPSaveConfigurationAsync(configuration, cancellationToken);
}
