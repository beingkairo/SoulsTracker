using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{
    internal const string MissingSaveDirectoryMessage = "Save directory was not found automatically, please click Choose Directory and select the save folder";
    // Session-only provenance, keyed by the source actually selected by discovery.
    private readonly Dictionary<GameId, string> automaticDirectorySources = [];
    private readonly Dictionary<GameId, string> discoveredDirectories = [];
    internal const string ChooseCharacterMessage = "Choose a character to continue.";
    public bool EldenRingDirectoryWasAutomatic => DirectoryWasAutomatic(GameId.EldenRing);
    public bool BlackMythWukongDirectoryWasAutomatic => DirectoryWasAutomatic(GameId.BlackMythWukong);
    public bool LiesOfPDirectoryWasAutomatic => DirectoryWasAutomatic(GameId.LiesOfP);
    public string? EldenRingDirectoryStatus => DirectoryStatus(GameId.EldenRing, EldenRingSaveDiscoveryStatus, SelectedEldenRingSaveChoice);
    public string? BlackMythWukongDirectoryStatus => DirectoryStatus(GameId.BlackMythWukong, BlackMythWukongSaveDiscoveryStatus, SelectedBlackMythWukongSaveChoice);
    public string? LiesOfPDirectoryStatus => DirectoryStatus(GameId.LiesOfP, LiesOfPSaveDiscoveryStatus, SelectedLiesOfPSaveChoice);
    public string? EldenRingDirectoryActionFeedback => ActionFeedback(EldenRingDirectoryStatus);
    public string? BlackMythWukongDirectoryActionFeedback => ActionFeedback(BlackMythWukongDirectoryStatus);
    public string? LiesOfPDirectoryActionFeedback => ActionFeedback(LiesOfPDirectoryStatus);

    private static string? ActionFeedback(string? status) => status?.Contains("previous selection is unchanged", StringComparison.OrdinalIgnoreCase) == true
        || status?.Contains("could not be saved", StringComparison.OrdinalIgnoreCase) == true ? status : null;

    private string? SaveReaderStatus(GameId game)
    {
        string? status = game == GameId.EldenRing ? EldenRingDirectoryStatus
            : game == GameId.BlackMythWukong ? BlackMythWukongDirectoryStatus : LiesOfPDirectoryStatus;
        return ActionFeedback(status) is null ? status : null;
    }

    private static bool RequiresDirectoryAttention(string status) => status == ChooseCharacterMessage
        || status.StartsWith("No usable saves", StringComparison.Ordinal)
        || status.StartsWith("The directory could not be used", StringComparison.Ordinal)
        || status.StartsWith("Selected source is unavailable", StringComparison.Ordinal)
        || status.StartsWith("Character information is unavailable", StringComparison.Ordinal);

    private bool DirectoryWasAutomatic(GameId game) => pendingDirectoryGame != game
        && ((ConfiguredPathFor(game) is null && discoveredDirectories.ContainsKey(game))
            || (automaticDirectorySources.TryGetValue(game, out string? path) && PathsEqual(path, ConfiguredPathFor(game))));

    private void ProjectDiscoveredDirectory(GameId game, IEnumerable<DiscoveredLocalSave> candidates)
    {
        // Presentation only: never choose a source or persist an ambiguous result.
        if (ConfiguredPathFor(game) is not null) return;
        string?[] directories = candidates.Select(x => Path.GetDirectoryName(x.LocalPath))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (directories is [string directory] && !string.IsNullOrWhiteSpace(directory))
            discoveredDirectories[game] = directory;
        else
            discoveredDirectories.Remove(game);
        NotifyDirectoryPaths();
    }

    private string? DirectoryStatus(GameId game, string? status, DiscoveredLocalSave? selected) =>
        status == selected?.Label || (ConfiguredPathFor(game) is { } path && status == Path.GetFileName(path)) ? null : status;

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
    private string DirectoryHeading(GameId game) => pendingDirectoryGame == game ? "Pending Directory"
        : DirectoryFor(game) is null ? "Save Directory" : "Chosen Directory";

    public Task SetEldenRingSaveDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        ChooseSaveDirectoryAsync(GameId.EldenRing, directory, cancellationToken);
    public Task SetBlackMythWukongSaveDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        ChooseSaveDirectoryAsync(GameId.BlackMythWukong, directory, cancellationToken);
    public Task SetLiesOfPSaveDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        ChooseSaveDirectoryAsync(GameId.LiesOfP, directory, cancellationToken);

    private Task ChooseSaveDirectoryAsync(GameId game, string directory, CancellationToken cancellationToken)
    {
        // Picking the current folder is not Refresh. Do not invalidate reader
        // operations, alter pending selection, or write legacy configuration.
        try
        {
            if (DirectoryFor(game) is { } current && PathsEqual(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)))) return Task.CompletedTask;
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (PathTooLongException) { }
        return SetSaveDirectoryAsync(game, directory, cancellationToken);
    }

    private string? ConfiguredPathFor(GameId game) => game == GameId.EldenRing ? state?.EldenRingSave.LocalPath
        : game == GameId.BlackMythWukong ? state?.BlackMythWukongSave.LocalPath : state?.LiesOfPSave.LocalPath;
    private string? ConfiguredDirectoryFor(GameId game) => game == GameId.EldenRing ? state?.EldenRingSave.SelectedDirectory
        : game == GameId.BlackMythWukong ? state?.BlackMythWukongSave.SelectedDirectory : state?.LiesOfPSave.SelectedDirectory;
    private string? DirectoryFor(GameId game)
    {
        if (pendingDirectoryGame == game) return pendingSaveDirectory;
        if (ConfiguredDirectoryFor(game) is { } directory) return directory;
        if (ConfiguredPathFor(game) is null && discoveredDirectories.TryGetValue(game, out string? discovered)) return discovered;
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
        using var resolution = BeginSaveReaderSourceResolution(game);
        long version = saveGameConfigurationWorkflow.BeginOperation();
        Interlocked.Increment(ref eldenRingDiscoveryVersion);
        InvalidateWukongOperations();
        saveGameConfigurationWorkflow.BeginLiesSelection();
        bool isReplacement = allowReplacement && !PathsEqual(directory, DirectoryFor(game));
        // Rejected attempts need cancellation state even when no pending source
        // exists. A rescan of the committed directory must retain its own errors.
        if (isReplacement && directorySelectionSnapshot?.Game != game)
        {
            directorySelectionSnapshot = CaptureDirectorySelection(game);
            var snapshot = directorySelectionSnapshot;
            SetDirectoryChoice(game, snapshot.Selected, snapshot.SourceState, changeMode: true);
        }
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
                    : "No usable saves found in this directory. Choose another directory or Refresh.");
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
                SetDirectoryStatus(game, ChooseCharacterMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            if (IsCurrentDirectoryOperation(game, version)) SetDirectoryStatus(game, isReplacement && directorySelectionSnapshot?.Game == game && ConfiguredPathFor(game) is not null
                ? "The attempted directory could not be used. Your previous selection is unchanged. Choose another directory or Cancel."
                : "The directory could not be used. Choose another directory or Refresh.");
        }
    }

    private async Task CommitDirectoryChoiceAsync(GameId game, DiscoveredLocalSave choice, long version, CancellationToken cancellationToken, bool preserveProfileSelection = false)
    {
        using var resolution = BeginSaveReaderSourceResolution(game);
        string? directory = DirectoryFor(game);
        bool valid = await SaveGameConfigurationWorkflow.ValidateDirectoryChoiceAsync(game, choice.LocalPath, cancellationToken);
        if (!IsCurrentDirectoryOperation(game, version)) return;
        if (!valid)
        {
            SetDirectoryStatus(game, "Selected source is unavailable or unsupported. Refresh or choose another directory.");
            return;
        }
        IReadOnlyList<EldenRingProfileSlotChoice> profiles = [];
        if (game == GameId.EldenRing)
            profiles = await ReadEldenRingProfileChoicesAsync(choice.LocalPath, cancellationToken);
        if (!IsCurrentDirectoryOperation(game, version)) return;
        if (game == GameId.EldenRing && profiles.Count == 0)
        {
            SetDirectoryStatus(game, "Character information is unavailable. Refresh or choose another directory.");
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
            if (!preserveProfileSelection) automaticDirectorySources.Remove(game);
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
        OnPropertyChanged(nameof(EldenRingDirectoryActionFeedback));
        OnPropertyChanged(nameof(BlackMythWukongDirectoryActionFeedback));
        OnPropertyChanged(nameof(LiesOfPDirectoryActionFeedback));
        OnPropertyChanged(nameof(EldenRingDirectoryWasAutomatic));
        OnPropertyChanged(nameof(BlackMythWukongDirectoryWasAutomatic));
        OnPropertyChanged(nameof(LiesOfPDirectoryWasAutomatic));
        OnPropertyChanged(nameof(EldenRingDirectoryStatus));
        OnPropertyChanged(nameof(BlackMythWukongDirectoryStatus));
        OnPropertyChanged(nameof(LiesOfPDirectoryStatus));
        OnPropertyChanged(nameof(EldenRingDirectoryPath));
        OnPropertyChanged(nameof(BlackMythWukongDirectoryPath));
        OnPropertyChanged(nameof(LiesOfPDirectoryPath));
        OnPropertyChanged(nameof(EldenRingDirectoryHeading));
        OnPropertyChanged(nameof(BlackMythWukongDirectoryHeading));
        OnPropertyChanged(nameof(LiesOfPDirectoryHeading));
    }

    private readonly object eldenRingProfileSlotsSynchronization = new();
    private readonly object eldenRingSaveChoicesSynchronization = new();
    private readonly object blackMythWukongSaveChoicesSynchronization = new();
    private readonly object liesOfPSaveChoicesSynchronization = new();
    private LocalSaveSourceState wukongSaveSourceState;
    private bool isBlackMythWukongChangeMode;
    private string? blackMythWukongSaveDiscoveryStatus;
    private string? liesOfPSaveDiscoveryStatus;
    private LocalSaveSourceState liesOfPSaveSourceState;
    private bool isLiesOfPChangeMode;
    private BlackMythWukongSaveMetadata? blackMythWukongSaveMetadata;

    private long eldenRingDiscoveryVersion;
    private LocalSaveSourceState eldenRingSaveSourceState;
    private bool isEldenRingChangeMode;
    private string? eldenRingSaveDiscoveryStatus;

    public ObservableCollection<EldenRingProfileSlotChoice> EldenRingProfileSlots { get; }
    public ObservableCollection<DiscoveredLocalSave> EldenRingSaveChoices { get; }
    public DiscoveredLocalSave? SelectedEldenRingSaveChoice { get; private set; }
    public string? EldenRingSaveDiscoveryStatus => eldenRingSaveDiscoveryStatus;
    public LocalSaveSourceState EldenRingSaveSourceState { get => eldenRingSaveSourceState; private set { if (SetField(ref eldenRingSaveSourceState, value)) NotifyEldenRingSaveSourceProperties(); } }
    public bool IsEldenRingChangeMode { get => isEldenRingChangeMode; private set { if (SetField(ref isEldenRingChangeMode, value)) NotifyEldenRingSaveSourceProperties(); } }
    public string? EldenRingCharacterStatus => IsEldenRingSelected && state?.EldenRingSave.LocalPath is not null && SelectedEldenRingProfileSlot is null ? EldenRingChooseCharacterMessage : null;
    public ObservableCollection<DiscoveredLocalSave> BlackMythWukongSaveChoices { get; }
    public DiscoveredLocalSave? SelectedBlackMythWukongSaveChoice { get; private set; }
    public string? BlackMythWukongSaveDiscoveryStatus => blackMythWukongSaveDiscoveryStatus;
    public LocalSaveSourceState WukongSaveSourceState { get => wukongSaveSourceState; private set { if (SetField(ref wukongSaveSourceState, value)) NotifyWukongSaveSourceProperties(); } }
    public bool IsBlackMythWukongChangeMode { get => isBlackMythWukongChangeMode; private set { if (SetField(ref isBlackMythWukongChangeMode, value)) NotifyWukongSaveSourceProperties(); } }
    public bool IsWukongSaveSelectorVisible =>
        BlackMythWukongSaveChoices.Count > 0
        && (IsBlackMythWukongChangeMode || BlackMythWukongSaveChoices.Count > 1);
    public bool IsWukongBrowseVisible => WukongSaveSourceState is LocalSaveSourceState.Scanning or LocalSaveSourceState.NoCandidate or LocalSaveSourceState.MultipleCandidates || IsBlackMythWukongChangeMode;
    public bool IsWukongChangeVisible => !IsBlackMythWukongChangeMode && WukongSaveSourceState is LocalSaveSourceState.AutomaticallySelected or LocalSaveSourceState.PersistedDiscovered or LocalSaveSourceState.CustomSelection or LocalSaveSourceState.UnavailableSelection;
    public bool IsWukongCancelVisible => IsBlackMythWukongChangeMode;
    public bool IsWukongSaveSelectorEnabled => ControlsEnabled && WukongSaveSourceState != LocalSaveSourceState.Scanning && IsWukongSaveSelectorVisible && BlackMythWukongSaveChoices.Count > 0;
    public string? BlackMythWukongSaveMetadataText =>
        IsBlackMythWukongSelected
            ? FormatBlackMythWukongSaveMetadata(blackMythWukongSaveMetadata, CultureInfo.CurrentCulture, timeProvider)
            : null;
    public ObservableCollection<DiscoveredLocalSave> LiesOfPSaveChoices { get; }
    public DiscoveredLocalSave? SelectedLiesOfPSaveChoice { get; private set; }
    public string? LiesOfPSaveDiscoveryStatus => liesOfPSaveDiscoveryStatus;
    public LocalSaveSourceState LiesOfPSaveSourceState { get => liesOfPSaveSourceState; private set { if (SetField(ref liesOfPSaveSourceState, value)) NotifyLiesOfPSaveSourceProperties(); } }
    public bool IsLiesOfPChangeMode { get => isLiesOfPChangeMode; private set { if (SetField(ref isLiesOfPChangeMode, value)) NotifyLiesOfPSaveSourceProperties(); } }
    public bool IsLiesOfPSaveSelectorVisible => LiesOfPSaveChoices.Count > 0 && (IsLiesOfPChangeMode || LiesOfPSaveChoices.Count > 1);
    public bool IsLiesOfPSaveSelectorEnabled => ControlsEnabled && LiesOfPSaveSourceState != LocalSaveSourceState.Scanning && IsLiesOfPSaveSelectorVisible;
    public bool IsLiesOfPBrowseVisible => LiesOfPSaveSourceState is LocalSaveSourceState.Scanning or LocalSaveSourceState.NoCandidate or LocalSaveSourceState.MultipleCandidates || IsLiesOfPChangeMode;
    public bool IsLiesOfPChangeVisible => !IsLiesOfPChangeMode && LiesOfPSaveSourceState is LocalSaveSourceState.AutomaticallySelected or LocalSaveSourceState.PersistedDiscovered or LocalSaveSourceState.CustomSelection or LocalSaveSourceState.UnavailableSelection;
    public bool IsLiesOfPCancelVisible => IsLiesOfPChangeMode;
    /// <summary>True only when the selected local Elden Ring save is still available for slot selection.</summary>
    public bool CanSelectEldenRingProfile => ControlsEnabled
        && IsEldenRingSelected
        && IsAvailableEldenRingSaveFile(state?.EldenRingSave.LocalPath)
        && EldenRingProfileSlots.Count > 0;
    public EldenRingProfileSlotChoice? SelectedEldenRingProfileSlot { get; private set; }

    public async Task SelectEldenRingSaveChoiceAsync(DiscoveredLocalSave? choice, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || choice is null || !EldenRingSaveChoices.Contains(choice)) return;
        if (HasDirectorySelection(GameId.EldenRing)) { await SelectDirectoryChoiceAsync(GameId.EldenRing, choice, cancellationToken); return; }
        await CommitEldenRingSaveAsync(choice.LocalPath, choice, LocalSaveSourceState.PersistedDiscovered, cancellationToken);
    }

    public void BeginEldenRingChange() { if (IsEldenRingSelected) IsEldenRingChangeMode = true; }
    public void CancelEldenRingChange() { if (IsBusy) return; CancelDirectorySelection(GameId.EldenRing); IsEldenRingChangeMode = false; }

    public async Task RescanEldenRingSavesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEldenRingSelected) return;
        using var resolution = BeginSaveReaderSourceResolution(GameId.EldenRing);
        if (HasDirectorySelection(GameId.EldenRing)) { await RescanDirectoryAsync(GameId.EldenRing, cancellationToken); return; }
        long version = Interlocked.Increment(ref eldenRingDiscoveryVersion);
        LocalSaveSourceState stableState = EldenRingSaveSourceState;
        bool stableChangeMode = IsEldenRingChangeMode;
        string? stableStatus = EldenRingSaveDiscoveryStatus;
        EldenRingSaveSourceState = LocalSaveSourceState.Scanning;
        SetEldenRingSaveDiscoveryStatus("Looking for local saves…");
        IReadOnlyList<DiscoveredLocalSave> candidates;
        try
        {
            candidates = await SaveGameConfigurationWorkflow.DiscoverAsync(eldenRingSaveDiscovery, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (version == Interlocked.Read(ref eldenRingDiscoveryVersion))
            {
                SetEldenRingSaveDiscoveryStatus(stableStatus);
                EldenRingSaveSourceState = stableState;
                IsEldenRingChangeMode = stableChangeMode;
            }
            return;
        }
        catch
        {
            if (version == Interlocked.Read(ref eldenRingDiscoveryVersion))
            {
                EldenRingSaveSourceState = stableState;
                IsEldenRingChangeMode = stableChangeMode;
                SetEldenRingSaveDiscoveryStatus("Could not search for local saves. Try Refresh or choose another directory.");
            }
            return;
        }
        if (version != Interlocked.Read(ref eldenRingDiscoveryVersion) || !IsEldenRingSelected) return;

        EldenRingSaveChoices.Clear();
        foreach (DiscoveredLocalSave candidate in candidates) EldenRingSaveChoices.Add(candidate);
        string? configured = state?.EldenRingSave.LocalPath;
        ProjectDiscoveredDirectory(GameId.EldenRing, candidates);
        SelectedEldenRingSaveChoice = candidates.SingleOrDefault(candidate => string.Equals(candidate.LocalPath, configured, StringComparison.OrdinalIgnoreCase));

        if (configured is not null && !File.Exists(configured))
        {
            SetEldenRingSaveDiscoveryStatus("Selected save is unavailable.");
            EldenRingSaveSourceState = LocalSaveSourceState.UnavailableSelection;
            ApplyEldenRingProfileChoices([]);
        }
        else if (SelectedEldenRingSaveChoice is not null)
        {
            SetEldenRingSaveDiscoveryStatus(SelectedEldenRingSaveChoice.Label);
            EldenRingSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
            await RefreshEldenRingProfileSlotsAsync(cancellationToken, clearStaleSelection: true);
        }
        else if (configured is null && candidates.Count == 1)
        {
            await CommitEldenRingSaveAsync(candidates[0].LocalPath, candidates[0], LocalSaveSourceState.AutomaticallySelected, cancellationToken);
        }
        else if (configured is null && candidates.Count > 1)
        {
            SetEldenRingSaveDiscoveryStatus(ChooseCharacterMessage);
            EldenRingSaveSourceState = LocalSaveSourceState.MultipleCandidates;
            ApplyEldenRingProfileChoices([]);
        }
        else if (configured is not null && EldenRingSaveDiscovery.IsParserValidSave(configured))
        {
            SetEldenRingSaveDiscoveryStatus(CustomSaveTrackingStatus(configured));
            EldenRingSaveSourceState = LocalSaveSourceState.CustomSelection;
            await RefreshEldenRingProfileSlotsAsync(cancellationToken, clearStaleSelection: true);
        }
        else
        {
            SetEldenRingSaveDiscoveryStatus(configured is null ? MissingSaveDirectoryMessage : "Selected save is unavailable.");
            EldenRingSaveSourceState = configured is null ? LocalSaveSourceState.NoCandidate : LocalSaveSourceState.UnavailableSelection;
            ApplyEldenRingProfileChoices([]);
        }
        OnPropertyChanged(nameof(SelectedEldenRingSaveChoice));
        NotifyEldenRingSaveSourceProperties();
    }
    public async Task SetEldenRingProfileSlotAsync(EldenRingProfileSlotChoice slot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!CanSelectEldenRingProfile || !EldenRingProfileSlots.Any(choice => choice.Index == slot.Index)) return;
        await SaveEldenRingSaveAsync(new EldenRingSaveConfiguration(state?.EldenRingSave.LocalPath, slot.Index, state?.EldenRingSave.SelectedDirectory), cancellationToken);
    }

    public async Task SelectBlackMythWukongSaveChoiceAsync(DiscoveredLocalSave? choice, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || choice is null || !BlackMythWukongSaveChoices.Contains(choice)) return;
        if (HasDirectorySelection(GameId.BlackMythWukong)) { await SelectDirectoryChoiceAsync(GameId.BlackMythWukong, choice, cancellationToken); return; }
        long selectionVersion = BeginWukongSelectionOperation();
        await SaveBlackMythWukongSaveAsync(new BlackMythWukongSaveConfiguration(choice.LocalPath), cancellationToken);
        if (!IsCurrentWukongSelectionOperation(selectionVersion, choice.LocalPath)) return;
        SelectedBlackMythWukongSaveChoice = choice;
        automaticDirectorySources.Remove(GameId.BlackMythWukong);
        IsBlackMythWukongChangeMode = false;
        WukongSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
        SetBlackMythWukongSaveDiscoveryStatus(choice.Label);
        OnPropertyChanged(nameof(SelectedBlackMythWukongSaveChoice));
        long metadataVersion = BeginWukongMetadataRead();
        WukongSaveMetadataReadResult read = await readWukongSaveMetadataAsync(choice.LocalPath, cancellationToken);
        TryApplyBlackMythWukongSaveMetadata(
            metadataVersion,
            choice.LocalPath,
            read.IsValid ? read.Metadata : null);
    }

    public async Task RescanBlackMythWukongSavesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBlackMythWukongSelected) return;
        using var resolution = BeginSaveReaderSourceResolution(GameId.BlackMythWukong);
        if (HasDirectorySelection(GameId.BlackMythWukong)) { await RescanDirectoryAsync(GameId.BlackMythWukong, cancellationToken); return; }
        long selectionVersion = BeginWukongSelectionOperation();
        long version = saveGameConfigurationWorkflow.BeginWukongDiscovery();
        LocalSaveSourceState stableState = WukongSaveSourceState;
        bool stableChangeMode = IsBlackMythWukongChangeMode;
        string? stableStatus = BlackMythWukongSaveDiscoveryStatus;
        WukongSaveSourceState = LocalSaveSourceState.Scanning;
        SetBlackMythWukongSaveDiscoveryStatus("Looking for local saves…");
        IReadOnlyList<DiscoveredLocalSave> candidates;
        string? configuredAtStart = state?.BlackMythWukongSave.LocalPath;
        try
        {
            SaveGameConfigurationWorkflow.SaveDiscoveryResult discoveryResult = await SaveGameConfigurationWorkflow.DiscoverWithConfiguredFallbackAsync(
                blackMythWukongSaveDiscovery,
                BlackMythWukongSaveDiscovery.DiscoverInSelectedFolder,
                configuredAtStart,
                cancellationToken);
            candidates = discoveryResult.Candidates;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (saveGameConfigurationWorkflow.IsCurrentWukongDiscovery(version) &&
                IsCurrentWukongSelectionOperation(selectionVersion))
            {
                SetBlackMythWukongSaveDiscoveryStatus(stableStatus);
                WukongSaveSourceState = stableState;
                IsBlackMythWukongChangeMode = stableChangeMode;
                NotifyWukongSaveSourceProperties();
            }
            return;
        }
        catch
        {
            if (saveGameConfigurationWorkflow.IsCurrentWukongDiscovery(version) &&
                IsCurrentWukongSelectionOperation(selectionVersion))
            {
                WukongSaveSourceState = stableState;
                IsBlackMythWukongChangeMode = stableChangeMode;
                SetBlackMythWukongSaveDiscoveryStatus("Could not search for local saves. Try Refresh or choose another directory.");
                NotifyWukongSaveSourceProperties();
            }
            return;
        }
        if (!saveGameConfigurationWorkflow.IsCurrentWukongDiscovery(version) ||
            !IsCurrentWukongSelectionOperation(selectionVersion)) return;
        BlackMythWukongSaveChoices.Clear();
        foreach (DiscoveredLocalSave candidate in candidates) BlackMythWukongSaveChoices.Add(candidate);

        string? configured = state?.BlackMythWukongSave.LocalPath;
        ProjectDiscoveredDirectory(GameId.BlackMythWukong, candidates);
        string? metadataPath = null;
        string? metadataReadPath = null;
        BlackMythWukongSaveMetadata? refreshedMetadata = null;
        bool metadataWasRead = false;
        long metadataVersion = 0;
        SelectedBlackMythWukongSaveChoice = candidates.SingleOrDefault(candidate => string.Equals(candidate.LocalPath, configured, StringComparison.OrdinalIgnoreCase));
        if (configured is not null && !File.Exists(configured))
        {
            SetBlackMythWukongSaveDiscoveryStatus("Selected save is unavailable.");
            WukongSaveSourceState = LocalSaveSourceState.UnavailableSelection;
        }
        else if (SelectedBlackMythWukongSaveChoice is { } selected)
        {
            SetBlackMythWukongSaveDiscoveryStatus(selected.Label);
            WukongSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
            metadataPath = selected.LocalPath;
        }
        else if (configured is null && candidates.Count == 1)
        {
            await SaveBlackMythWukongSaveAsync(new BlackMythWukongSaveConfiguration(candidates[0].LocalPath), cancellationToken);
            if (IsCurrentWukongSelectionOperation(selectionVersion, candidates[0].LocalPath))
            {
                SelectedBlackMythWukongSaveChoice = candidates[0];
                automaticDirectorySources[GameId.BlackMythWukong] = candidates[0].LocalPath;
                SetBlackMythWukongSaveDiscoveryStatus(candidates[0].Label);
                WukongSaveSourceState = LocalSaveSourceState.AutomaticallySelected;
                metadataPath = candidates[0].LocalPath;
            }
        }
        else if (configured is null && candidates.Count > 1)
        {
            WukongSaveSourceState = LocalSaveSourceState.MultipleCandidates;
            SetBlackMythWukongSaveDiscoveryStatus(ChooseCharacterMessage);
        }
        else if (configured is not null)
        {
            metadataVersion = BeginWukongMetadataRead();
            metadataReadPath = configured;
            WukongSaveMetadataReadResult read = await readWukongSaveMetadataAsync(configured, cancellationToken);
            if (!saveGameConfigurationWorkflow.IsCurrentWukongDiscovery(version) ||
                !IsCurrentWukongSelectionOperation(selectionVersion, configured)) return;
            if (read.IsValid)
            {
                SetBlackMythWukongSaveDiscoveryStatus(CustomSaveTrackingStatus(configured));
                WukongSaveSourceState = LocalSaveSourceState.CustomSelection;
                refreshedMetadata = read.Metadata;
                metadataWasRead = true;
            }
            else
            {
                SetBlackMythWukongSaveDiscoveryStatus("Unable to read the selected save.");
                WukongSaveSourceState = LocalSaveSourceState.UnavailableSelection;
            }
        }
        else
        {
            SetBlackMythWukongSaveDiscoveryStatus(MissingSaveDirectoryMessage);
            WukongSaveSourceState = LocalSaveSourceState.NoCandidate;
        }
        if (metadataPath is not null)
        {
            metadataVersion = BeginWukongMetadataRead();
            metadataReadPath = metadataPath;
            WukongSaveMetadataReadResult read = await readWukongSaveMetadataAsync(metadataPath, cancellationToken);
            refreshedMetadata = read.IsValid ? read.Metadata : null;
            metadataWasRead = true;
        }
        if (!saveGameConfigurationWorkflow.IsCurrentWukongDiscovery(version) ||
            !IsCurrentWukongSelectionOperation(selectionVersion)) return;
        if (metadataWasRead)
        {
            TryApplyBlackMythWukongSaveMetadata(metadataVersion, metadataReadPath!, refreshedMetadata);
        }
        else
        {
            SetBlackMythWukongSaveMetadata(null);
        }
        OnPropertyChanged(nameof(SelectedBlackMythWukongSaveChoice));
    }

    public void BeginBlackMythWukongChange() { if (IsBlackMythWukongSelected) IsBlackMythWukongChangeMode = true; }
    public void CancelBlackMythWukongChange() { if (IsBusy) return; CancelDirectorySelection(GameId.BlackMythWukong); IsBlackMythWukongChangeMode = false; }


    public async Task SelectLiesOfPSaveChoiceAsync(DiscoveredLocalSave? choice, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || choice is null || !LiesOfPSaveChoices.Contains(choice)) return;
        if (HasDirectorySelection(GameId.LiesOfP)) { await SelectDirectoryChoiceAsync(GameId.LiesOfP, choice, cancellationToken); return; }
        ApplyLiesSelectionOutcome(await saveGameConfigurationWorkflow.SelectLiesOfPSaveAsync(choice, cancellationToken));
    }

    public async Task RescanLiesOfPSavesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLiesOfPSelected) return;
        using var resolution = BeginSaveReaderSourceResolution(GameId.LiesOfP);
        if (HasDirectorySelection(GameId.LiesOfP)) { await RescanDirectoryAsync(GameId.LiesOfP, cancellationToken); return; }
        LiesOfPSaveSourceState = LocalSaveSourceState.Scanning;
        SetLiesOfPSaveDiscoveryStatus("Looking for local saves…");
        ApplyLiesSelectionOutcome(await saveGameConfigurationWorkflow.RescanLiesOfPSavesAsync(state?.LiesOfPSave.LocalPath, liesOfPSaveDiscovery, cancellationToken));
    }

    public void BeginLiesOfPChange() { if (IsLiesOfPSelected) IsLiesOfPChangeMode = true; }
    public void CancelLiesOfPChange() { if (IsBusy) return; CancelDirectorySelection(GameId.LiesOfP); IsLiesOfPChangeMode = false; }

    private async Task SaveEldenRingSaveAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try { ApplyCommittedState(await saveGameConfigurationWorkflow.SaveEldenRingAsync(configuration, cancellationToken)); }
        catch { ErrorMessage = "The Elden Ring save selection could not be saved."; }
        finally { IsBusy = false; NotifyTrackerProperties(); }
    }

    private async Task CommitEldenRingSaveAsync(string localPath, DiscoveredLocalSave? discoveredChoice, LocalSaveSourceState sourceState, CancellationToken cancellationToken)
    {
        long version = saveGameConfigurationWorkflow.BeginOperation();
        Interlocked.Increment(ref eldenRingDiscoveryVersion);
        IReadOnlyList<EldenRingProfileSlotChoice> choices = await ReadEldenRingProfileChoicesAsync(localPath, cancellationToken);
        if (!IsCurrentDirectoryOperation(GameId.EldenRing, version)) return;
        bool sameFile = string.Equals(state?.EldenRingSave.LocalPath, localPath, StringComparison.OrdinalIgnoreCase);
        int currentSlot = state?.EldenRingSave.SlotIndex ?? EldenRingSaveConfiguration.NoSlotIndex;
        int selectedSlot = sameFile && choices.Any(choice => choice.Index == currentSlot)
            ? currentSlot
            : choices.Count == 1
                ? choices[0].Index
                : EldenRingSaveConfiguration.NoSlotIndex;
        await SaveEldenRingSaveAsync(new EldenRingSaveConfiguration(localPath, selectedSlot), cancellationToken);
        EldenRingSaveConfiguration? committed = state?.EldenRingSave;
        if (committed is null || !string.Equals(committed.LocalPath, localPath, StringComparison.OrdinalIgnoreCase) || committed.SlotIndex != selectedSlot) return;

        SelectedEldenRingSaveChoice = discoveredChoice;
        if (sourceState == LocalSaveSourceState.AutomaticallySelected) automaticDirectorySources[GameId.EldenRing] = localPath;
        else automaticDirectorySources.Remove(GameId.EldenRing);
        IsEldenRingChangeMode = false;
        EldenRingSaveSourceState = sourceState;
        SetEldenRingSaveDiscoveryStatus(discoveredChoice?.Label ?? CustomSaveTrackingStatus(localPath));
        ApplyEldenRingProfileChoices(choices);
        OnPropertyChanged(nameof(SelectedEldenRingSaveChoice));
        NotifyEldenRingSaveSourceProperties();
    }

    private async Task<IReadOnlyList<EldenRingProfileSlotChoice>> ReadEldenRingProfileChoicesAsync(string localPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<EldenRingCharacterSlotMetadata> metadata;
        try
        {
            metadata = await eldenRingSaveProfileReader.ReadAsync(new EldenRingSaveConfiguration(localPath, EldenRingSaveConfiguration.NoSlotIndex), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { metadata = EldenRingCharacterSlotMetadata.UnavailableSlots; }
        return metadata.Where(static item => !item.IsEmpty).Select(EldenRingProfileSlotChoice.FromMetadata).ToArray();
    }

    private void ApplyEldenRingProfileChoices(IEnumerable<EldenRingProfileSlotChoice> choices)
    {
        EldenRingProfileSlots.Clear();
        foreach (EldenRingProfileSlotChoice choice in choices) EldenRingProfileSlots.Add(choice);
        int selectedIndex = state?.EldenRingSave.SlotIndex ?? EldenRingSaveConfiguration.NoSlotIndex;
        SelectedEldenRingProfileSlot = EldenRingProfileSlots.SingleOrDefault(slot => slot.Index == selectedIndex);
        OnPropertyChanged(nameof(SelectedEldenRingProfileSlot));
        OnPropertyChanged(nameof(CanSelectEldenRingProfile));
        OnPropertyChanged(nameof(EldenRingCharacterStatus));
        OnPropertyChanged(nameof(IsEldenRingMissedDeathAdjustmentAvailable));
        OnPropertyChanged(nameof(CanAdjustEldenRingMissedDeaths));
        OnPropertyChanged(nameof(CanDecrementEldenRingMissedDeaths));
        OnPropertyChanged(nameof(EldenRingMissedDeaths));
        OnPropertyChanged(nameof(GlobalHotkeyUsageDescription));
    }

    private async Task SaveBlackMythWukongSaveAsync(BlackMythWukongSaveConfiguration configuration, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            ApplyCommittedState(
                await saveGameConfigurationWorkflow.SaveWukongAsync(configuration, cancellationToken),
                preserveWukongMetadataOperation: true);
        }
        catch { ErrorMessage = "The Black Myth: Wukong save selection could not be saved."; }
        finally { IsBusy = false; NotifyTrackerProperties(); }
    }

    private async Task SaveLiesOfPSaveAsync(LiesOfPSaveConfiguration configuration, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try { ApplyCommittedState(await saveGameConfigurationWorkflow.SaveLiesOfPAsync(configuration, cancellationToken)); }
        catch { ErrorMessage = "The Lies of P save selection could not be saved."; }
        finally { IsBusy = false; NotifyTrackerProperties(); }
    }

    private void ApplyLiesSelectionOutcome(SaveGameConfigurationWorkflow.LiesSelectionOutcome? outcome)
    {
        if (outcome is null) return;
        if (outcome.Error is not null) { ErrorMessage = outcome.Error; return; }
        if (outcome.CommittedState is not null) ApplyCommittedState(outcome.CommittedState);
        if (outcome.Candidates is { } candidates)
        {
            ProjectDiscoveredDirectory(GameId.LiesOfP, candidates);
            LiesOfPSaveChoices.Clear();
            foreach (DiscoveredLocalSave candidate in candidates) LiesOfPSaveChoices.Add(candidate);
        }
        SelectedLiesOfPSaveChoice = outcome.SelectedChoice;
        if (outcome.SourceState == LocalSaveSourceState.AutomaticallySelected && outcome.SelectedChoice is { } automatic)
            automaticDirectorySources[GameId.LiesOfP] = automatic.LocalPath;
        else if (outcome.ExitChangeMode) automaticDirectorySources.Remove(GameId.LiesOfP);
        LiesOfPSaveSourceState = outcome.SourceState;
        SetLiesOfPSaveDiscoveryStatus(outcome.Status);
        if (outcome.ExitChangeMode) IsLiesOfPChangeMode = false;
        OnPropertyChanged(nameof(SelectedLiesOfPSaveChoice));
        NotifyLiesOfPSaveSourceProperties();
    }

    private static async Task<WukongSaveMetadataReadResult> ReadBlackMythWukongSaveMetadataCoreAsync(
        string localPath,
        CancellationToken cancellationToken)
    {
        var reader = new BlackMythWukongSaveDeathReader();
        reader.Configure(new BlackMythWukongSaveConfiguration(localPath));
        RuntimeGameReadResult? read = await Task.Run(async () => await reader.ReadAsync(cancellationToken), cancellationToken);
        return new(
            read?.Status == RuntimeGameReaderStatus.Synced,
            read?.Status == RuntimeGameReaderStatus.Synced ? read.BlackMythWukongSaveMetadata : null);
    }

    private long BeginWukongSelectionOperation()
    {
        InvalidateWukongMetadataOperations();
        return saveGameConfigurationWorkflow.BeginWukongSelection();
    }

    private long BeginWukongMetadataRead() => saveGameConfigurationWorkflow.BeginWukongMetadataRead();

    private void InvalidateWukongMetadataOperations() => saveGameConfigurationWorkflow.InvalidateWukongMetadataReads();

    private void InvalidateWukongOperations()
    {
        saveGameConfigurationWorkflow.InvalidateWukongSelections();
        InvalidateWukongMetadataOperations();
    }

    private bool IsCurrentWukongSelectionOperation(long version, string? expectedPath = null) =>
        saveGameConfigurationWorkflow.IsCurrentWukongSelection(version) &&
        IsBlackMythWukongSelected &&
        (expectedPath is null || PathsEqual(expectedPath, state?.BlackMythWukongSave.LocalPath));

    private void TryApplyBlackMythWukongSaveMetadata(
        long version,
        string expectedPath,
        BlackMythWukongSaveMetadata? metadata)
    {
        if (!saveGameConfigurationWorkflow.IsCurrentWukongMetadataRead(version) ||
            !IsBlackMythWukongSelected ||
            !PathsEqual(expectedPath, state?.BlackMythWukongSave.LocalPath))
        {
            return;
        }

        SetBlackMythWukongSaveMetadata(metadata);
    }

    private static string CustomSaveTrackingStatus(string localPath)
    {
        return Path.GetFileName(localPath);
    }

    private void ReconcileWukongSaveSourceFromCommittedState()
    {
        if (!IsBlackMythWukongSelected) return;
        string? localPath = state?.BlackMythWukongSave.LocalPath;
        SelectedBlackMythWukongSaveChoice = BlackMythWukongSaveChoices.SingleOrDefault(
            choice => PathsEqual(choice.LocalPath, localPath));
        if (SelectedBlackMythWukongSaveChoice is { } discovered)
        {
            WukongSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
            SetBlackMythWukongSaveDiscoveryStatus(discovered.Label);
        }
        else if (localPath is null)
        {
            WukongSaveSourceState = BlackMythWukongSaveChoices.Count > 1
                ? LocalSaveSourceState.MultipleCandidates
                : LocalSaveSourceState.NoCandidate;
            SetBlackMythWukongSaveDiscoveryStatus(
                BlackMythWukongSaveChoices.Count > 1
                    ? ChooseCharacterMessage
                    : MissingSaveDirectoryMessage);
        }
        else if (File.Exists(localPath))
        {
            WukongSaveSourceState = LocalSaveSourceState.CustomSelection;
            SetBlackMythWukongSaveDiscoveryStatus(CustomSaveTrackingStatus(localPath));
        }
        else
        {
            WukongSaveSourceState = LocalSaveSourceState.UnavailableSelection;
            SetBlackMythWukongSaveDiscoveryStatus("Selected save is unavailable.");
        }

        OnPropertyChanged(nameof(SelectedBlackMythWukongSaveChoice));
        NotifyWukongSaveSourceProperties();
    }

    private void SetBlackMythWukongSaveMetadata(BlackMythWukongSaveMetadata? value)
    {
        if (Equals(blackMythWukongSaveMetadata, value)) return;
        blackMythWukongSaveMetadata = value;
        OnPropertyChanged(nameof(BlackMythWukongSaveMetadataText));
    }

    internal static string? FormatBlackMythWukongSaveMetadata(
        BlackMythWukongSaveMetadata? metadata,
        CultureInfo culture,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (metadata is null) return null;

        var values = new List<string>(3);
        if (metadata.Level is > 0)
        {
            values.Add($"Level {metadata.Level.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (metadata.TotalPlayTime is { } playTime && playTime >= TimeSpan.Zero)
        {
            long totalMinutes = checked((long)Math.Floor(playTime.TotalMinutes));
            string duration = playTime.Days > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{playTime.Days}d {playTime.Hours}h {playTime.Minutes}m")
                : totalMinutes >= 60
                    ? string.Create(CultureInfo.InvariantCulture, $"{totalMinutes / 60}h {totalMinutes % 60}m")
                    : string.Create(CultureInfo.InvariantCulture, $"{totalMinutes}m");
            values.Add($"Play time {duration}");
        }

        if (metadata.LastSaved is { } lastSaved)
        {
            try
            {
                DateTimeOffset localSaved = TimeZoneInfo.ConvertTime(lastSaved, timeProvider.LocalTimeZone);
                DateTimeOffset localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
                string display = localSaved.Date == localNow.Date
                    ? $"Today, {localSaved.ToString("t", culture)}"
                    : localSaved.ToString("g", culture);
                values.Add($"Last saved {display}");
            }
            catch (ArgumentException)
            {
                // Independently omit timestamps that cannot be represented in the local timezone.
            }
        }

        return values.Count == 0 ? null : string.Join(" • ", values);
    }

    private async Task RefreshEldenRingProfileSlotsAsync(CancellationToken cancellationToken, bool clearStaleSelection = false)
    {
        string? localPath = state?.EldenRingSave.LocalPath;
        IReadOnlyList<EldenRingProfileSlotChoice>? choices = localPath is null
            ? []
            : await SaveGameConfigurationWorkflow.ReadEldenRingProfileSlotsAsync(eldenRingSaveProfileReader, localPath, cancellationToken);
        ApplyEldenRingProfileChoices(choices ?? []);
        if (clearStaleSelection
            && choices is not null
            && localPath is not null
            && state!.EldenRingSave.SlotIndex != EldenRingSaveConfiguration.NoSlotIndex
            && SelectedEldenRingProfileSlot is null)
        {
            await SaveEldenRingSaveAsync(new EldenRingSaveConfiguration(localPath, EldenRingSaveConfiguration.NoSlotIndex, state?.EldenRingSave.SelectedDirectory), cancellationToken);
            ApplyEldenRingProfileChoices(choices);
        }
    }

    private static bool IsAvailableEldenRingSaveFile(string? localPath) =>
        localPath is not null
        && string.Equals(Path.GetFileName(localPath), "ER0000.sl2", StringComparison.OrdinalIgnoreCase)
        && File.Exists(localPath);

    private void NotifyEldenRingSaveSourceProperties()
    {
        NotifyDirectoryPaths();
        OnPropertyChanged(nameof(IsEldenRingSaveSelectorVisible));
        OnPropertyChanged(nameof(IsEldenRingSaveSelectorEnabled));
        OnPropertyChanged(nameof(IsEldenRingBrowseVisible));
        OnPropertyChanged(nameof(IsEldenRingChangeVisible));
        OnPropertyChanged(nameof(IsEldenRingCancelVisible));
        OnPropertyChanged(nameof(EldenRingCharacterStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void SetEldenRingSaveDiscoveryStatus(string? value)
    {
        if (string.Equals(eldenRingSaveDiscoveryStatus, value, StringComparison.Ordinal)) return;
        eldenRingSaveDiscoveryStatus = value;
        NotifyDirectoryPaths();
        OnPropertyChanged(nameof(EldenRingSaveDiscoveryStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void NotifyWukongSaveSourceProperties()
    {
        NotifyDirectoryPaths();
        OnPropertyChanged(nameof(IsWukongSaveSelectorVisible));
        OnPropertyChanged(nameof(IsWukongBrowseVisible));
        OnPropertyChanged(nameof(IsWukongChangeVisible));
        OnPropertyChanged(nameof(IsWukongCancelVisible));
        OnPropertyChanged(nameof(IsWukongSaveSelectorEnabled));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void SetBlackMythWukongSaveDiscoveryStatus(string? value)
    {
        if (string.Equals(blackMythWukongSaveDiscoveryStatus, value, StringComparison.Ordinal)) return;
        blackMythWukongSaveDiscoveryStatus = value;
        NotifyDirectoryPaths();
        OnPropertyChanged(nameof(BlackMythWukongSaveDiscoveryStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void NotifyLiesOfPSaveSourceProperties()
    {
        NotifyDirectoryPaths();
        OnPropertyChanged(nameof(IsLiesOfPSaveSelectorVisible));
        OnPropertyChanged(nameof(IsLiesOfPBrowseVisible));
        OnPropertyChanged(nameof(IsLiesOfPChangeVisible));
        OnPropertyChanged(nameof(IsLiesOfPCancelVisible));
        OnPropertyChanged(nameof(IsLiesOfPSaveSelectorEnabled));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void SetLiesOfPSaveDiscoveryStatus(string? value)
    {
        if (string.Equals(liesOfPSaveDiscoveryStatus, value, StringComparison.Ordinal)) return;
        liesOfPSaveDiscoveryStatus = value;
        NotifyDirectoryPaths();
        OnPropertyChanged(nameof(LiesOfPSaveDiscoveryStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }
}
