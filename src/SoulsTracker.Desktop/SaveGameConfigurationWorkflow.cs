using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Owns persistence of game-specific save configuration changes.</summary>
internal sealed class SaveGameConfigurationWorkflow(SerializedTrackerCoordinator coordinator)
{
    public static Task<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(ILocalSaveDiscovery discovery, CancellationToken cancellationToken) =>
        Task.Run(async () => await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false), cancellationToken);

    public static Task<IReadOnlyList<DiscoveredLocalSave>> DiscoverInSelectedFolderAsync(
        Func<string, IReadOnlyList<DiscoveredLocalSave>> discover,
        string localPath,
        CancellationToken cancellationToken) =>
        Task.Run(() => discover(localPath), cancellationToken);

    public static Task<IReadOnlyList<EldenRingProfileSlotChoice>> ReadEldenRingProfileSlotsAsync(
        IEldenRingSaveProfileReader reader,
        string localPath,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<EldenRingCharacterSlotMetadata> metadata = await reader.ReadAsync(
                    new EldenRingSaveConfiguration(localPath, EldenRingSaveConfiguration.NoSlotIndex), cancellationToken).ConfigureAwait(false);
                return (IReadOnlyList<EldenRingProfileSlotChoice>)metadata
                    .Where(static item => !item.IsEmpty)
                    .Select(EldenRingProfileSlotChoice.FromMetadata)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<EldenRingProfileSlotChoice>();
            }
        }, cancellationToken);

    public Task<PersistentTrackerState> SaveEldenRingAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetEldenRingSaveConfigurationAsync(configuration, cancellationToken);

    public Task<PersistentTrackerState> SaveWukongAsync(BlackMythWukongSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetBlackMythWukongSaveConfigurationAsync(configuration, cancellationToken);

    public Task<PersistentTrackerState> SaveLiesOfPAsync(LiesOfPSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetLiesOfPSaveConfigurationAsync(configuration, cancellationToken);
}
