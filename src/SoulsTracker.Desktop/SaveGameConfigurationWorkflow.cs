using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using System.IO;
using System.Collections.Immutable;

namespace SoulsTracker.Desktop;

/// <summary>Owns persistence of game-specific save configuration changes.</summary>
internal sealed class SaveGameConfigurationWorkflow(SerializedTrackerCoordinator coordinator)
{
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

    public static Task<IReadOnlyList<DiscoveredLocalSave>> DiscoverDirectoryAsync(GameId game, string directory, CancellationToken cancellationToken) =>
        Task.Run(() => game == GameId.EldenRing ? EldenRingSaveDiscovery.DiscoverInDirectory(directory)
            : game == GameId.BlackMythWukong ? BlackMythWukongSaveDiscovery.DiscoverInDirectory(directory)
            : LiesOfPSaveDiscovery.DiscoverInDirectory(directory), cancellationToken);

    public static Task<bool> ValidateDirectoryChoiceAsync(GameId game, string path, CancellationToken cancellationToken) =>
        Task.Run(() => game == GameId.EldenRing
            ? EldenRingSaveDiscovery.DiscoverInDirectory(Path.GetDirectoryName(path)!).Any(x => PathsEqual(x.LocalPath, path))
            : game == GameId.BlackMythWukong
                ? BlackMythWukongSaveDiscovery.DiscoverInSelectedFolder(path).Any(x => PathsEqual(x.LocalPath, path))
                : LiesOfPSaveDiscovery.DiscoverInSelectedFolder(path).Any(x => LiesOfPSaveDiscovery.IsSameCharacter(x.LocalPath, path)), cancellationToken);

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
        CancellationToken cancellationToken,
        Func<string, string, bool>? matchesConfiguredSource = null)
    {
        IReadOnlyList<DiscoveredLocalSave> candidates = await DiscoverAsync(discovery, cancellationToken).ConfigureAwait(false);
        bool configuredPathExists = configuredPath is not null && File.Exists(configuredPath);
        if (configuredPathExists && !candidates.Any(candidate => (matchesConfiguredSource ?? PathsEqual)(candidate.LocalPath, configuredPath!)))
        {
            candidates = await DiscoverInSelectedFolderAsync(discoverInSelectedFolder, configuredPath!, cancellationToken).ConfigureAwait(false);
        }
        return new SaveDiscoveryResult(candidates, configuredPath, configuredPathExists);
    }

    public static bool IsConfiguredSelectionAvailable(string? configuredPath, bool configuredPathExists) =>
        configuredPath is not null && configuredPathExists;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // Null means unavailable; an empty list can be a valid save with no characters.
    public static Task<IReadOnlyList<EldenRingProfileSlotChoice>?> ReadEldenRingProfileSlotsAsync(
        IEldenRingSaveProfileReader reader,
        string localPath,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<EldenRingCharacterSlotMetadata> metadata = await reader.ReadAsync(
                    new EldenRingSaveConfiguration(localPath, EldenRingSaveConfiguration.NoSlotIndex), cancellationToken).ConfigureAwait(false);
                if (ReferenceEquals(metadata, EldenRingCharacterSlotMetadata.UnavailableSlots)) return null;
                return (IReadOnlyList<EldenRingProfileSlotChoice>?)metadata
                    .Where(static item => !item.IsEmpty)
                    .Select(EldenRingProfileSlotChoice.FromMetadata)
                    .ToArray();
            }
            catch
            {
                return null;
            }
        }, cancellationToken);

    public Task<PersistentTrackerState> SaveEldenRingAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetEldenRingSaveConfigurationAsync(configuration, cancellationToken);

    public Task<PersistentTrackerState> SaveWukongAsync(BlackMythWukongSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetBlackMythWukongSaveConfigurationAsync(configuration, cancellationToken);

    public Task<PersistentTrackerState> SaveLiesOfPAsync(LiesOfPSaveConfiguration configuration, CancellationToken cancellationToken) =>
        coordinator.SetLiesOfPSaveConfigurationAsync(configuration, cancellationToken);


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
            SaveDiscoveryResult result = await DiscoverWithConfiguredFallbackAsync(discovery, LiesOfPSaveDiscovery.DiscoverInSelectedFolder, configured, cancellationToken, LiesOfPSaveDiscovery.IsSameCharacter).ConfigureAwait(false);
            if (!IsCurrentLiesSelection(version)) return null;
            // Keep the committed member identity when discovery reports its newer pair.
            ImmutableArray<DiscoveredLocalSave> candidates = [.. result.Candidates.Select(candidate =>
                configured is not null && LiesOfPSaveDiscovery.IsSameCharacter(candidate.LocalPath, configured)
                    ? candidate with { LocalPath = configured } : candidate)];
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
            return new(null, candidates, null, LocalSaveSourceState.NoCandidate,
                configured is null ? DesktopTrackerViewModel.MissingSaveDirectoryMessage : "Selected save is unavailable.", null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch { return new(null, null, null, LocalSaveSourceState.NoCandidate, "Could not search for local saves. Try Rescan or Choose directory.", null, false); }
    }

    private static string CustomSaveStatus(string localPath) =>
        Path.GetFileName(localPath);
}
