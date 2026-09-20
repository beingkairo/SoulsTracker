using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using System.IO;
using System.Collections.Immutable;

namespace SoulsTracker.Desktop;

/// <summary>Owns persistence of game-specific save configuration changes.</summary>
internal sealed class SaveGameConfigurationWorkflow(SerializedTrackerCoordinator coordinator)
{
    internal readonly record struct SelectionValidationResult(bool IsValid, string Status);
    internal readonly record struct SelectionDiscoveryResult(IReadOnlyList<DiscoveredLocalSave> Candidates, SelectionValidationResult Validation);
    internal sealed record LiesSelectionOutcome(
        PersistentTrackerState? CommittedState,
        ImmutableArray<DiscoveredLocalSave>? Candidates,
        DiscoveredLocalSave? SelectedChoice,
        LocalSaveSourceState SourceState,
        string Status,
        string? Error,
        bool ExitChangeMode);
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

    public async Task<LiesSelectionOutcome?> BrowseLiesOfPSaveAsync(string localPath, CancellationToken cancellationToken)
    {
        long version = BeginLiesSelection();
        SelectionValidationResult validation = ValidateLiesOfPSelectionResult(localPath);
        if (!validation.IsValid)
            return new(null, null, null, LocalSaveSourceState.UnavailableSelection, validation.Status, null, false);
        IReadOnlyList<DiscoveredLocalSave> candidates = await DiscoverInSelectedFolderAsync(LiesOfPSaveDiscovery.DiscoverInSelectedFolder, localPath, cancellationToken).ConfigureAwait(false);
        if (!IsCurrentLiesSelection(version)) return null;
        try
        {
            PersistentTrackerState state = await SaveLiesOfPAsync(new LiesOfPSaveConfiguration(localPath), cancellationToken).ConfigureAwait(false);
            if (!IsCurrentLiesSelection(version)) return null;
            DiscoveredLocalSave? selected = candidates.SingleOrDefault(candidate => PathsEqual(candidate.LocalPath, localPath));
            return new(state, [.. candidates], selected, LocalSaveSourceState.CustomSelection, CustomSaveStatus(localPath), null, true);
        }
        catch { return new(null, [.. candidates], null, LocalSaveSourceState.UnavailableSelection, string.Empty, "The Lies of P save selection could not be saved.", false); }
    }

    public async Task<LiesSelectionOutcome?> SelectLiesOfPSaveAsync(DiscoveredLocalSave choice, CancellationToken cancellationToken)
    {
        long version = BeginLiesSelection();
        try
        {
            PersistentTrackerState state = await SaveLiesOfPAsync(new LiesOfPSaveConfiguration(choice.LocalPath), cancellationToken).ConfigureAwait(false);
            return IsCurrentLiesSelection(version)
                ? new(state, null, choice, LocalSaveSourceState.PersistedDiscovered, choice.Label, null, true)
                : null;
        }
        catch { return new(null, null, null, LocalSaveSourceState.UnavailableSelection, string.Empty, "The Lies of P save selection could not be saved.", false); }
    }

    public async Task<LiesSelectionOutcome?> RescanLiesOfPSavesAsync(string? configured, ILocalSaveDiscovery discovery, CancellationToken cancellationToken)
    {
        long version = BeginLiesSelection();
        try
        {
            SaveDiscoveryResult result = await DiscoverWithConfiguredFallbackAsync(discovery, LiesOfPSaveDiscovery.DiscoverInSelectedFolder, configured, cancellationToken).ConfigureAwait(false);
            if (!IsCurrentLiesSelection(version)) return null;
            ImmutableArray<DiscoveredLocalSave> candidates = [.. result.Candidates];
            DiscoveredLocalSave? selected = candidates.SingleOrDefault(candidate => PathsEqual(candidate.LocalPath, configured ?? string.Empty));
            if (configured is not null && !result.ConfiguredPathExists) return new(null, candidates, null, LocalSaveSourceState.UnavailableSelection, "Selected save is unavailable.", null, false);
            if (selected is not null) return new(null, candidates, selected, LocalSaveSourceState.PersistedDiscovered, selected.Label, null, false);
            if (configured is null && candidates.Length == 1)
            {
                PersistentTrackerState state = await SaveLiesOfPAsync(new LiesOfPSaveConfiguration(candidates[0].LocalPath), cancellationToken).ConfigureAwait(false);
                return IsCurrentLiesSelection(version) ? new(state, candidates, candidates[0], LocalSaveSourceState.AutomaticallySelected, candidates[0].Label, null, false) : null;
            }
            if (configured is null && candidates.Length > 1) return new(null, candidates, null, LocalSaveSourceState.MultipleCandidates, "Choose the character you’re streaming.", null, false);
            if (configured is not null && IsLiesOfPConfiguredSaveReadable(configured)) return new(null, candidates, null, LocalSaveSourceState.CustomSelection, CustomSaveStatus(configured), null, false);
            return new(null, candidates, null, LocalSaveSourceState.NoCandidate, "No save found automatically.", null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch { return new(null, null, null, LocalSaveSourceState.NoCandidate, "Could not search for local saves. Try Rescan or Browse…", null, false); }
    }

    private static string CustomSaveStatus(string localPath) =>
        Path.GetFileName(localPath);
}
