using System.Collections.ObjectModel;
using System.IO;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{
    // Directory choice is provisional until a resolved source is committed.
    private string? pendingSaveDirectory;
    private GameId? pendingDirectoryGame;
    private DirectorySelectionSnapshot? directorySelectionSnapshot;
    private sealed record DirectorySelectionSnapshot(GameId Game, DiscoveredLocalSave[] Choices, DiscoveredLocalSave? Selected, LocalSaveSourceState SourceState, string? Status);

    public string? EldenRingDirectoryPath => DirectoryFor(GameId.EldenRing);
    public string? BlackMythWukongDirectoryPath => DirectoryFor(GameId.BlackMythWukong);
    public string? LiesOfPDirectoryPath => DirectoryFor(GameId.LiesOfP);
    public string EldenRingDirectoryHeading => DirectoryHeading(GameId.EldenRing);
    public string BlackMythWukongDirectoryHeading => DirectoryHeading(GameId.BlackMythWukong);
    public string LiesOfPDirectoryHeading => DirectoryHeading(GameId.LiesOfP);
    private string DirectoryHeading(GameId game) => pendingDirectoryGame == game ? "Pending directory"
        : DirectoryFor(game) is null ? "Save directory" : "Chosen Directory";

    public Task SetEldenRingSaveDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        SetSaveDirectoryAsync(GameId.EldenRing, directory, cancellationToken);
    public Task SetBlackMythWukongSaveDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        SetSaveDirectoryAsync(GameId.BlackMythWukong, directory, cancellationToken);
    public Task SetLiesOfPSaveDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        SetSaveDirectoryAsync(GameId.LiesOfP, directory, cancellationToken);

    private string? ConfiguredPathFor(GameId game) => game == GameId.EldenRing ? state?.EldenRingSave.LocalPath
        : game == GameId.BlackMythWukong ? state?.BlackMythWukongSave.LocalPath : state?.LiesOfPSave.LocalPath;
    private string? ConfiguredDirectoryFor(GameId game) => game == GameId.EldenRing ? state?.EldenRingSave.SelectedDirectory
        : game == GameId.BlackMythWukong ? state?.BlackMythWukongSave.SelectedDirectory : state?.LiesOfPSave.SelectedDirectory;
    private string? DirectoryFor(GameId game)
    {
        if (pendingDirectoryGame == game) return pendingSaveDirectory;
        if (ConfiguredDirectoryFor(game) is { } directory) return directory;
        try { return Path.GetDirectoryName(ConfiguredPathFor(game)); }
        catch (ArgumentException) { return null; }
    }

    private ObservableCollection<DiscoveredLocalSave> ChoicesFor(GameId game) => game == GameId.EldenRing ? EldenRingSaveChoices
        : game == GameId.BlackMythWukong ? BlackMythWukongSaveChoices : LiesOfPSaveChoices;

    private bool IsCurrentDirectoryOperation(GameId game, long version) =>
        state?.SelectedGameId == game && saveGameConfigurationWorkflow.IsCurrentOperation(version);

    private async Task SetSaveDirectoryAsync(GameId game, string directory, CancellationToken cancellationToken, bool allowReplacement = true)
    {
        if (IsBusy || (allowReplacement && !ControlsEnabled) || state?.SelectedGameId != game) return;
        long version = saveGameConfigurationWorkflow.BeginOperation();
        Interlocked.Increment(ref eldenRingDiscoveryVersion);
        InvalidateWukongOperations();
        saveGameConfigurationWorkflow.BeginLiesSelection();
        bool isReplacement = allowReplacement && !PathsEqual(directory, DirectoryFor(game));
        // Rejected attempts need cancellation state even when no pending source
        // exists. A rescan of the committed directory must retain its own errors.
        if (isReplacement && directorySelectionSnapshot?.Game != game)
            directorySelectionSnapshot = CaptureDirectorySelection(game);
        else if (!isReplacement && pendingDirectoryGame != game)
            directorySelectionSnapshot = null;
        try
        {
            IReadOnlyList<DiscoveredLocalSave> candidates = await SaveGameConfigurationWorkflow.DiscoverDirectoryAsync(game, directory, cancellationToken);
            if (!IsCurrentDirectoryOperation(game, version)) return;
            // The reader follows both Lies members. Discovery may return the newer
            // member, but that must not silently change the accepted source identity.
            if (game == GameId.LiesOfP && ConfiguredPathFor(game) is { } configured)
                candidates = candidates.Select(x => LiesOfPSaveDiscovery.IsSameCharacter(x.LocalPath, configured)
                    ? x with { LocalPath = configured } : x).ToArray();
            if (candidates.Count == 0)
            {
                SetDirectoryStatus(game, isReplacement && ConfiguredPathFor(game) is not null
                    ? "No usable saves found in the attempted directory. Your previous selection is unchanged. Choose another directory or Cancel."
                    : "No usable saves found in this directory. Choose another directory or Rescan.");
                return;
            }
            pendingSaveDirectory = Path.GetFullPath(directory);
            pendingDirectoryGame = game;
            ObservableCollection<DiscoveredLocalSave> choices = ChoicesFor(game);
            choices.Clear();
            foreach (DiscoveredLocalSave candidate in candidates) choices.Add(candidate);
            NotifyDirectoryPaths();
            DiscoveredLocalSave? selected = candidates.SingleOrDefault(x => PathsEqual(x.LocalPath, ConfiguredPathFor(game)));
            SetDirectoryChoice(game, selected, LocalSaveSourceState.MultipleCandidates, changeMode: true);
            if (selected is not null || (candidates.Count == 1 && (allowReplacement || ConfiguredPathFor(game) is null)))
                await CommitDirectoryChoiceAsync(game, selected ?? candidates[0], version, cancellationToken, preserveProfileSelection: !allowReplacement);
            else
                SetDirectoryStatus(game, game == GameId.EldenRing ? "Choose a save source, then a character." : "Choose the character or save slot you are streaming.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            if (IsCurrentDirectoryOperation(game, version)) SetDirectoryStatus(game, isReplacement && directorySelectionSnapshot?.Game == game && ConfiguredPathFor(game) is not null
                ? "The attempted directory could not be used. Your previous selection is unchanged. Choose another directory or Cancel."
                : "The directory could not be used. Choose another directory or Rescan.");
        }
    }

    private async Task CommitDirectoryChoiceAsync(GameId game, DiscoveredLocalSave choice, long version, CancellationToken cancellationToken, bool preserveProfileSelection = false)
    {
        string? directory = DirectoryFor(game);
        bool valid = await SaveGameConfigurationWorkflow.ValidateDirectoryChoiceAsync(game, choice.LocalPath, cancellationToken);
        if (!IsCurrentDirectoryOperation(game, version)) return;
        if (!valid)
        {
            SetDirectoryStatus(game, "Selected source is unavailable or unsupported. Rescan or choose another directory.");
            return;
        }
        IReadOnlyList<EldenRingProfileSlotChoice> profiles = [];
        if (game == GameId.EldenRing)
            profiles = await ReadEldenRingProfileChoicesAsync(choice.LocalPath, cancellationToken);
        if (!IsCurrentDirectoryOperation(game, version)) return;
        if (game == GameId.EldenRing && profiles.Count == 0)
        {
            SetDirectoryStatus(game, "Character information is unavailable. Rescan or choose another directory.");
            return;
        }
        // Persistence is the commit boundary. The same disabled-controls guard
        // applies to public Cancel/Rescan calls until this write finishes.
        bool wasBusy = IsBusy;
        IsBusy = true;
        try
        {
            if (game == GameId.EldenRing)
            {
                int oldSlot = state!.EldenRingSave.SlotIndex;
                int slot = PathsEqual(choice.LocalPath, state.EldenRingSave.LocalPath) && (preserveProfileSelection || profiles.Any(x => x.Index == oldSlot))
                    ? oldSlot : !preserveProfileSelection && profiles.Count == 1 ? profiles[0].Index : EldenRingSaveConfiguration.NoSlotIndex;
                var configuration = new EldenRingSaveConfiguration(choice.LocalPath, slot, directory);
                if (configuration != state.EldenRingSave)
                    ApplyCommittedState(await saveGameConfigurationWorkflow.SaveEldenRingAsync(configuration, cancellationToken));
                ApplyEldenRingProfileChoices(profiles);
                SelectedEldenRingSaveChoice = choice;
                EldenRingSaveSourceState = LocalSaveSourceState.CustomSelection;
                IsEldenRingChangeMode = false;
                OnPropertyChanged(nameof(SelectedEldenRingSaveChoice));
            }
            else if (game == GameId.BlackMythWukong)
            {
                var configuration = new BlackMythWukongSaveConfiguration(choice.LocalPath, directory);
                if (configuration != state!.BlackMythWukongSave)
                    ApplyCommittedState(await saveGameConfigurationWorkflow.SaveWukongAsync(configuration, cancellationToken));
                SelectedBlackMythWukongSaveChoice = choice;
                WukongSaveSourceState = LocalSaveSourceState.CustomSelection;
                IsBlackMythWukongChangeMode = false;
                OnPropertyChanged(nameof(SelectedBlackMythWukongSaveChoice));
            }
            else
            {
                var configuration = new LiesOfPSaveConfiguration(choice.LocalPath, directory);
                if (configuration != state!.LiesOfPSave)
                    ApplyCommittedState(await saveGameConfigurationWorkflow.SaveLiesOfPAsync(configuration, cancellationToken));
                SelectedLiesOfPSaveChoice = choice;
                LiesOfPSaveSourceState = LocalSaveSourceState.CustomSelection;
                IsLiesOfPChangeMode = false;
                OnPropertyChanged(nameof(SelectedLiesOfPSaveChoice));
            }
            pendingSaveDirectory = null;
            pendingDirectoryGame = null;
            directorySelectionSnapshot = null;
            SetDirectoryStatus(game, choice.Label);
            NotifyDirectoryPaths();
        }
        catch { SetDirectoryStatus(game, "The directory selection could not be saved. Your previous selection is unchanged."); }
        finally { IsBusy = wasBusy; NotifyTrackerProperties(); }
        if (game == GameId.BlackMythWukong && IsCurrentDirectoryOperation(game, version) && PathsEqual(ConfiguredPathFor(game), choice.LocalPath))
        {
            long metadataVersion = BeginWukongMetadataRead();
            WukongSaveMetadataReadResult read = await readWukongSaveMetadataAsync(choice.LocalPath, cancellationToken);
            TryApplyBlackMythWukongSaveMetadata(metadataVersion, choice.LocalPath, read.IsValid ? read.Metadata : null);
        }
    }

    private DirectorySelectionSnapshot CaptureDirectorySelection(GameId game) => game == GameId.EldenRing
        ? new(game, [.. EldenRingSaveChoices], SelectedEldenRingSaveChoice, EldenRingSaveSourceState, EldenRingSaveDiscoveryStatus)
        : game == GameId.BlackMythWukong
            ? new(game, [.. BlackMythWukongSaveChoices], SelectedBlackMythWukongSaveChoice, WukongSaveSourceState, BlackMythWukongSaveDiscoveryStatus)
            : new(game, [.. LiesOfPSaveChoices], SelectedLiesOfPSaveChoice, LiesOfPSaveSourceState, LiesOfPSaveDiscoveryStatus);

    private void SetDirectoryChoice(GameId game, DiscoveredLocalSave? choice, LocalSaveSourceState sourceState, bool changeMode)
    {
        if (game == GameId.EldenRing)
        {
            SelectedEldenRingSaveChoice = choice;
            EldenRingSaveSourceState = sourceState;
            IsEldenRingChangeMode = changeMode;
            OnPropertyChanged(nameof(SelectedEldenRingSaveChoice));
            NotifyEldenRingSaveSourceProperties();
        }
        else if (game == GameId.BlackMythWukong)
        {
            SelectedBlackMythWukongSaveChoice = choice;
            WukongSaveSourceState = sourceState;
            IsBlackMythWukongChangeMode = changeMode;
            OnPropertyChanged(nameof(SelectedBlackMythWukongSaveChoice));
            NotifyWukongSaveSourceProperties();
        }
        else
        {
            SelectedLiesOfPSaveChoice = choice;
            LiesOfPSaveSourceState = sourceState;
            IsLiesOfPChangeMode = changeMode;
            OnPropertyChanged(nameof(SelectedLiesOfPSaveChoice));
            NotifyLiesOfPSaveSourceProperties();
        }
    }

    private void CancelDirectorySelection(GameId game)
    {
        saveGameConfigurationWorkflow.InvalidateOperations();
        if (pendingDirectoryGame != game && directorySelectionSnapshot?.Game != game) return;
        if (directorySelectionSnapshot is { } snapshot && snapshot.Game == game)
        {
            ObservableCollection<DiscoveredLocalSave> choices = ChoicesFor(game);
            choices.Clear();
            foreach (DiscoveredLocalSave candidate in snapshot.Choices) choices.Add(candidate);
            SetDirectoryChoice(game, snapshot.Selected, snapshot.SourceState, changeMode: false);
            SetDirectoryStatus(game, snapshot.Status ?? string.Empty);
        }
        pendingSaveDirectory = null;
        pendingDirectoryGame = null;
        directorySelectionSnapshot = null;
        NotifyDirectoryPaths();
    }

    private bool HasDirectorySelection(GameId game) => pendingDirectoryGame == game || ConfiguredDirectoryFor(game) is not null;

    private Task RescanDirectoryAsync(GameId game, CancellationToken cancellationToken) =>
        SetSaveDirectoryAsync(game, DirectoryFor(game)!, cancellationToken, allowReplacement: pendingDirectoryGame == game);

    private Task SelectDirectoryChoiceAsync(GameId game, DiscoveredLocalSave choice, CancellationToken cancellationToken) =>
        CommitDirectoryChoiceAsync(game, choice, saveGameConfigurationWorkflow.BeginOperation(), cancellationToken);

    private void SetDirectoryStatus(GameId game, string status)
    {
        if (game == GameId.EldenRing) SetEldenRingSaveDiscoveryStatus(status);
        else if (game == GameId.BlackMythWukong) SetBlackMythWukongSaveDiscoveryStatus(status);
        else SetLiesOfPSaveDiscoveryStatus(status);
    }

    private void NotifyDirectoryPaths()
    {
        OnPropertyChanged(nameof(EldenRingDirectoryPath));
        OnPropertyChanged(nameof(BlackMythWukongDirectoryPath));
        OnPropertyChanged(nameof(LiesOfPDirectoryPath));
        OnPropertyChanged(nameof(EldenRingDirectoryHeading));
        OnPropertyChanged(nameof(BlackMythWukongDirectoryHeading));
        OnPropertyChanged(nameof(LiesOfPDirectoryHeading));
    }
}
