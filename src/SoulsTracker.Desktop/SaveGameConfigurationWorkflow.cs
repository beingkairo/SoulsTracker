using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using System.IO;

namespace SoulsTracker.Desktop;

/// <summary>Owns persistence of game-specific save configuration changes.</summary>
internal sealed class SaveGameConfigurationWorkflow(SerializedTrackerCoordinator coordinator)
{
    internal readonly record struct SelectionValidationResult(bool IsValid, string Status);
    internal readonly record struct SelectionDiscoveryResult(IReadOnlyList<DiscoveredLocalSave> Candidates, SelectionValidationResult Validation);
    private long operationVersion;
    private long wukongDiscoveryVersion;
    private long liesSelectionVersion;
    private long wukongMetadataVersion;
    private long wukongSelectionVersion;

    // Versioning belongs to the configuration workflow because discovery and
    // selection are configuration operations, even though their results are
    // projected by the view model.
    public long BeginOperation() => Interlocked.Increment(ref operationVersion);

    public bool IsCurrentOperation(long version) =>
        version == Interlocked.Read(ref operationVersion);

    public void InvalidateOperations() => Interlocked.Increment(ref operationVersion);

    public long BeginWukongDiscovery() => Interlocked.Increment(ref wukongDiscoveryVersion);
    public bool IsCurrentWukongDiscovery(long version) => version == Interlocked.Read(ref wukongDiscoveryVersion);
    public long BeginLiesSelection() => Interlocked.Increment(ref liesSelectionVersion);
    public bool IsCurrentLiesSelection(long version) => version == Interlocked.Read(ref liesSelectionVersion);
    public long BeginWukongMetadataRead() => Interlocked.Increment(ref wukongMetadataVersion);
    public bool IsCurrentWukongMetadataRead(long version) => version == Interlocked.Read(ref wukongMetadataVersion);
    public void InvalidateWukongMetadataReads() => Interlocked.Increment(ref wukongMetadataVersion);
    public long BeginWukongSelection() => Interlocked.Increment(ref wukongSelectionVersion);
    public bool IsCurrentWukongSelection(long version) => version == Interlocked.Read(ref wukongSelectionVersion);
    public void InvalidateWukongSelections() => Interlocked.Increment(ref wukongSelectionVersion);

    public bool IsValidLiesOfPSave(string localPath)
    {
        _ = coordinator;
        return LiesOfPSaveConfiguration.IsCharacterSaveFileName(Path.GetFileName(localPath)) && LiesOfPSaveDiscovery.IsRegularBoundedSave(localPath);
    }

    public bool ValidateLiesOfPSelection(string localPath) =>
        IsValidLiesOfPSave(localPath);

    public static SelectionValidationResult ValidateLiesOfPSelectionResult(string localPath) =>
        LiesOfPSaveConfiguration.IsCharacterSaveFileName(Path.GetFileName(localPath)) && LiesOfPSaveDiscovery.IsRegularBoundedSave(localPath)
            ? new(true, string.Empty)
            : new(false, "Selected save is unavailable or unsupported.");

    public IReadOnlyList<DiscoveredLocalSave> DiscoverWukongSelection(string localPath)
    {
        _ = coordinator;
        return BlackMythWukongSaveDiscovery.DiscoverInSelectedFolder(localPath);
    }

    public IReadOnlyList<DiscoveredLocalSave> DiscoverLiesOfPSelection(string localPath)
    {
        _ = coordinator;
        return LiesOfPSaveDiscovery.DiscoverInSelectedFolder(localPath);
    }

    public SelectionDiscoveryResult DiscoverLiesOfPSelectionResult(string localPath) =>
        new(DiscoverLiesOfPSelection(localPath), ValidateLiesOfPSelectionResult(localPath));

    public bool IsLiesOfPConfiguredSaveReadable(string localPath)
    {
        _ = coordinator;
        return LiesOfPSaveDiscovery.IsRegularBoundedSave(localPath);
    }

    internal readonly record struct SaveDiscoveryResult(
        IReadOnlyList<DiscoveredLocalSave> Candidates,
        string? ConfiguredPath,
        bool ConfiguredPathExists);

    public static Task<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(ILocalSaveDiscovery discovery, CancellationToken cancellationToken) =>
        Task.Run(async () => await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false), cancellationToken);

    public static Task<IReadOnlyList<DiscoveredLocalSave>> DiscoverInSelectedFolderAsync(
        Func<string, IReadOnlyList<DiscoveredLocalSave>> discover,
        string localPath,
        CancellationToken cancellationToken) =>
        Task.Run(() => discover(localPath), cancellationToken);

    public static async Task<SaveDiscoveryResult> DiscoverWithConfiguredFallbackAsync(
        ILocalSaveDiscovery discovery,
        Func<string, IReadOnlyList<DiscoveredLocalSave>> discoverInSelectedFolder,
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DiscoveredLocalSave> candidates = await DiscoverAsync(discovery, cancellationToken).ConfigureAwait(false);
        bool configuredPathExists = configuredPath is not null && File.Exists(configuredPath);
        if (configuredPathExists && !candidates.Any(candidate => PathsEqual(candidate.LocalPath, configuredPath!)))
        {
            candidates = await DiscoverInSelectedFolderAsync(discoverInSelectedFolder, configuredPath!, cancellationToken).ConfigureAwait(false);
        }
        return new SaveDiscoveryResult(candidates, configuredPath, configuredPathExists);
    }

    public static bool IsConfiguredSelectionAvailable(string? configuredPath, bool configuredPathExists) =>
        configuredPath is not null && configuredPathExists;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
