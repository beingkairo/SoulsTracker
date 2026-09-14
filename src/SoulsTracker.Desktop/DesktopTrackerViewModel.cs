using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Net.Http;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

internal readonly record struct WukongSaveMetadataReadResult(
    bool IsValid,
    BlackMythWukongSaveMetadata? Metadata);

/// <summary>Projects persisted tracker state into the small P3-01 desktop surface.</summary>
public sealed class DesktopTrackerViewModel : INotifyPropertyChanged
{
    internal const string LocalTrackerStateReadyMessage = "Local tracker state is ready.";
    internal const string LocalTrackerStateUnavailableMessage = "Local tracker state is unavailable. Tracker controls remain disabled.";
    internal const string LocalOverlayReadyMessage = "Local OBS overlay is ready.";
    internal const string LocalOverlayUnavailableMessage = "Local OBS overlay is unavailable. Local tracker controls remain available.";
    internal const string GameUnavailableMessage = "Game unavailable";
    internal const string GameWaitingForActiveCharacterMessage = "Game detected — waiting for active character";
    internal const string GameWaitingForSaveFileMessage = "Choose an Elden Ring save file";
    internal const string BlackMythWukongWaitingForSaveFileMessage = "Choose a Black Myth: Wukong save file";
    internal const string LiesOfPWaitingForSaveFileMessage = "Choose a Lies of P save file";
    internal const string EldenRingChooseCharacterMessage = "Choose a character to continue.";
    internal const string GameSyncedMessage = "Synced";
    internal const string NoDeathsRecordedMessage = "No deaths recorded yet — the tracker will update after your first saved death.";
    internal const string SelectedSaveUnreadableMessage = "Selected save cannot currently be read. Save in-game, then Rescan or change the selected save.";
    internal const string GameTotalDeathsUnavailableMessage = "Unable to read total deaths.";
    internal const string GameTotalDeathsWaitingForActiveCharacterMessage = "Unavailable — waiting for active character.";


    private readonly SerializedTrackerCoordinator coordinator;
    private readonly SaveGameConfigurationWorkflow saveGameConfigurationWorkflow;
    private readonly IEldenRingSaveProfileReader eldenRingSaveProfileReader;
    private readonly ILocalSaveDiscovery eldenRingSaveDiscovery;
    private readonly ILocalSaveDiscovery blackMythWukongSaveDiscovery;
    private readonly ILocalSaveDiscovery liesOfPSaveDiscovery;
    private static readonly HttpClient ManualUpdateHttpClient = new();
    private readonly IManualReleaseUpdateChecker manualReleaseUpdateChecker;
    private readonly Func<string> installedVersionProvider;
    private readonly IUpdateReleasePageLauncher updateReleasePageLauncher;
    private bool isCheckingForUpdates;
    private bool hasCheckedForUpdates;
    private string? updateCurrentVersion;
    private string? updateLatestVersion;
    private string? updateCheckStatus;
    private Uri? availableUpdateReleasePage;
    private bool updateCheckCanRetry;
    private PersistentTrackerState? state;
    private RuntimeGameObservation? runtimeObservation;
    private RuntimeGameReaderStatus runtimeReaderStatus;
    private bool runtimeReaderHasNoRecordedDeaths;
    private GameId? runtimeReaderGameId;
    private bool isLoading = true;
    private bool isBusy;
    private string? errorMessage;
    private string totalDeathsText = "Load tracker state before using controls.";
    private string? totalDeathsOverlayUrl;

    private string? globalHotkeyStatus;
    private string? localTrackerStateStatus;
    private string? localOverlayStatus;
    private bool isTotalDeathsOverlayEnabled;
    private bool showTotalDeathsGameName;

    private LegacyImportViewModel? legacyImport;
    private GlobalHotkeySettings hotkeySettings = GlobalHotkeySettings.Default;
    private string pendingIncrementHotkey = GlobalHotkeyBinding.IncrementDefault.DisplayText;
    private string pendingDecrementHotkey = GlobalHotkeyBinding.DecrementDefault.DisplayText;
    private GlobalHotkeyBinding pendingIncrementBinding = GlobalHotkeyBinding.IncrementDefault;
    private GlobalHotkeyBinding pendingDecrementBinding = GlobalHotkeyBinding.DecrementDefault;
    private bool isHotkeyRecording;
    private bool isEldenRingNoticeVisible;
    private GameChoice? pendingEldenRingChoice;
    private bool recordingIncrementHotkey;
    private GlobalHotkeyBinding? hotkeyBindingBeforeRecording;
    private Func<GlobalHotkeySettings, Task<GlobalHotkeyRegistrationResult>>? applyHotkeysAsync;
    private string? textExportStatus;
    private OverlayTitleIconModeChoice draftTitleIconModeChoice = OverlayTitleIconModeChoice.All[0];

    private string? totalDeathsAppearanceStatus;


    private bool legacyDraftShowGameName;
    private bool legacyDraftCompactTitle = true;

    private readonly object eldenRingProfileSlotsSynchronization = new();
    private readonly object eldenRingSaveChoicesSynchronization = new();
    private readonly object blackMythWukongSaveChoicesSynchronization = new();
    private readonly object liesOfPSaveChoicesSynchronization = new();
    private long blackMythWukongDiscoveryVersion;
    private long liesOfPSelectionVersion;
    private LocalSaveSourceState wukongSaveSourceState;
    private bool isBlackMythWukongChangeMode;
    private string? blackMythWukongSaveDiscoveryStatus;
    private string? liesOfPSaveDiscoveryStatus;
    private LocalSaveSourceState liesOfPSaveSourceState;
    private bool isLiesOfPChangeMode;
    private BlackMythWukongSaveMetadata? blackMythWukongSaveMetadata;
    private readonly TimeProvider timeProvider;
    private readonly Func<string, CancellationToken, Task<WukongSaveMetadataReadResult>> readWukongSaveMetadataAsync;
    private long wukongMetadataOperationVersion;
    private long wukongSelectionOperationVersion;
    private long eldenRingDiscoveryVersion;
    private LocalSaveSourceState eldenRingSaveSourceState;
    private bool isEldenRingChangeMode;
    private string? eldenRingSaveDiscoveryStatus;

    public DesktopTrackerViewModel(
        SerializedTrackerCoordinator coordinator,
        IEldenRingSaveProfileReader? eldenRingSaveProfileReader = null,
        ILocalSaveDiscovery? blackMythWukongSaveDiscovery = null,
        ILocalSaveDiscovery? eldenRingSaveDiscovery = null,
        TimeProvider? timeProvider = null,
        ILocalSaveDiscovery? liesOfPSaveDiscovery = null,
        IManualReleaseUpdateChecker? manualReleaseUpdateChecker = null,
        Func<string>? installedVersionProvider = null,
        IUpdateReleasePageLauncher? updateReleasePageLauncher = null)
        : this(
            coordinator,
            eldenRingSaveProfileReader,
            blackMythWukongSaveDiscovery,
            eldenRingSaveDiscovery,
            timeProvider,
            ReadBlackMythWukongSaveMetadataCoreAsync,
            liesOfPSaveDiscovery,
            manualReleaseUpdateChecker,
            installedVersionProvider,
            updateReleasePageLauncher)
    {
    }

    internal DesktopTrackerViewModel(
        SerializedTrackerCoordinator coordinator,
        IEldenRingSaveProfileReader? eldenRingSaveProfileReader,
        ILocalSaveDiscovery? blackMythWukongSaveDiscovery,
        ILocalSaveDiscovery? eldenRingSaveDiscovery,
        TimeProvider? timeProvider,
        Func<string, CancellationToken, Task<WukongSaveMetadataReadResult>> readWukongSaveMetadataAsync,
        ILocalSaveDiscovery? liesOfPSaveDiscovery = null,
        IManualReleaseUpdateChecker? manualReleaseUpdateChecker = null,
        Func<string>? installedVersionProvider = null,
        IUpdateReleasePageLauncher? updateReleasePageLauncher = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        saveGameConfigurationWorkflow = new SaveGameConfigurationWorkflow(this.coordinator);
        this.eldenRingSaveProfileReader = eldenRingSaveProfileReader ?? new EldenRingSaveProfileReader();
        this.eldenRingSaveDiscovery = eldenRingSaveDiscovery ?? new EldenRingSaveDiscovery();
        this.blackMythWukongSaveDiscovery = blackMythWukongSaveDiscovery ?? new BlackMythWukongSaveDiscovery();
        this.liesOfPSaveDiscovery = liesOfPSaveDiscovery ?? new LiesOfPSaveDiscovery();
        this.manualReleaseUpdateChecker = manualReleaseUpdateChecker ?? new GitHubLatestReleaseUpdateChecker(ManualUpdateHttpClient);
        this.installedVersionProvider = installedVersionProvider ?? CurrentInstalledVersion;
        this.updateReleasePageLauncher = updateReleasePageLauncher ?? new ShellUpdateReleasePageLauncher();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.readWukongSaveMetadataAsync = readWukongSaveMetadataAsync ?? throw new ArgumentNullException(nameof(readWukongSaveMetadataAsync));
        GameChoices = new ObservableCollection<GameChoice>(GameCatalog.All.Select(static game => new GameChoice(game)));
        EldenRingProfileSlots = [];
        EldenRingSaveChoices = [];
        BlackMythWukongSaveChoices = [];
        LiesOfPSaveChoices = [];

        BindingOperations.EnableCollectionSynchronization(EldenRingProfileSlots, eldenRingProfileSlotsSynchronization);
        BindingOperations.EnableCollectionSynchronization(EldenRingSaveChoices, eldenRingSaveChoicesSynchronization);
        BindingOperations.EnableCollectionSynchronization(BlackMythWukongSaveChoices, blackMythWukongSaveChoicesSynchronization);
        BindingOperations.EnableCollectionSynchronization(LiesOfPSaveChoices, liesOfPSaveChoicesSynchronization);

    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCheckingForUpdates { get => isCheckingForUpdates; private set { if (SetField(ref isCheckingForUpdates, value)) { OnPropertyChanged(nameof(CanCheckForUpdates)); OnPropertyChanged(nameof(CanRetryUpdateCheck)); OnPropertyChanged(nameof(CanOpenAvailableUpdateReleasePage)); } } }
    public bool HasCheckedForUpdates { get => hasCheckedForUpdates; private set => SetField(ref hasCheckedForUpdates, value); }
    public string? UpdateCurrentVersion { get => updateCurrentVersion; private set => SetField(ref updateCurrentVersion, value); }
    public string? UpdateLatestVersion { get => updateLatestVersion; private set => SetField(ref updateLatestVersion, value); }
    public string? UpdateCheckStatus { get => updateCheckStatus; private set => SetField(ref updateCheckStatus, value); }
    public Uri? AvailableUpdateReleasePage { get => availableUpdateReleasePage; private set { if (SetField(ref availableUpdateReleasePage, value)) OnPropertyChanged(nameof(CanOpenAvailableUpdateReleasePage)); } }
    public bool CanCheckForUpdates => ControlsEnabled && !IsCheckingForUpdates;
    public bool CanRetryUpdateCheck => CanCheckForUpdates && updateCheckCanRetry;
    public bool CanOpenAvailableUpdateReleasePage => !IsCheckingForUpdates && AvailableUpdateReleasePage is not null;

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCheckForUpdates) return;
        IsCheckingForUpdates = true;
        updateCheckCanRetry = false;
        AvailableUpdateReleasePage = null;
        string installedVersion = NormalizeProductVersion(installedVersionProvider());
        UpdateCurrentVersion = installedVersion;
        UpdateLatestVersion = "Checking…";
        HasCheckedForUpdates = true;
        UpdateCheckStatus = "Checking for updates…";
        try
        {
            ManualReleaseUpdateResult result = await manualReleaseUpdateChecker.CheckAsync(installedVersion, cancellationToken);
            switch (result.Status)
            {
                case ManualReleaseUpdateStatus.UpToDate: UpdateLatestVersion = result.AvailableVersion ?? UpdateCurrentVersion; UpdateCheckStatus = "All up to date."; break;
                case ManualReleaseUpdateStatus.UpdateAvailable: UpdateLatestVersion = result.AvailableVersion ?? "Unavailable"; UpdateCheckStatus = "New version out!"; AvailableUpdateReleasePage = result.ReleasePage; break;
                case ManualReleaseUpdateStatus.RateLimited: SetRetryableUpdateFailure("GitHub asked you to try again later."); break;
                case ManualReleaseUpdateStatus.InvalidResponse or ManualReleaseUpdateStatus.InvalidInstalledVersion: SetRetryableUpdateFailure("Update information could not be verified. Try again or open the official Releases page."); break;
                default: SetRetryableUpdateFailure("Couldn’t reach GitHub right now. Check your connection and try again."); break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { UpdateLatestVersion = "Unavailable"; UpdateCheckStatus = "Update check cancelled. Try again when you’re ready."; updateCheckCanRetry = true; }
        finally { IsCheckingForUpdates = false; OnPropertyChanged(nameof(CanRetryUpdateCheck)); }
    }

    public void OpenAvailableUpdateReleasePage()
    {
        if (!CanOpenAvailableUpdateReleasePage || AvailableUpdateReleasePage is not { } page) return;
        UpdateCheckStatus = updateReleasePageLauncher.TryOpen(page)
            ? "Opening the official release page…"
            : "The official release page could not be opened. Try again or open GitHub Releases in your browser.";
    }

    private void SetRetryableUpdateFailure(string status)
    {
        updateCheckCanRetry = true;
        AvailableUpdateReleasePage = new Uri("https://github.com/beingkairo/SoulsTracker/releases");
        UpdateLatestVersion = "Unavailable";
        UpdateCheckStatus = status;
    }

    private static string CurrentInstalledVersion() => typeof(DesktopTrackerViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static string NormalizeProductVersion(string version) =>
        ReleaseSemanticVersion.TryParse(version, out ReleaseSemanticVersion? parsed)
            ? parsed!.ToString()
            : version;


    public ObservableCollection<GameChoice> GameChoices { get; }

    public ObservableCollection<EldenRingProfileSlotChoice> EldenRingProfileSlots { get; }
    public IReadOnlyList<OverlayTextAlignment> OverlayAlignments { get; } = Enum.GetValues<OverlayTextAlignment>();
    public IReadOnlyList<string> LocalFontFamilies { get; } = GetLocalFontFamilies();

    private static string[] GetLocalFontFamilies()
    {
        try { return Fonts.SystemFontFamilies.Select(static font => font.Source).Where(static name => !string.IsNullOrWhiteSpace(name)).Append("Segoe UI").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return ["Segoe UI"]; }
    }
    public OverlayAppearanceDraft TotalDeathsAppearanceDraft { get; } = new();

    // Legacy test/binding compatibility; V1 no longer exposes editable controls for these choices.
    public bool DraftShowGameName { get => legacyDraftShowGameName; set => SetField(ref legacyDraftShowGameName, value); }
    public bool DraftCompactTitle { get => legacyDraftCompactTitle; set => SetField(ref legacyDraftCompactTitle, value); }
    public IReadOnlyList<OverlayTitleIconModeChoice> TitleIconModes { get; } = OverlayTitleIconModeChoice.All;
    public OverlayTitleIconModeChoice DraftTitleIconModeChoice { get => draftTitleIconModeChoice; set { if (SetField(ref draftTitleIconModeChoice, value)) OnPropertyChanged(nameof(IsTitleIconSelected)); } }

    public bool IsTitleIconSelected => DraftTitleIconModeChoice.Value != OverlayTitleIconMode.Off;
    public string DraftCheckmarkAccent { get; set; } = "#A78BFA";
    public string DraftMaximumVisibleCount { get; set; } = "25";

    public GameChoice? SelectedGame { get; private set; }
    public string GameSelectionAutomationName => SelectedGame is null
        ? "Game selection"
        : $"Game selection: {SelectedGame.DisplayName}";

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (SetField(ref isLoading, value))
            {
                OnPropertyChanged(nameof(ControlsEnabled));
                OnPropertyChanged(nameof(CanCheckForUpdates));
                OnPropertyChanged(nameof(CanRetryUpdateCheck));
                OnPropertyChanged(nameof(CanSelectEldenRingProfile));
                NotifyTextExportControlAvailability();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                OnPropertyChanged(nameof(ControlsEnabled));
                OnPropertyChanged(nameof(CanCheckForUpdates));
                OnPropertyChanged(nameof(CanRetryUpdateCheck));
                OnPropertyChanged(nameof(PresentationControlsEnabled));
                OnPropertyChanged(nameof(CanConfigureTotalDeathsGameName));
                OnPropertyChanged(nameof(CanSelectEldenRingProfile));
                NotifyTextExportControlAvailability();
            }
        }
    }

    public bool ControlsEnabled => state is not null && !IsLoading && !IsBusy;

    public bool PresentationControlsEnabled => ControlsEnabled;

    public bool CanConfigureTotalDeathsGameName => ControlsEnabled && IsTotalDeathsOverlayEnabled;

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetField(ref errorMessage, value);
    }

    public string TotalDeathsText
    {
        get => totalDeathsText;
        private set
        {
            if (SetField(ref totalDeathsText, value))
            {
                OnPropertyChanged(nameof(IsTotalDeathsValueNumeric));
            }
        }
    }

    /// <summary>Controls whether the Main-tab Total Deaths text uses counter or compact status presentation.</summary>
    public bool IsTotalDeathsValueNumeric => long.TryParse(TotalDeathsText, out _);

    public string? RuntimeReaderStatusText
    {
        get
        {
            GameId? selectedGameId = state?.SelectedGameId;
            if (selectedGameId is null || selectedGameId == GameId.DemonsSouls)
            {
                return null;
            }

            if (runtimeReaderHasNoRecordedDeaths)
            {
                return NoDeathsRecordedMessage;
            }

            if (runtimeReaderStatus == RuntimeGameReaderStatus.SelectedSaveUnreadable)
            {
                return SelectedSaveUnreadableMessage;
            }

            if (selectedGameId == GameId.BlackMythWukong && runtimeReaderStatus != RuntimeGameReaderStatus.Synced)
            {
                return BlackMythWukongSaveDiscoveryStatus ?? WaitingForSaveFileMessage(selectedGameId);
            }
            if (selectedGameId == GameId.LiesOfP && runtimeReaderStatus != RuntimeGameReaderStatus.Synced)
            {
                return LiesOfPSaveDiscoveryStatus ?? WaitingForSaveFileMessage(selectedGameId);
            }
            if (selectedGameId == GameId.EldenRing && runtimeReaderStatus != RuntimeGameReaderStatus.Synced)
            {
                if (state?.EldenRingSave.LocalPath is null) return EldenRingSaveDiscoveryStatus ?? GameWaitingForSaveFileMessage;
                if (state.EldenRingSave.SlotIndex == EldenRingSaveConfiguration.NoSlotIndex) return EldenRingChooseCharacterMessage;
                return EldenRingSaveDiscoveryStatus ?? GameUnavailableMessage;
            }

            return runtimeReaderStatus switch
            {
                RuntimeGameReaderStatus.WaitingForActiveCharacter => GameWaitingForActiveCharacterMessage,
                RuntimeGameReaderStatus.WaitingForSaveFile => WaitingForSaveFileMessage(selectedGameId),
                RuntimeGameReaderStatus.Synced => GameSyncedMessage,
                _ => GameUnavailableMessage,
            };
        }
    }

    public long ManualDeaths => state?.SelectedGameId is GameId selectedGame && IsManualGame(selectedGame)
        ? state.GetManualDeathCounter(selectedGame).Value
        : 0;

    public bool IsBloodborneSelected => state?.SelectedGameId == GameId.Bloodborne;
    public bool IsEldenRingSelected => state?.SelectedGameId == GameId.EldenRing;
    public ObservableCollection<DiscoveredLocalSave> EldenRingSaveChoices { get; }
    public DiscoveredLocalSave? SelectedEldenRingSaveChoice { get; private set; }
    public string? EldenRingSaveDiscoveryStatus => eldenRingSaveDiscoveryStatus;
    public LocalSaveSourceState EldenRingSaveSourceState { get => eldenRingSaveSourceState; private set { if (SetField(ref eldenRingSaveSourceState, value)) NotifyEldenRingSaveSourceProperties(); } }
    public bool IsEldenRingChangeMode { get => isEldenRingChangeMode; private set { if (SetField(ref isEldenRingChangeMode, value)) NotifyEldenRingSaveSourceProperties(); } }
    public bool IsEldenRingSaveSelectorVisible => EldenRingSaveChoices.Count > 0 && (IsEldenRingChangeMode || EldenRingSaveSourceState == LocalSaveSourceState.MultipleCandidates);
    public bool IsEldenRingSaveSelectorEnabled => ControlsEnabled && EldenRingSaveSourceState != LocalSaveSourceState.Scanning && IsEldenRingSaveSelectorVisible;
    public bool IsEldenRingBrowseVisible => EldenRingSaveSourceState is LocalSaveSourceState.Scanning or LocalSaveSourceState.NoCandidate or LocalSaveSourceState.MultipleCandidates || IsEldenRingChangeMode;
    public bool IsEldenRingChangeVisible => !IsEldenRingChangeMode && EldenRingSaveSourceState is LocalSaveSourceState.AutomaticallySelected or LocalSaveSourceState.PersistedDiscovered or LocalSaveSourceState.CustomSelection or LocalSaveSourceState.UnavailableSelection;
    public bool IsEldenRingCancelVisible => IsEldenRingChangeMode;
    public string? EldenRingCharacterStatus => IsEldenRingSelected && state?.EldenRingSave.LocalPath is not null && SelectedEldenRingProfileSlot is null ? EldenRingChooseCharacterMessage : null;
    public bool IsBlackMythWukongSelected => state?.SelectedGameId == GameId.BlackMythWukong;
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
    public bool IsLiesOfPSelected => state?.SelectedGameId == GameId.LiesOfP;
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
    public bool IsManualGameSelected => state?.SelectedGameId is GameId id && IsManualGame(id);
    /// <summary>True when the shared global hotkey configuration applies to the current game view.</summary>
    public bool IsGlobalHotkeyConfigurationAvailable => IsManualGameSelected || IsEldenRingSelected;
    public string GlobalHotkeyUsageDescription => IsManualGameSelected
        ? "Use these global hotkeys to adjust the active manual death total."
        : IsEldenRingMissedDeathAdjustmentAvailable
            ? "Use these global hotkeys to add or remove missed deaths for the selected character."
            : "Choose a local save and character before global hotkeys can adjust missed deaths.";

    public bool CanDecrementManualDeaths => IsManualGameSelected && ManualDeaths > 0 && ControlsEnabled;
    public bool IsEldenRingMissedDeathAdjustmentAvailable => IsEldenRingSelected
        && state is not null
        && state.EldenRingSave.LocalPath is not null
        && SelectedEldenRingProfileSlot is not null;
    public bool CanAdjustEldenRingMissedDeaths => IsEldenRingMissedDeathAdjustmentAvailable && ControlsEnabled;
    public bool CanDecrementEldenRingMissedDeaths => CanAdjustEldenRingMissedDeaths && EldenRingMissedDeaths > 0;
    public long EldenRingMissedDeaths => state is not null && IsEldenRingMissedDeathAdjustmentAvailable
        ? state.EldenRingMissedDeathAdjustments.Get(state.EldenRingSave)
        : 0;
    public string EldenRingSavedDeathsText => runtimeObservation?.GameId == GameId.EldenRing
        ? runtimeObservation.TotalDeaths.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "Unavailable";
    public string? TotalDeathsOverlayUrl { get => totalDeathsOverlayUrl; private set { if (SetField(ref totalDeathsOverlayUrl, value)) { OnPropertyChanged(nameof(TotalDeathsSceneUrl)); OnPropertyChanged(nameof(TotalDeathsSceneUrlDisplay)); } } }

    /// <summary>Each generated URL contains only its own bounded, applied presentation values.</summary>
    public string? TotalDeathsSceneUrl => AppendStyleQuery(TotalDeathsOverlayUrl, totalDeaths: true);

    /// <summary>Safe, compact presentation of a canonical URL. Copy always uses the full URL.</summary>
    public string? TotalDeathsSceneUrlDisplay => ShortenUrlForDisplay(TotalDeathsSceneUrl);
    /// <summary>Safe, compact presentation of a canonical URL. Copy always uses the full URL.</summary>



    public string? GlobalHotkeyStatus { get => globalHotkeyStatus; private set => SetField(ref globalHotkeyStatus, value); }
    public string PendingIncrementHotkey { get => pendingIncrementHotkey; set => SetField(ref pendingIncrementHotkey, value); }
    public string PendingDecrementHotkey { get => pendingDecrementHotkey; set => SetField(ref pendingDecrementHotkey, value); }
    /// <summary>True while the in-app manual-hotkey capture surface owns keyboard input.</summary>
    public bool IsHotkeyRecording { get => isHotkeyRecording; private set => SetField(ref isHotkeyRecording, value); }
    /// <summary>True while the Elden Ring acknowledgement gate is visible.</summary>
    public bool IsEldenRingNoticeVisible { get => isEldenRingNoticeVisible; private set => SetField(ref isEldenRingNoticeVisible, value); }
    public string ActiveIncrementHotkey => hotkeySettings.Increment.DisplayText;
    public string ActiveDecrementHotkey => hotkeySettings.Decrement.DisplayText;
    public string? LocalTrackerStateStatus { get => localTrackerStateStatus; private set => SetField(ref localTrackerStateStatus, value); }
    public string? LocalOverlayStatus { get => localOverlayStatus; private set => SetField(ref localOverlayStatus, value); }
    public bool IsTotalDeathsOverlayEnabled { get => isTotalDeathsOverlayEnabled; private set => SetField(ref isTotalDeathsOverlayEnabled, value); }
    public bool ShowTotalDeathsGameName { get => showTotalDeathsGameName; private set => SetField(ref showTotalDeathsGameName, value); }

    public LegacyImportViewModel? LegacyImport { get => legacyImport; private set => SetField(ref legacyImport, value); }
    public bool HasActiveLegacyImport => LegacyImport is { OfferVisible: true } or { ReviewVisible: true };
    public string? TotalDeathsAppearanceStatus { get => totalDeathsAppearanceStatus; private set => SetField(ref totalDeathsAppearanceStatus, value); }

    public string? TextExportStatus { get => textExportStatus; private set => SetField(ref textExportStatus, value); }
    public string? DeathsExportFileName => state?.TextExports.DeathsPath is { } path ? Path.GetFileName(path) : null;

    public bool IsDeathsExportEnabled => state?.TextExports.DeathsEnabled ?? false;

    public bool CanChooseDeathsExport => ControlsEnabled && IsDeathsExportEnabled;
    public bool CanClearDeathsExport => CanChooseDeathsExport && state?.TextExports.DeathsPath is not null;

    internal PersistentTrackerState? CurrentState => state;
    internal void ApplyRuntimeReaderResult(RuntimeGameReadResult? result)
    {
        if (result is
            {
                GameId: var resultGameId,
                BlackMythWukongSavePath: { } resultPath,
            } &&
            resultGameId == GameId.BlackMythWukong &&
            state?.SelectedGameId == GameId.BlackMythWukong &&
            !PathsEqual(resultPath, state.BlackMythWukongSave.LocalPath))
        {
            return;
        }

        InvalidateWukongMetadataOperations();
        if (state is null)
        {
            runtimeReaderGameId = null;
            runtimeReaderStatus = RuntimeGameReaderStatus.Unavailable;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            SetBlackMythWukongSaveMetadata(null);
            UpdateTotalDeathsText();
            OnPropertyChanged(nameof(RuntimeReaderStatusText));
            return;
        }

        bool blackMythWukongSaveIsUnconfigured = state.SelectedGameId == GameId.BlackMythWukong && state.BlackMythWukongSave.LocalPath is null;
        bool liesOfPSaveIsUnconfigured = state.SelectedGameId == GameId.LiesOfP && state.LiesOfPSave.LocalPath is null;
        runtimeReaderGameId = result?.GameId;
        if (result is not null && result.GameId == state.SelectedGameId && !blackMythWukongSaveIsUnconfigured && !liesOfPSaveIsUnconfigured)
        {
            runtimeReaderStatus = result.Status;
            runtimeReaderHasNoRecordedDeaths = result.HasNoRecordedDeaths;
            runtimeObservation = result.Observation;
            SetBlackMythWukongSaveMetadata(
                result.GameId == GameId.BlackMythWukong &&
                result.Status == RuntimeGameReaderStatus.Synced &&
                result.BlackMythWukongSaveMetadata is not null &&
                result.BlackMythWukongSavePath is { } metadataPath &&
                PathsEqual(metadataPath, state.BlackMythWukongSave.LocalPath)
                    ? result.BlackMythWukongSaveMetadata
                    : null);
        }
        else if (blackMythWukongSaveIsUnconfigured)
        {
            runtimeReaderGameId = GameId.BlackMythWukong;
            runtimeReaderStatus = RuntimeGameReaderStatus.WaitingForSaveFile;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }
        else if (liesOfPSaveIsUnconfigured)
        {
            runtimeReaderGameId = GameId.LiesOfP;
            runtimeReaderStatus = RuntimeGameReaderStatus.WaitingForSaveFile;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }
        else
        {
            runtimeReaderStatus = RuntimeGameReaderStatus.Unavailable;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }

        UpdateTotalDeathsText();
        OnPropertyChanged(nameof(EldenRingSavedDeathsText));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    internal void ApplyRuntimeObservation(RuntimeGameObservation? observation) =>
        ApplyRuntimeReaderResult(observation is null ? null : RuntimeGameReadResult.Synced(observation));
    internal void ConfigureLegacyImport(LegacyImportViewModel workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (LegacyImport is not null) LegacyImport.PropertyChanged -= LegacyImport_PropertyChanged;
        LegacyImport = workflow;
        workflow.PropertyChanged += LegacyImport_PropertyChanged;
        OnPropertyChanged(nameof(HasActiveLegacyImport));
    }
    internal void ApplyImportedCommittedState(PersistentTrackerState committedState)
    {
        ApplyCommittedState(committedState);
        ReconcileWukongSaveSourceFromCommittedState();
    }
    public void SetOverlayUrls(string totalDeathsUrl) { TotalDeathsOverlayUrl = totalDeathsUrl; }
    internal void SetOverlayReady() => LocalOverlayStatus = LocalOverlayReadyMessage;
    public void SetOverlayUnavailable()
    {
        TotalDeathsOverlayUrl = "Overlay endpoint unavailable. Close the conflicting local application and restart SoulsTracker.";

        LocalOverlayStatus = LocalOverlayUnavailableMessage;
    }

    internal void SetGlobalHotkeyStatus(string status) => GlobalHotkeyStatus = string.IsNullOrWhiteSpace(status)
        ? throw new ArgumentException("A global hotkey status is required.", nameof(status))
        : status;
    internal void SetTextExportStatus(bool succeeded) => TextExportStatus = succeeded ? "Text exports ready." : "Text export is unavailable.";
    public Task SetDeathsExportPathAsync(string path, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(path, IsDeathsExportEnabled), cancellationToken);
    public Task SetDeathsExportEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(state?.TextExports.DeathsPath, enabled), cancellationToken);
    public Task ClearDeathsExportAsync(CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(null, IsDeathsExportEnabled), cancellationToken);
    public async Task SetEldenRingSaveFileAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled) return;
        if (!await Task.Run(() => EldenRingSaveDiscovery.IsParserValidSave(localPath), cancellationToken))
        {
            SetEldenRingSaveDiscoveryStatus("Selected save is unavailable or unsupported.");
            return;
        }
        await CommitEldenRingSaveAsync(localPath, discoveredChoice: null, LocalSaveSourceState.CustomSelection, cancellationToken);
    }

    public async Task SelectEldenRingSaveChoiceAsync(DiscoveredLocalSave? choice, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || choice is null || !EldenRingSaveChoices.Contains(choice)) return;
        await CommitEldenRingSaveAsync(choice.LocalPath, choice, LocalSaveSourceState.PersistedDiscovered, cancellationToken);
    }

    public void BeginEldenRingChange() { if (IsEldenRingSelected) IsEldenRingChangeMode = true; }
    public void CancelEldenRingChange() => IsEldenRingChangeMode = false;

    public async Task RescanEldenRingSavesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEldenRingSelected) return;
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
                SetEldenRingSaveDiscoveryStatus("Could not search for local saves. Try Rescan or Browse…");
            }
            return;
        }
        if (version != Interlocked.Read(ref eldenRingDiscoveryVersion) || !IsEldenRingSelected) return;

        EldenRingSaveChoices.Clear();
        foreach (DiscoveredLocalSave candidate in candidates) EldenRingSaveChoices.Add(candidate);
        string? configured = state?.EldenRingSave.LocalPath;
        SelectedEldenRingSaveChoice = candidates.SingleOrDefault(candidate => string.Equals(candidate.LocalPath, configured, StringComparison.OrdinalIgnoreCase));

        if (configured is not null && !File.Exists(configured))
        {
            SetEldenRingSaveDiscoveryStatus("Selected save is unavailable.");
            EldenRingSaveSourceState = LocalSaveSourceState.UnavailableSelection;
            ApplyEldenRingProfileChoices([]);
        }
        else if (SelectedEldenRingSaveChoice is not null)
        {
            SetEldenRingSaveDiscoveryStatus("Save found automatically");
            EldenRingSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
            await RefreshEldenRingProfileSlotsAsync(cancellationToken, clearStaleSelection: true);
        }
        else if (configured is null && candidates.Count == 1)
        {
            await CommitEldenRingSaveAsync(candidates[0].LocalPath, candidates[0], LocalSaveSourceState.AutomaticallySelected, cancellationToken);
        }
        else if (configured is null && candidates.Count > 1)
        {
            SetEldenRingSaveDiscoveryStatus("Choose a save to track");
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
            SetEldenRingSaveDiscoveryStatus(configured is null ? "No save found automatically." : "Selected save is unavailable.");
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
        await SaveEldenRingSaveAsync(new EldenRingSaveConfiguration(state?.EldenRingSave.LocalPath, slot.Index), cancellationToken);
    }
    public async Task SetBlackMythWukongSaveFileAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled) return;
        long selectionVersion = BeginWukongSelectionOperation();
        WukongSaveMetadataReadResult read = await readWukongSaveMetadataAsync(localPath, cancellationToken);
        if (!IsCurrentWukongSelectionOperation(selectionVersion)) return;
        if (!read.IsValid)
        {
            SetBlackMythWukongSaveDiscoveryStatus("Selected save is unavailable or unsupported.");
            return;
        }
        IReadOnlyList<DiscoveredLocalSave> candidates = await Task.Run(
            () => BlackMythWukongSaveDiscovery.DiscoverInSelectedFolder(localPath),
            cancellationToken);
        if (!IsCurrentWukongSelectionOperation(selectionVersion)) return;
        await SaveBlackMythWukongSaveAsync(new BlackMythWukongSaveConfiguration(localPath), cancellationToken);
        if (!IsCurrentWukongSelectionOperation(selectionVersion, localPath)) return;
        BlackMythWukongSaveChoices.Clear();
        foreach (DiscoveredLocalSave candidate in candidates) BlackMythWukongSaveChoices.Add(candidate);
        SelectedBlackMythWukongSaveChoice = candidates.SingleOrDefault(candidate => PathsEqual(candidate.LocalPath, localPath));
        SetBlackMythWukongSaveDiscoveryStatus(CustomSaveTrackingStatus(localPath));
        IsBlackMythWukongChangeMode = false;
        WukongSaveSourceState = LocalSaveSourceState.CustomSelection;
        OnPropertyChanged(nameof(SelectedBlackMythWukongSaveChoice));
        long metadataVersion = BeginWukongMetadataRead();
        TryApplyBlackMythWukongSaveMetadata(metadataVersion, localPath, read.Metadata);
    }

    public async Task SelectBlackMythWukongSaveChoiceAsync(DiscoveredLocalSave? choice, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || choice is null || !BlackMythWukongSaveChoices.Contains(choice)) return;
        long selectionVersion = BeginWukongSelectionOperation();
        await SaveBlackMythWukongSaveAsync(new BlackMythWukongSaveConfiguration(choice.LocalPath), cancellationToken);
        if (!IsCurrentWukongSelectionOperation(selectionVersion, choice.LocalPath)) return;
        SelectedBlackMythWukongSaveChoice = choice;
        IsBlackMythWukongChangeMode = false;
        WukongSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
        SetBlackMythWukongSaveDiscoveryStatus($"Tracking {choice.Label}");
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
        long selectionVersion = BeginWukongSelectionOperation();
        long version = Interlocked.Increment(ref blackMythWukongDiscoveryVersion);
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
            if (version == Interlocked.Read(ref blackMythWukongDiscoveryVersion) &&
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
            if (version == Interlocked.Read(ref blackMythWukongDiscoveryVersion) &&
                IsCurrentWukongSelectionOperation(selectionVersion))
            {
                WukongSaveSourceState = stableState;
                IsBlackMythWukongChangeMode = stableChangeMode;
                SetBlackMythWukongSaveDiscoveryStatus("Could not search for local saves. Try Rescan or Browse…");
                NotifyWukongSaveSourceProperties();
            }
            return;
        }
        if (version != Interlocked.Read(ref blackMythWukongDiscoveryVersion) ||
            !IsCurrentWukongSelectionOperation(selectionVersion)) return;
        BlackMythWukongSaveChoices.Clear();
        foreach (DiscoveredLocalSave candidate in candidates) BlackMythWukongSaveChoices.Add(candidate);

        string? configured = state?.BlackMythWukongSave.LocalPath;
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
            SetBlackMythWukongSaveDiscoveryStatus($"Tracking {selected.Label}");
            WukongSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
            metadataPath = selected.LocalPath;
        }
        else if (configured is null && candidates.Count == 1)
        {
            await SaveBlackMythWukongSaveAsync(new BlackMythWukongSaveConfiguration(candidates[0].LocalPath), cancellationToken);
            if (IsCurrentWukongSelectionOperation(selectionVersion, candidates[0].LocalPath))
            {
                SelectedBlackMythWukongSaveChoice = candidates[0];
                SetBlackMythWukongSaveDiscoveryStatus($"Tracking {candidates[0].Label}");
                WukongSaveSourceState = LocalSaveSourceState.AutomaticallySelected;
                metadataPath = candidates[0].LocalPath;
            }
        }
        else if (configured is null && candidates.Count > 1)
        {
            WukongSaveSourceState = LocalSaveSourceState.MultipleCandidates;
            SetBlackMythWukongSaveDiscoveryStatus("Choose the save slot you’re streaming.");
        }
        else if (configured is not null)
        {
            metadataVersion = BeginWukongMetadataRead();
            metadataReadPath = configured;
            WukongSaveMetadataReadResult read = await readWukongSaveMetadataAsync(configured, cancellationToken);
            if (version != Interlocked.Read(ref blackMythWukongDiscoveryVersion) ||
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
            SetBlackMythWukongSaveDiscoveryStatus("No save found automatically.");
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
        if (version != Interlocked.Read(ref blackMythWukongDiscoveryVersion) ||
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
    public void CancelBlackMythWukongChange() => IsBlackMythWukongChangeMode = false;

    public async Task SetLiesOfPSaveFileAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || !LiesOfPSaveConfiguration.IsCharacterSaveFileName(Path.GetFileName(localPath)) || !LiesOfPSaveDiscovery.IsRegularBoundedSave(localPath))
        {
            SetLiesOfPSaveDiscoveryStatus("Selected save is unavailable or unsupported.");
            return;
        }
        long selectionVersion = Interlocked.Increment(ref liesOfPSelectionVersion);
        IReadOnlyList<DiscoveredLocalSave> candidates = await Task.Run(
            () => LiesOfPSaveDiscovery.DiscoverInSelectedFolder(localPath),
            cancellationToken);
        if (selectionVersion != Interlocked.Read(ref liesOfPSelectionVersion)) return;
        await SaveLiesOfPSaveAsync(new LiesOfPSaveConfiguration(localPath), cancellationToken);
        if (selectionVersion != Interlocked.Read(ref liesOfPSelectionVersion)) return;
        if (!IsLiesOfPSelected || !PathsEqual(localPath, state?.LiesOfPSave.LocalPath)) return;
        LiesOfPSaveChoices.Clear();
        foreach (DiscoveredLocalSave candidate in candidates) LiesOfPSaveChoices.Add(candidate);
        SelectedLiesOfPSaveChoice = candidates.SingleOrDefault(candidate => PathsEqual(candidate.LocalPath, localPath));
        IsLiesOfPChangeMode = false;
        LiesOfPSaveSourceState = LocalSaveSourceState.CustomSelection;
        SetLiesOfPSaveDiscoveryStatus(CustomSaveTrackingStatus(localPath));
        OnPropertyChanged(nameof(SelectedLiesOfPSaveChoice));
    }

    public async Task SelectLiesOfPSaveChoiceAsync(DiscoveredLocalSave? choice, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || choice is null || !LiesOfPSaveChoices.Contains(choice)) return;
        long selectionVersion = Interlocked.Increment(ref liesOfPSelectionVersion);
        await SaveLiesOfPSaveAsync(new LiesOfPSaveConfiguration(choice.LocalPath), cancellationToken);
        if (selectionVersion != Interlocked.Read(ref liesOfPSelectionVersion)) return;
        if (!IsLiesOfPSelected || !PathsEqual(choice.LocalPath, state?.LiesOfPSave.LocalPath)) return;
        SelectedLiesOfPSaveChoice = choice;
        IsLiesOfPChangeMode = false;
        LiesOfPSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
        SetLiesOfPSaveDiscoveryStatus($"Tracking {choice.Label}");
        OnPropertyChanged(nameof(SelectedLiesOfPSaveChoice));
    }

    public async Task RescanLiesOfPSavesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLiesOfPSelected) return;
        long selectionVersion = Interlocked.Increment(ref liesOfPSelectionVersion);
        LiesOfPSaveSourceState = LocalSaveSourceState.Scanning;
        SetLiesOfPSaveDiscoveryStatus("Looking for local saves…");
        IReadOnlyList<DiscoveredLocalSave> candidates;
        string? configuredAtStart = state?.LiesOfPSave.LocalPath;
        try
        {
            SaveGameConfigurationWorkflow.SaveDiscoveryResult discoveryResult = await SaveGameConfigurationWorkflow.DiscoverWithConfiguredFallbackAsync(
                liesOfPSaveDiscovery,
                LiesOfPSaveDiscovery.DiscoverInSelectedFolder,
                configuredAtStart,
                cancellationToken);
            candidates = discoveryResult.Candidates;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch { SetLiesOfPSaveDiscoveryStatus("Could not search for local saves. Try Rescan or Browse…"); LiesOfPSaveSourceState = LocalSaveSourceState.NoCandidate; return; }
 if (selectionVersion != Interlocked.Read(ref liesOfPSelectionVersion)) return;
 if (!IsLiesOfPSelected) return;

        LiesOfPSaveChoices.Clear();
        foreach (DiscoveredLocalSave candidate in candidates) LiesOfPSaveChoices.Add(candidate);
        string? configured = state?.LiesOfPSave.LocalPath;
        SelectedLiesOfPSaveChoice = candidates.SingleOrDefault(candidate => PathsEqual(candidate.LocalPath, configured));
        if (configured is not null && !File.Exists(configured))
        {
            LiesOfPSaveSourceState = LocalSaveSourceState.UnavailableSelection;
            SetLiesOfPSaveDiscoveryStatus("Selected save is unavailable.");
        }
        else if (SelectedLiesOfPSaveChoice is { } selected)
        {
            LiesOfPSaveSourceState = LocalSaveSourceState.PersistedDiscovered;
            SetLiesOfPSaveDiscoveryStatus($"Tracking {selected.Label}");
        }
        else if (configured is null && candidates.Count == 1)
        {
            await SaveLiesOfPSaveAsync(new LiesOfPSaveConfiguration(candidates[0].LocalPath), cancellationToken);
            if (selectionVersion != Interlocked.Read(ref liesOfPSelectionVersion)) return;
            if (!IsLiesOfPSelected || !PathsEqual(candidates[0].LocalPath, state?.LiesOfPSave.LocalPath)) return;
            SelectedLiesOfPSaveChoice = candidates[0];
            LiesOfPSaveSourceState = LocalSaveSourceState.AutomaticallySelected;
            SetLiesOfPSaveDiscoveryStatus($"Tracking {candidates[0].Label}");
        }
        else if (configured is null && candidates.Count > 1)
        {
            LiesOfPSaveSourceState = LocalSaveSourceState.MultipleCandidates;
            SetLiesOfPSaveDiscoveryStatus("Choose the character you’re streaming.");
        }
        else if (configured is not null && LiesOfPSaveDiscovery.IsRegularBoundedSave(configured!))
        {
            LiesOfPSaveSourceState = LocalSaveSourceState.CustomSelection;
            SetLiesOfPSaveDiscoveryStatus(CustomSaveTrackingStatus(configured));
        }
        else
        {
            LiesOfPSaveSourceState = LocalSaveSourceState.NoCandidate;
            SetLiesOfPSaveDiscoveryStatus("No save found automatically.");
        }
        OnPropertyChanged(nameof(SelectedLiesOfPSaveChoice));
        NotifyLiesOfPSaveSourceProperties();
    }

    public void BeginLiesOfPChange() { if (IsLiesOfPSelected) IsLiesOfPChangeMode = true; }
    public void CancelLiesOfPChange() => IsLiesOfPChangeMode = false;


    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            TrackerStateLoadResult result = await coordinator.InitializeAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                ErrorMessage = LoadFailureMessage(result.FailureKind);
                LocalTrackerStateStatus = LocalTrackerStateUnavailableMessage;
                return;
            }

            ApplyCommittedState(result.State!);
            if (state?.SelectedGameId == GameId.EldenRing)
            {
                await RescanEldenRingSavesAsync(cancellationToken);
            }
            if (state?.SelectedGameId == GameId.BlackMythWukong)
            {
                await RescanBlackMythWukongSavesAsync(cancellationToken);
            }
            if (state?.SelectedGameId == GameId.LiesOfP)
            {
                await RescanLiesOfPSavesAsync(cancellationToken);
            }
            LocalTrackerStateStatus = LocalTrackerStateReadyMessage;
        }
        catch
        {
            ErrorMessage = "SoulsTracker could not load local tracker state. Close the app and try again.";
            LocalTrackerStateStatus = LocalTrackerStateUnavailableMessage;
        }
        finally
        {
            IsLoading = false;
            NotifyTrackerProperties();
        }
    }

    internal void SetGlobalHotkeySettings(GlobalHotkeySettings settings)
    {
        hotkeySettings = settings ?? throw new ArgumentNullException(nameof(settings));
        PendingIncrementHotkey = settings.Increment.DisplayText;
        PendingDecrementHotkey = settings.Decrement.DisplayText;
        pendingIncrementBinding = settings.Increment;
        pendingDecrementBinding = settings.Decrement;
        OnPropertyChanged(nameof(ActiveIncrementHotkey));
        OnPropertyChanged(nameof(ActiveDecrementHotkey));
    }

    internal void ConfigureGlobalHotkeys(GlobalHotkeySettings settings, Func<GlobalHotkeySettings, Task<GlobalHotkeyRegistrationResult>> apply)
    {
        applyHotkeysAsync = apply ?? throw new ArgumentNullException(nameof(apply));
        SetGlobalHotkeySettings(settings);
    }

    public void CapturePendingHotkey(bool increment, System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!GlobalHotkeyBinding.TryCreate(key, modifiers, out GlobalHotkeyBinding? binding, out string message))
        {
            SetGlobalHotkeyStatus(message);
            return;
        }
        GlobalHotkeyBinding captured = binding!;
        if (increment) { pendingIncrementBinding = captured; PendingIncrementHotkey = captured.DisplayText; } else { pendingDecrementBinding = captured; PendingDecrementHotkey = captured.DisplayText; }
        SetGlobalHotkeyStatus("Choose the other binding or apply the change.");
    }

    /// <summary>Starts an in-app recording session without changing an active global binding.</summary>
    public void BeginHotkeyRecording(bool increment)
    {
        if (!IsGlobalHotkeyConfigurationAvailable || !ControlsEnabled) return;

        recordingIncrementHotkey = increment;
        hotkeyBindingBeforeRecording = increment ? pendingIncrementBinding : pendingDecrementBinding;
        IsHotkeyRecording = true;
    }

    /// <summary>Captures the next valid binding for the active recording session.</summary>
    public void CaptureRecordedHotkey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!IsHotkeyRecording) return;
        CapturePendingHotkey(recordingIncrementHotkey, key, modifiers);
    }

    /// <summary>Cancels a recording session and restores the pending value it started with.</summary>
    public void CancelHotkeyRecording()
    {
        if (!IsHotkeyRecording) return;

        if (hotkeyBindingBeforeRecording is GlobalHotkeyBinding original)
        {
            if (recordingIncrementHotkey)
            {
                pendingIncrementBinding = original;
                PendingIncrementHotkey = original.DisplayText;
            }
            else
            {
                pendingDecrementBinding = original;
                PendingDecrementHotkey = original.DisplayText;
            }
        }

        hotkeyBindingBeforeRecording = null;
        IsHotkeyRecording = false;
    }

    /// <summary>Applies a recording session's captured binding and always releases the capture surface.</summary>
    public async Task SaveRecordedHotkeyAsync()
    {
        if (!IsHotkeyRecording) return;

        try
        {
            await ApplyGlobalHotkeysAsync();
        }
        finally
        {
            hotkeyBindingBeforeRecording = null;
            IsHotkeyRecording = false;
        }
    }

    public async Task ApplyGlobalHotkeysAsync()
    {
        if (applyHotkeysAsync is null) { SetGlobalHotkeyStatus("Global hotkeys are unavailable. The desktop controls remain available."); return; }
        if (!TryParsePending(out GlobalHotkeySettings? candidate, out string message)) { SetGlobalHotkeyStatus(message); return; }
        GlobalHotkeyRegistrationResult result = await applyHotkeysAsync(candidate!);
        SetGlobalHotkeyStatus(result.StatusMessage);
        if (result.IsRegistered) SetGlobalHotkeySettings(candidate!);
        else SetGlobalHotkeySettings(hotkeySettings);
    }

    private bool TryParsePending(out GlobalHotkeySettings? settings, out string message)
    {
        settings = null;
        if (pendingIncrementBinding == pendingDecrementBinding) { message = "Increment and decrement cannot use the same binding."; return false; }
        settings = new GlobalHotkeySettings(pendingIncrementBinding, pendingDecrementBinding); message = string.Empty; return true;
    }

    public async Task SelectGameAsync(GameChoice? choice, CancellationToken cancellationToken = default)
    {
        if (choice is null || !choice.IsSelectable || !ControlsEnabled)
        {
            return;
        }

        SoulsTracker.Domain.GameId? selectedGameBeforeSelection = state?.SelectedGameId;
        await SubmitAsync(new SelectGameCommand(choice.GameId), cancellationToken);
        if (state?.SelectedGameId != selectedGameBeforeSelection)
        {

        }

        if (state?.SelectedGameId == SoulsTracker.Domain.GameId.EldenRing)
        {
            await RescanEldenRingSavesAsync(cancellationToken);
        }
        if (state?.SelectedGameId == SoulsTracker.Domain.GameId.BlackMythWukong)
        {
            await RescanBlackMythWukongSavesAsync(cancellationToken);
        }
        if (state?.SelectedGameId == SoulsTracker.Domain.GameId.LiesOfP)
        {
            await RescanLiesOfPSavesAsync(cancellationToken);
        }
    }

    /// <summary>Starts a game selection, showing the local Elden Ring notice when needed.</summary>
    public bool RequestGameSelection(GameChoice? choice)
    {
        if (choice is null || !choice.IsSelectable || !ControlsEnabled)
        {
            return false;
        }

        if (choice.GameId == GameId.EldenRing && state?.EldenRingNoticeAcknowledged != true)
        {
            pendingEldenRingChoice = choice;
            IsEldenRingNoticeVisible = true;
            return false;
        }

        return true;
    }

    public async Task ConfirmEldenRingNoticeAsync(CancellationToken cancellationToken = default)
    {
        GameChoice? choice = pendingEldenRingChoice;
        if (choice is null || !IsEldenRingNoticeVisible)
        {
            return;
        }

        await SubmitAsync(new AcknowledgeEldenRingNoticeCommand(), cancellationToken);
        if (state?.EldenRingNoticeAcknowledged == true)
        {
            await SelectGameAsync(choice, cancellationToken);
        }

        pendingEldenRingChoice = null;
        IsEldenRingNoticeVisible = false;
    }

    public void CancelEldenRingNotice()
    {
        pendingEldenRingChoice = null;
        IsEldenRingNoticeVisible = false;
    }

    public Task IncrementManualDeathsAsync(CancellationToken cancellationToken = default) =>
        !IsManualGameSelected || !ControlsEnabled
            ? Task.CompletedTask
            : SubmitAsync(new IncrementManualBloodborneDeathsCommand(), cancellationToken);

    public Task DecrementManualDeathsAsync(CancellationToken cancellationToken = default) =>
        !CanDecrementManualDeaths
            ? Task.CompletedTask
            : SubmitAsync(new DecrementManualBloodborneDeathsCommand(), cancellationToken);

    /// <summary>Routes the shared global increment binding to the active supported counter.</summary>
    public Task IncrementGlobalTrackedDeathsAsync(CancellationToken cancellationToken = default) =>
        IsManualGameSelected
            ? IncrementManualDeathsAsync(cancellationToken)
            : CanAdjustEldenRingMissedDeaths
                ? IncrementEldenRingMissedDeathsAsync(cancellationToken)
                : Task.CompletedTask;

    /// <summary>Routes the shared global decrement binding to the active supported counter.</summary>
    public Task DecrementGlobalTrackedDeathsAsync(CancellationToken cancellationToken = default) =>
        IsManualGameSelected
            ? DecrementManualDeathsAsync(cancellationToken)
            : CanDecrementEldenRingMissedDeaths
                ? DecrementEldenRingMissedDeathsAsync(cancellationToken)
                : Task.CompletedTask;

    public Task IncrementEldenRingMissedDeathsAsync(CancellationToken cancellationToken = default) =>
        !CanAdjustEldenRingMissedDeaths
            ? Task.CompletedTask
            : SubmitAsync(new AdjustEldenRingMissedDeathsCommand(Increment: true), cancellationToken);

    public Task DecrementEldenRingMissedDeathsAsync(CancellationToken cancellationToken = default) =>
        !CanDecrementEldenRingMissedDeaths
            ? Task.CompletedTask
            : SubmitAsync(new AdjustEldenRingMissedDeathsCommand(Increment: false), cancellationToken);


    public Task SetTotalDeathsOverlayEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled || isEnabled == IsTotalDeathsOverlayEnabled
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(isEnabled, ShowTotalDeathsGameName, cancellationToken);

    public Task SetShowTotalDeathsGameNameAsync(bool showGameName, CancellationToken cancellationToken = default) =>
        !CanConfigureTotalDeathsGameName || showGameName == ShowTotalDeathsGameName
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(IsTotalDeathsOverlayEnabled, showGameName, cancellationToken);


    public Task ResetOverlayAppearanceAsync(bool totalDeaths, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled ? Task.CompletedTask : SubmitAsync(new ResetOverlayAppearanceCommand(totalDeaths), cancellationToken);

    public async Task ApplyOverlayAppearanceAsync(bool totalDeaths, CancellationToken cancellationToken = default)
    {
        if (!PresentationControlsEnabled || state is null) return;
        try
        {
            OverlayAppearance appearance = totalDeaths
                ? TotalDeathsAppearanceDraft.ToDomain(OverlayTextAlignment.Left)
                : TotalDeathsAppearanceDraft.ToDomain(OverlayTextAlignment.Left);
            // The selector is the sole source of truth. Legacy booleans are projected only for compatibility.

            // Appearance drafts are isolated from Main-tab operational errors.
            // A failed Apply must leave the last applied style/URL/preview intact.
            await SubmitAsync(new UpdateOverlayAppearanceCommand(totalDeaths, appearance, false, false, OverlayTitleIconMode.Off), cancellationToken);
            SetAppearanceFeedback(totalDeaths, ErrorMessage is null
                ? "Total Deaths appearance applied."
                : "Total Deaths appearance could not be applied.");
        }
        catch (ArgumentException exception)
        {
            OverlayAppearanceDraft draft = TotalDeathsAppearanceDraft;
            string detail = draft.ValidationMessage ?? exception.Message.Split(Environment.NewLine)[0];
            SetAppearanceFeedback(totalDeaths, detail);
            return;
        }
    }

    private Task SubmitOverlayPresentationAsync(
        bool totalDeathsEnabled,
        bool showGameName,
        CancellationToken cancellationToken) =>
        SubmitAsync(new UpdateOverlayPresentationCommand(totalDeathsEnabled, showGameName), cancellationToken);

    private async Task SubmitAsync(ITrackerCommand command, CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            TrackerCommandExecutionResult result = await coordinator.SubmitAsync(command, cancellationToken);
            if (result.CommittedState is not null)
            {
                ApplyCommittedState(result.CommittedState);
            }

            if (result.Status is TrackerCommandExecutionStatus.SaveFailed or TrackerCommandExecutionStatus.NotInitialized)
            {
                ErrorMessage = "SoulsTracker could not save the requested tracker change. The displayed state was not changed.";
            }
            else if (result.Status == TrackerCommandExecutionStatus.DeliveryFailed)
            {
                ErrorMessage = "The tracker change was saved, but a local update could not be delivered.";
            }
        }
        catch
        {
            ErrorMessage = "SoulsTracker could not apply the requested tracker change. Try again.";
        }
        finally
        {
            IsBusy = false;
            NotifyTrackerProperties();
        }
    }

    private void ApplyCommittedState(
        PersistentTrackerState committedState,
        bool preserveWukongMetadataOperation = false)
    {
        if (!preserveWukongMetadataOperation)
        {
            InvalidateWukongOperations();
        }
        string? previousWukongSavePath = state?.BlackMythWukongSave.LocalPath;
        EldenRingSaveConfiguration? previousEldenRingSave = state?.EldenRingSave;
        LiesOfPSaveConfiguration? previousLiesOfPSave = state?.LiesOfPSave;
        state = committedState ?? throw new ArgumentNullException(nameof(committedState));
        if (state.SelectedGameId != GameId.BlackMythWukong ||
            !string.Equals(previousWukongSavePath, state.BlackMythWukongSave.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            SetBlackMythWukongSaveMetadata(null);
        }
        bool blackMythWukongSaveIsUnconfigured = state.SelectedGameId == GameId.BlackMythWukong && state.BlackMythWukongSave.LocalPath is null;
        bool liesOfPSaveIsUnconfigured = state.SelectedGameId == GameId.LiesOfP && state.LiesOfPSave.LocalPath is null;
        if (runtimeReaderGameId != state.SelectedGameId || blackMythWukongSaveIsUnconfigured || liesOfPSaveIsUnconfigured)
        {
            runtimeObservation = null;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeReaderStatus = blackMythWukongSaveIsUnconfigured || liesOfPSaveIsUnconfigured
                ? RuntimeGameReaderStatus.WaitingForSaveFile
                : RuntimeGameReaderStatus.Unavailable;
            runtimeReaderGameId = blackMythWukongSaveIsUnconfigured ? GameId.BlackMythWukong : liesOfPSaveIsUnconfigured ? GameId.LiesOfP : null;
        }
        else if ((state.SelectedGameId == GameId.EldenRing && previousEldenRingSave != state.EldenRingSave) ||
                 (state.SelectedGameId == GameId.BlackMythWukong && !string.Equals(previousWukongSavePath, state.BlackMythWukongSave.LocalPath, StringComparison.OrdinalIgnoreCase)) ||
                 (state.SelectedGameId == GameId.LiesOfP && previousLiesOfPSave != state.LiesOfPSave))
        {
            runtimeObservation = null;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeReaderStatus = RuntimeGameReaderStatus.Unavailable;
            runtimeReaderGameId = state.SelectedGameId;
        }
        IsTotalDeathsOverlayEnabled = state.OverlayConfiguration.TotalDeaths.IsEnabled;
        ShowTotalDeathsGameName = state.OverlayConfiguration.TotalDeaths.ShowGameName;
        TotalDeathsAppearanceDraft.Load(state.OverlayConfiguration.TotalDeaths.Appearance);
        TotalDeathsAppearanceDraft.FontFamily = ResolveLocalFont(TotalDeathsAppearanceDraft.FontFamily);
        DraftTitleIconModeChoice = TitleIconModes.Single(choice => choice.Value == state.OverlayConfiguration.TotalDeaths.TitleIconMode);

        GameId? selectedId = state.SelectedGameId;
        SelectedGame = GameChoices.Single(choice => choice.GameId == selectedId);
        SelectedEldenRingProfileSlot = EldenRingProfileSlots.SingleOrDefault(slot => slot.Index == state.EldenRingSave.SlotIndex);

        OnPropertyChanged(nameof(SelectedGame));
        OnPropertyChanged(nameof(GameSelectionAutomationName));
        OnPropertyChanged(nameof(SelectedEldenRingProfileSlot));


        UpdateTotalDeathsText();
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
        NotifyTrackerProperties();
        NotifyEldenRingSaveSourceProperties();
        NotifyWukongSaveSourceProperties();
        NotifyLiesOfPSaveSourceProperties();
        OnPropertyChanged(nameof(TotalDeathsSceneUrl));
        OnPropertyChanged(nameof(TotalDeathsSceneUrlDisplay));

    }

    private void UpdateTotalDeathsText()
    {
        if (state is null)
        {
            return;
        }

        GameId selectedId = state.SelectedGameId;
        if (runtimeReaderHasNoRecordedDeaths)
        {
            TotalDeathsText = NoDeathsRecordedMessage;
            return;
        }

        if (runtimeReaderStatus == RuntimeGameReaderStatus.SelectedSaveUnreadable)
        {
            TotalDeathsText = SelectedSaveUnreadableMessage;
            return;
        }

        long? combined = TotalDeathsDisplayProjection.Combine(state, runtimeObservation?.GameId == selectedId ? runtimeObservation : null);
        TotalDeathsText = combined.HasValue
            ? combined.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : IsManualGame(selectedId)
                ? ManualDeaths.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : runtimeReaderStatus == RuntimeGameReaderStatus.WaitingForActiveCharacter
                    ? GameTotalDeathsWaitingForActiveCharacterMessage
                    : runtimeReaderStatus == RuntimeGameReaderStatus.WaitingForSaveFile
                        ? WaitingForSaveFileMessage(selectedId) + " to begin tracking."
                        : GameTotalDeathsUnavailableMessage;
    }


    private static string LoadFailureMessage(TrackerStateLoadFailureKind kind) => kind switch
    {
        TrackerStateLoadFailureKind.UnsupportedVersion => "Stored tracker state is from an unsupported version. Update SoulsTracker or restore a compatible local backup.",
        TrackerStateLoadFailureKind.Integrity => "Local tracker state failed validation. It was not replaced; close the app and restore a known-good local backup.",
        _ => "Local tracker state could not be read. It was not replaced; close the app and try again.",
    };

    private string ResolveLocalFont(string fontFamily) => LocalFontFamilies.Contains(fontFamily, StringComparer.OrdinalIgnoreCase) ? fontFamily : "Segoe UI";

    private string? AppendStyleQuery(string? url, bool totalDeaths)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("Overlay endpoint unavailable", StringComparison.Ordinal)) return url;
        if (state is null) return url;
        OverlayAppearance appearance = state.OverlayConfiguration.TotalDeaths.Appearance;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["styleVersion"] = "1",
            ["title"] = appearance.Title,
            ["font"] = appearance.FontFamily,
            ["size"] = appearance.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["textColor"] = appearance.TextColor,
            ["backgroundColor"] = appearance.BackgroundColor,
            ["backgroundOpacity"] = appearance.BackgroundOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["textOpacity"] = appearance.TextOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["iconColor"] = appearance.IconColor,
            ["outline"] = appearance.OutlineEnabled ? "true" : "false",
            ["outlineColor"] = appearance.OutlineColor,
            ["outlineWidth"] = appearance.OutlineWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["shadow"] = appearance.ShadowEnabled ? "true" : "false",
            ["shadowColor"] = appearance.ShadowColor,
            ["shadowX"] = appearance.ShadowOffsetX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["shadowY"] = appearance.ShadowOffsetY.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["shadowBlur"] = appearance.ShadowBlur.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        values["inline"] = "true";
        values["titleIcon"] = state.OverlayConfiguration.TotalDeaths.TitleIconMode.ToString();
        string separator = url.Contains('?') ? "&" : "?";
        return url + separator + string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    internal static string? ShortenUrlForDisplay(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return url;
        string[] visible = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(static pair => !pair.StartsWith("token=", StringComparison.OrdinalIgnoreCase)).ToArray();
        return visible.Length == 0 ? "…" : "…&" + string.Join("&", visible);
    }

    private void SetAppearanceFeedback(bool totalDeaths, string message)
    {
        if (totalDeaths) TotalDeathsAppearanceStatus = message;
        _ = ClearAppearanceFeedbackAfterDelayAsync(totalDeaths, message);
    }

    private async Task ClearAppearanceFeedbackAfterDelayAsync(bool totalDeaths, string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (totalDeaths && TotalDeathsAppearanceStatus == message) TotalDeathsAppearanceStatus = null;

    }

    private void NotifyTrackerProperties()
    {
        OnPropertyChanged(nameof(ControlsEnabled));
        OnPropertyChanged(nameof(ManualDeaths));
        OnPropertyChanged(nameof(IsBloodborneSelected));
        OnPropertyChanged(nameof(IsEldenRingSelected));
        OnPropertyChanged(nameof(IsBlackMythWukongSelected));
        OnPropertyChanged(nameof(IsLiesOfPSelected));
        OnPropertyChanged(nameof(CanSelectEldenRingProfile));
        OnPropertyChanged(nameof(EldenRingCharacterStatus));
        OnPropertyChanged(nameof(IsManualGameSelected));
        OnPropertyChanged(nameof(IsGlobalHotkeyConfigurationAvailable));
        OnPropertyChanged(nameof(GlobalHotkeyUsageDescription));

        OnPropertyChanged(nameof(CanDecrementManualDeaths));
        OnPropertyChanged(nameof(IsEldenRingMissedDeathAdjustmentAvailable));
        OnPropertyChanged(nameof(CanAdjustEldenRingMissedDeaths));
        OnPropertyChanged(nameof(CanDecrementEldenRingMissedDeaths));
        OnPropertyChanged(nameof(EldenRingMissedDeaths));
        OnPropertyChanged(nameof(EldenRingSavedDeathsText));
        OnPropertyChanged(nameof(PresentationControlsEnabled));
        OnPropertyChanged(nameof(CanConfigureTotalDeathsGameName));
        OnPropertyChanged(nameof(DeathsExportFileName)); OnPropertyChanged(nameof(IsDeathsExportEnabled));
        NotifyTextExportControlAvailability();
    }

    private void NotifyTextExportControlAvailability()
    {
        OnPropertyChanged(nameof(CanChooseDeathsExport));
        OnPropertyChanged(nameof(CanClearDeathsExport));

    }

    private static string WaitingForSaveFileMessage(GameId gameId) => gameId == GameId.BlackMythWukong
        ? BlackMythWukongWaitingForSaveFileMessage
        : gameId == GameId.LiesOfP
            ? LiesOfPWaitingForSaveFileMessage
            : GameWaitingForSaveFileMessage;

    private static bool IsManualGame(GameId gameId) => gameId == GameId.DemonsSouls;


    private async Task SaveExportsAsync(TextExportConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!ControlsEnabled) return;
        try { ApplyCommittedState(await coordinator.SetTextExportConfigurationAsync(configuration, cancellationToken)); }
        catch { ErrorMessage = "Text export settings could not be saved."; }
    }

    private async Task SaveEldenRingSaveAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try { ApplyCommittedState(await saveGameConfigurationWorkflow.SaveEldenRingAsync(configuration, cancellationToken)); }
        catch { ErrorMessage = "The Elden Ring save selection could not be saved."; }
        finally { IsBusy = false; NotifyTrackerProperties(); }
    }

    private async Task CommitEldenRingSaveAsync(string localPath, DiscoveredLocalSave? discoveredChoice, LocalSaveSourceState sourceState, CancellationToken cancellationToken)
    {
        IReadOnlyList<EldenRingProfileSlotChoice> choices = await ReadEldenRingProfileChoicesAsync(localPath, cancellationToken);
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
        IsEldenRingChangeMode = false;
        EldenRingSaveSourceState = sourceState;
        SetEldenRingSaveDiscoveryStatus(
            sourceState == LocalSaveSourceState.AutomaticallySelected
                ? "Save found automatically"
                : discoveredChoice is null
                    ? CustomSaveTrackingStatus(localPath)
                    : $"Tracking {discoveredChoice.Label}");
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
        return Interlocked.Increment(ref wukongSelectionOperationVersion);
    }

    private long BeginWukongMetadataRead() =>
        Interlocked.Increment(ref wukongMetadataOperationVersion);

    private void InvalidateWukongMetadataOperations() =>
        Interlocked.Increment(ref wukongMetadataOperationVersion);

    private void InvalidateWukongOperations()
    {
        Interlocked.Increment(ref wukongSelectionOperationVersion);
        InvalidateWukongMetadataOperations();
    }

    private bool IsCurrentWukongSelectionOperation(long version, string? expectedPath = null) =>
        version == Interlocked.Read(ref wukongSelectionOperationVersion) &&
        IsBlackMythWukongSelected &&
        (expectedPath is null || PathsEqual(expectedPath, state?.BlackMythWukongSave.LocalPath));

    private void TryApplyBlackMythWukongSaveMetadata(
        long version,
        string expectedPath,
        BlackMythWukongSaveMetadata? metadata)
    {
        if (version != Interlocked.Read(ref wukongMetadataOperationVersion) ||
            !IsBlackMythWukongSelected ||
            !PathsEqual(expectedPath, state?.BlackMythWukongSave.LocalPath))
        {
            return;
        }

        SetBlackMythWukongSaveMetadata(metadata);
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string CustomSaveTrackingStatus(string localPath)
    {
        string fileName = Path.GetFileName(localPath);
        return string.IsNullOrEmpty(fileName)
            ? "Tracking custom save."
            : $"Tracking custom save: {fileName}";
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
            SetBlackMythWukongSaveDiscoveryStatus($"Tracking {discovered.Label}");
        }
        else if (localPath is null)
        {
            WukongSaveSourceState = BlackMythWukongSaveChoices.Count > 1
                ? LocalSaveSourceState.MultipleCandidates
                : LocalSaveSourceState.NoCandidate;
            SetBlackMythWukongSaveDiscoveryStatus(
                BlackMythWukongSaveChoices.Count > 1
                    ? "Choose the save slot youâ€™re streaming."
                    : "No save found automatically.");
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
        IReadOnlyList<EldenRingProfileSlotChoice> choices = localPath is null
            ? []
            : await SaveGameConfigurationWorkflow.ReadEldenRingProfileSlotsAsync(eldenRingSaveProfileReader, localPath, cancellationToken);
        ApplyEldenRingProfileChoices(choices);
        if (clearStaleSelection
            && localPath is not null
            && state!.EldenRingSave.SlotIndex != EldenRingSaveConfiguration.NoSlotIndex
            && SelectedEldenRingProfileSlot is null)
        {
            await SaveEldenRingSaveAsync(new EldenRingSaveConfiguration(localPath, EldenRingSaveConfiguration.NoSlotIndex), cancellationToken);
            ApplyEldenRingProfileChoices(choices);
        }
    }

    private static bool IsAvailableEldenRingSaveFile(string? localPath) =>
        localPath is not null
        && string.Equals(Path.GetFileName(localPath), "ER0000.sl2", StringComparison.OrdinalIgnoreCase)
        && File.Exists(localPath);

    private void NotifyEldenRingSaveSourceProperties()
    {
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
        OnPropertyChanged(nameof(EldenRingSaveDiscoveryStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void NotifyWukongSaveSourceProperties()
    {
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
        OnPropertyChanged(nameof(BlackMythWukongSaveDiscoveryStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void NotifyLiesOfPSaveSourceProperties()
    {
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
        OnPropertyChanged(nameof(LiesOfPSaveDiscoveryStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }


    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void LegacyImport_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LegacyImportViewModel.OfferVisible) or nameof(LegacyImportViewModel.ReviewVisible))
        {
            OnPropertyChanged(nameof(HasActiveLegacyImport));
        }
    }
}

public enum LocalSaveSourceState
{
    Scanning,
    NoCandidate,
    AutomaticallySelected,
    MultipleCandidates,
    PersistedDiscovered,
    CustomSelection,
    UnavailableSelection,
}

public sealed class GameChoice
{
    private readonly GameDefinition? definition;

    public GameChoice(GameDefinition definition) => this.definition = definition ?? throw new ArgumentNullException(nameof(definition));

    public GameId GameId => definition!.Id;
    public string DisplayName => GameId == SoulsTracker.Domain.GameId.DemonsSouls
            ? "Demon Souls [Manual]"
            : definition!.DisplayName;
    public bool IsSelectable => definition!.IsSelectable;
    public string AvailabilityLabel => IsSelectable ? string.Empty : "SOON";
}
