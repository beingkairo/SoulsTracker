using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    internal const string EldenRingChooseCharacterMessage = "Choose a character to continue.";
    internal const string GameSyncedMessage = "Synced";
    internal const string GameTotalDeathsUnavailableMessage = "Unable to read total deaths.";
    internal const string GameTotalDeathsWaitingForActiveCharacterMessage = "Unavailable — waiting for active character.";

    internal const string DeathSoundVolumeValidationMessage = "Death sound volume must be between 0 and 100.";

    private readonly SerializedTrackerCoordinator coordinator;
    private readonly IEldenRingSaveProfileReader eldenRingSaveProfileReader;
    private readonly ILocalSaveDiscovery eldenRingSaveDiscovery;
    private readonly ILocalSaveDiscovery blackMythWukongSaveDiscovery;
    private PersistentTrackerState? state;
    private RuntimeGameObservation? runtimeObservation;
    private RuntimeGameReaderStatus runtimeReaderStatus;
    private GameId? runtimeReaderGameId;
    private bool isLoading = true;
    private bool isBusy;
    private string? errorMessage;
    private string totalDeathsText = "Load tracker state before using controls.";
    private string? totalDeathsOverlayUrl;
    private string? bossListOverlayUrl;
    private string? globalHotkeyStatus;
    private string? localTrackerStateStatus;
    private string? localOverlayStatus;
    private bool isTotalDeathsOverlayEnabled;
    private bool showTotalDeathsGameName;
    private bool isBossListOverlayEnabled;
    private BossListVisibilityMode bossListVisibilityMode;
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
    private IDeathSoundPlayer? deathSoundPlayer;
    private string? deathSoundStatus;
    private string deathSoundVolumeText = "100";
    private readonly object deathSoundVolumeSaveSync = new();
    private Task deathSoundVolumeSaveTail = Task.CompletedTask;
    private long deathSoundVolumeEditVersion;
    private string? textExportStatus;
    private OverlayTitleIconModeChoice draftTitleIconModeChoice = OverlayTitleIconModeChoice.All[0];
    private BossListVisibilityMode draftBossListMode;
    private string draftDefeatedColor = "#8C8C96";
    private DefeatedBossTreatment draftDefeatedTreatment = DefeatedBossTreatment.Nothing;
    private string? totalDeathsAppearanceStatus;
    private string? bossListAppearanceStatus;
    private BossMarkerChoice draftBossMarker = BossMarkerChoice.All[1];
    private BossMarkerChoice lastNonCenterBossMarker = BossMarkerChoice.All[1];
    private CenterMarkerAlignment draftCenterMarkerAlignment = CenterMarkerAlignment.Left;
    private bool legacyDraftShowGameName;
    private bool legacyDraftCompactTitle = true;
    private IReadOnlyList<BossListScopeChoice> bossListScopes = BossListScopeChoice.For(GameCatalog.GetRequired(GameId.DemonsSouls));
    private BossListScopeChoice selectedBossListScope = BossListScopeChoice.For(GameCatalog.GetRequired(GameId.DemonsSouls))[0];
    private string bossSearchQuery = string.Empty;
    private readonly object bossesSynchronization = new();
    private readonly object filteredBossesSynchronization = new();
    private readonly object eldenRingProfileSlotsSynchronization = new();
    private readonly object eldenRingSaveChoicesSynchronization = new();
    private readonly object blackMythWukongSaveChoicesSynchronization = new();
    private long blackMythWukongDiscoveryVersion;
    private LocalSaveSourceState wukongSaveSourceState;
    private bool isBlackMythWukongChangeMode;
    private string? blackMythWukongSaveDiscoveryStatus;
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
        TimeProvider? timeProvider = null)
        : this(
            coordinator,
            eldenRingSaveProfileReader,
            blackMythWukongSaveDiscovery,
            eldenRingSaveDiscovery,
            timeProvider,
            ReadBlackMythWukongSaveMetadataCoreAsync)
    {
    }

    internal DesktopTrackerViewModel(
        SerializedTrackerCoordinator coordinator,
        IEldenRingSaveProfileReader? eldenRingSaveProfileReader,
        ILocalSaveDiscovery? blackMythWukongSaveDiscovery,
        ILocalSaveDiscovery? eldenRingSaveDiscovery,
        TimeProvider? timeProvider,
        Func<string, CancellationToken, Task<WukongSaveMetadataReadResult>> readWukongSaveMetadataAsync)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.eldenRingSaveProfileReader = eldenRingSaveProfileReader ?? new EldenRingSaveProfileReader();
        this.eldenRingSaveDiscovery = eldenRingSaveDiscovery ?? new EldenRingSaveDiscovery();
        this.blackMythWukongSaveDiscovery = blackMythWukongSaveDiscovery ?? new BlackMythWukongSaveDiscovery();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.readWukongSaveMetadataAsync = readWukongSaveMetadataAsync ?? throw new ArgumentNullException(nameof(readWukongSaveMetadataAsync));
        GameChoices = new ObservableCollection<GameChoice>(GameCatalog.All.Select(static game => new GameChoice(game)));
        Bosses.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsBossListEmpty));
        EldenRingProfileSlots = [];
        EldenRingSaveChoices = [];
        BlackMythWukongSaveChoices = [];
        BindingOperations.EnableCollectionSynchronization(Bosses, bossesSynchronization);
        BindingOperations.EnableCollectionSynchronization(FilteredBosses, filteredBossesSynchronization);
        BindingOperations.EnableCollectionSynchronization(EldenRingProfileSlots, eldenRingProfileSlotsSynchronization);
        BindingOperations.EnableCollectionSynchronization(EldenRingSaveChoices, eldenRingSaveChoicesSynchronization);
        BindingOperations.EnableCollectionSynchronization(BlackMythWukongSaveChoices, blackMythWukongSaveChoicesSynchronization);
        BossListAppearanceDraft.PropertyChanged += BossListAppearanceDraft_PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void BossListAppearanceDraft_PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(OverlayAppearanceDraft.Alignment))
        {
            if (IsCenterBossAlignment)
            {
                // Centered lists deliberately do not support markers. Clear the
                // draft immediately so Apply cannot persist an invisible marker.
                if (DraftBossMarker.Value != "None")
                {
                    lastNonCenterBossMarker = DraftBossMarker;
                }
                DraftBossMarker = BossMarkers.Single(choice => choice.Value == "None");
            }
            else if (DraftBossMarker.Value == "None" && lastNonCenterBossMarker.Value != "None")
            {
                // Returning to a side-aligned layout restores the user's last
                // side-marker draft without ever allowing it in centered output.
                DraftBossMarker = lastNonCenterBossMarker;
            }

            OnPropertyChanged(nameof(IsCenterBossAlignment));
            OnPropertyChanged(nameof(AreBossMarkerControlsVisible));
        }
    }

    public ObservableCollection<GameChoice> GameChoices { get; }

    public ObservableCollection<BossChoice> Bosses { get; } = [];

    /// <summary>Gets the current desktop-only boss checklist projection after search filtering.</summary>
    public ObservableCollection<BossChoice> FilteredBosses { get; } = [];

    /// <summary>Gets or sets the transient desktop boss-search query.</summary>
    public string BossSearchQuery
    {
        get => bossSearchQuery;
        set
        {
            if (!SetField(ref bossSearchQuery, value ?? string.Empty)) return;
            RefreshFilteredBosses();
            OnPropertyChanged(nameof(ShowBossSearchPlaceholder));
        }
    }

    /// <summary>Gets whether the current non-empty search has no matching bosses in the displayed scope.</summary>
    public bool IsBossSearchNoResults =>
        !string.IsNullOrWhiteSpace(BossSearchQuery) && Bosses.Count > 0 && FilteredBosses.Count == 0;

    /// <summary>Gets whether the selected game's current boss filter has no entries to display.</summary>
    public bool IsBossListEmpty => state is not null && Bosses.Count == 0;

    /// <summary>Shows the search hint until a meaningful filter has been entered.</summary>
    public bool ShowBossSearchPlaceholder => string.IsNullOrWhiteSpace(BossSearchQuery);

    /// <summary>Describes the boss checklist for the current selection.</summary>
    public string BossDescription => state is null
        ? "Track boss progress."
        : $"Track boss progress for {GameCatalog.GetRequired(state.SelectedGameId).DisplayName}.";

    public ObservableCollection<EldenRingProfileSlotChoice> EldenRingProfileSlots { get; }
    public IReadOnlyList<BossListScopeChoice> BossListScopes { get => bossListScopes; private set => SetField(ref bossListScopes, value); }
    public BossListScopeChoice SelectedBossListScope { get => selectedBossListScope; private set => SetField(ref selectedBossListScope, value); }
    public IReadOnlyList<BossListVisibilityMode> BossListVisibilityModes { get; } = Enum.GetValues<BossListVisibilityMode>();
    public IReadOnlyList<OverlayTextAlignment> OverlayAlignments { get; } = Enum.GetValues<OverlayTextAlignment>();
    public IReadOnlyList<DefeatedBossTreatment> DefeatedBossTreatments { get; } = [DefeatedBossTreatment.Nothing, DefeatedBossTreatment.Dimmed, DefeatedBossTreatment.Strikethrough, DefeatedBossTreatment.Both];
    public IReadOnlyList<string> LocalFontFamilies { get; } = GetLocalFontFamilies();

    private static string[] GetLocalFontFamilies()
    {
        try { return Fonts.SystemFontFamilies.Select(static font => font.Source).Where(static name => !string.IsNullOrWhiteSpace(name)).Append("Segoe UI").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return ["Segoe UI"]; }
    }
    public OverlayAppearanceDraft TotalDeathsAppearanceDraft { get; } = new();
    public OverlayAppearanceDraft BossListAppearanceDraft { get; } = new();
    // Legacy test/binding compatibility; V1 no longer exposes editable controls for these choices.
    public bool DraftShowGameName { get => legacyDraftShowGameName; set => SetField(ref legacyDraftShowGameName, value); }
    public bool DraftCompactTitle { get => legacyDraftCompactTitle; set => SetField(ref legacyDraftCompactTitle, value); }
    public IReadOnlyList<OverlayTitleIconModeChoice> TitleIconModes { get; } = OverlayTitleIconModeChoice.All;
    public OverlayTitleIconModeChoice DraftTitleIconModeChoice { get => draftTitleIconModeChoice; set { if (SetField(ref draftTitleIconModeChoice, value)) OnPropertyChanged(nameof(IsTitleIconSelected)); } }
    public BossListVisibilityMode DraftBossListMode { get => draftBossListMode; set => SetField(ref draftBossListMode, value); }
    public string DraftDefeatedColor { get => draftDefeatedColor; set => SetField(ref draftDefeatedColor, value); }
    public DefeatedBossTreatment DraftDefeatedTreatment { get => draftDefeatedTreatment; set => SetField(ref draftDefeatedTreatment, value); }
    public bool DraftShowCheckmark { get; set; } = true;
    public bool DraftShowDefeatedSkull { get; set; }
    public IReadOnlyList<BossMarkerChoice> BossMarkers { get; } = BossMarkerChoice.All;
    public BossMarkerChoice DraftBossMarker
    {
        get => draftBossMarker;
        set
        {
            BossMarkerChoice normalized = IsCenterBossAlignment && value.Value != "None"
                ? BossMarkers.Single(choice => choice.Value == "None")
                : value;
            if (SetField(ref draftBossMarker, normalized))
            {
                if (!IsCenterBossAlignment && normalized.Value != "None") lastNonCenterBossMarker = normalized;
                OnPropertyChanged(nameof(IsBossMarkerSelected));
                OnPropertyChanged(nameof(ShowBossMarkerColor));
            }
        }
    }
    public CenterMarkerAlignment DraftCenterMarkerAlignment { get => draftCenterMarkerAlignment; set => SetField(ref draftCenterMarkerAlignment, value); }
    public bool IsCenterBossAlignment => BossListAppearanceDraft.Alignment == OverlayTextAlignment.Center;
    public bool IsBossMarkerSelected => DraftBossMarker.Value != "None";
    public bool AreBossMarkerControlsVisible => !IsCenterBossAlignment;
    public bool ShowBossMarkerColor => AreBossMarkerControlsVisible && IsBossMarkerSelected;
    public bool IsTitleIconSelected => DraftTitleIconModeChoice.Value != OverlayTitleIconMode.Off;
    public string DraftCheckmarkAccent { get; set; } = "#A78BFA";
    public string DraftMaximumVisibleCount { get; set; } = "25";

    public GameChoice? SelectedGame { get; private set; }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (SetField(ref isLoading, value))
            {
                OnPropertyChanged(nameof(ControlsEnabled));
                OnPropertyChanged(nameof(CanSelectEldenRingProfile));
                NotifyDeathSoundControlAvailability();
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
                OnPropertyChanged(nameof(PresentationControlsEnabled));
                OnPropertyChanged(nameof(CanConfigureTotalDeathsGameName));
                OnPropertyChanged(nameof(CanSelectEldenRingProfile));
                NotifyDeathSoundControlAvailability();
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
            if (selectedGameId is null || selectedGameId == GameId.Bloodborne || selectedGameId == GameId.DemonsSouls)
            {
                return null;
            }

            if (selectedGameId == GameId.BlackMythWukong && runtimeReaderStatus != RuntimeGameReaderStatus.Synced)
            {
                return BlackMythWukongSaveDiscoveryStatus ?? WaitingForSaveFileMessage(selectedGameId);
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
        && (IsBlackMythWukongChangeMode || WukongSaveSourceState == LocalSaveSourceState.MultipleCandidates);
    public bool IsWukongBrowseVisible => WukongSaveSourceState is LocalSaveSourceState.Scanning or LocalSaveSourceState.NoCandidate or LocalSaveSourceState.MultipleCandidates || IsBlackMythWukongChangeMode;
    public bool IsWukongChangeVisible => !IsBlackMythWukongChangeMode && WukongSaveSourceState is LocalSaveSourceState.AutomaticallySelected or LocalSaveSourceState.PersistedDiscovered or LocalSaveSourceState.CustomSelection or LocalSaveSourceState.UnavailableSelection;
    public bool IsWukongCancelVisible => IsBlackMythWukongChangeMode;
    public bool IsWukongSaveSelectorEnabled => ControlsEnabled && WukongSaveSourceState != LocalSaveSourceState.Scanning && IsWukongSaveSelectorVisible && BlackMythWukongSaveChoices.Count > 0;
    public string? BlackMythWukongSaveMetadataText =>
        IsBlackMythWukongSelected
            ? FormatBlackMythWukongSaveMetadata(blackMythWukongSaveMetadata, CultureInfo.CurrentCulture, timeProvider)
            : null;
    /// <summary>True only when the selected local Elden Ring save is still available for slot selection.</summary>
    public bool CanSelectEldenRingProfile => ControlsEnabled
        && IsEldenRingSelected
        && IsAvailableEldenRingSaveFile(state?.EldenRingSave.LocalPath)
        && EldenRingProfileSlots.Count > 0;
    public EldenRingProfileSlotChoice? SelectedEldenRingProfileSlot { get; private set; }
    public bool IsManualGameSelected => state?.SelectedGameId is GameId id && IsManualGame(id);

    public bool CanDecrementManualDeaths => IsManualGameSelected && ManualDeaths > 0 && ControlsEnabled;
    public string? TotalDeathsOverlayUrl { get => totalDeathsOverlayUrl; private set { if (SetField(ref totalDeathsOverlayUrl, value)) { OnPropertyChanged(nameof(TotalDeathsSceneUrl)); OnPropertyChanged(nameof(TotalDeathsSceneUrlDisplay)); OnPropertyChanged(nameof(TotalDeathsPreviewUri)); } } }
    public string? BossListOverlayUrl { get => bossListOverlayUrl; private set { if (SetField(ref bossListOverlayUrl, value)) { OnPropertyChanged(nameof(BossListSceneUrl)); OnPropertyChanged(nameof(BossListSceneUrlDisplay)); OnPropertyChanged(nameof(BossListPreviewUri)); } } }
    /// <summary>Each generated URL contains only its own bounded, applied presentation values.</summary>
    public string? TotalDeathsSceneUrl => AppendStyleQuery(TotalDeathsOverlayUrl, totalDeaths: true);
    public string? BossListSceneUrl => AppendStyleQuery(BossListOverlayUrl, totalDeaths: false);
    /// <summary>Safe, compact presentation of a canonical URL. Copy always uses the full URL.</summary>
    public string? TotalDeathsSceneUrlDisplay => ShortenUrlForDisplay(TotalDeathsSceneUrl);
    /// <summary>Safe, compact presentation of a canonical URL. Copy always uses the full URL.</summary>
    public string? BossListSceneUrlDisplay => ShortenUrlForDisplay(BossListSceneUrl);
    public Uri? TotalDeathsPreviewUri => Uri.TryCreate(TotalDeathsSceneUrl, UriKind.Absolute, out Uri? uri) ? uri : null;
    public Uri? BossListPreviewUri => Uri.TryCreate(BossListSceneUrl, UriKind.Absolute, out Uri? uri) ? uri : null;
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
    public bool IsBossListOverlayEnabled { get => isBossListOverlayEnabled; private set => SetField(ref isBossListOverlayEnabled, value); }
    public BossListVisibilityMode BossListVisibilityMode { get => bossListVisibilityMode; private set => SetField(ref bossListVisibilityMode, value); }
    public LegacyImportViewModel? LegacyImport { get => legacyImport; private set => SetField(ref legacyImport, value); }
    public bool HasActiveLegacyImport => LegacyImport is { OfferVisible: true } or { ReviewVisible: true };
    public string? DeathSoundFileName => state?.DeathSound.LocalPath is { } path ? Path.GetFileName(path) : null;
    public bool IsDeathSoundEnabled => state?.DeathSound.IsEnabled ?? false;
    /// <summary>True when the user may choose a death-sound file for the enabled feature.</summary>
    public bool CanBrowseDeathSound => ControlsEnabled && IsDeathSoundEnabled;
    /// <summary>True when the enabled feature has a configured file that may be cleared.</summary>
    public bool CanClearDeathSound => CanBrowseDeathSound && state?.DeathSound.LocalPath is not null;
    /// <summary>True when the enabled feature has a local file that can be previewed safely.</summary>
    public bool CanPreviewDeathSound => CanBrowseDeathSound && state?.DeathSound.LocalPath is { } path && File.Exists(path);
    /// <summary>Volume controls are available only while the optional sound feature is enabled.</summary>
    public bool CanEditDeathSoundVolume => ControlsEnabled && IsDeathSoundEnabled;
    public int DeathSoundVolume => state?.DeathSound.Volume ?? 100;
    /// <summary>Editable volume draft; valid values persist after editing settles or is committed.</summary>
    public string DeathSoundVolumeText
    {
        get => deathSoundVolumeText;
        set => SetField(ref deathSoundVolumeText, value ?? string.Empty);
    }
    public string? DeathSoundStatus
    {
        get => deathSoundStatus;
        private set
        {
            if (SetField(ref deathSoundStatus, value))
            {
                OnPropertyChanged(nameof(IsDeathSoundVolumeUpdateSuccessful));
                OnPropertyChanged(nameof(IsDeathSoundVolumeValidationError));
            }
        }
    }

    /// <summary>True only for the requested successful percentage-save acknowledgement.</summary>
    public bool IsDeathSoundVolumeUpdateSuccessful =>
        DeathSoundStatus is not null && DeathSoundStatus.StartsWith("Volume changed to ", StringComparison.Ordinal);
    public bool IsDeathSoundVolumeValidationError =>
        string.Equals(DeathSoundStatus, DeathSoundVolumeValidationMessage, StringComparison.Ordinal);
    public string? TotalDeathsAppearanceStatus { get => totalDeathsAppearanceStatus; private set => SetField(ref totalDeathsAppearanceStatus, value); }
    public string? BossListAppearanceStatus { get => bossListAppearanceStatus; private set => SetField(ref bossListAppearanceStatus, value); }
    public string? TextExportStatus { get => textExportStatus; private set => SetField(ref textExportStatus, value); }
    public string? DeathsExportFileName => state?.TextExports.DeathsPath is { } path ? Path.GetFileName(path) : null;
    public string? BossExportFileName => state?.TextExports.BossListPath is { } path ? Path.GetFileName(path) : null;
    public bool IsDeathsExportEnabled => state?.TextExports.DeathsEnabled ?? false;
    public bool IsBossExportEnabled => state?.TextExports.BossListEnabled ?? false;
    public bool CanChooseDeathsExport => ControlsEnabled && IsDeathsExportEnabled;
    public bool CanClearDeathsExport => CanChooseDeathsExport && state?.TextExports.DeathsPath is not null;
    public bool CanChooseBossExport => ControlsEnabled && IsBossExportEnabled;
    public bool CanClearBossExport => CanChooseBossExport && state?.TextExports.BossListPath is not null;
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
            runtimeObservation = null;
            SetBlackMythWukongSaveMetadata(null);
            UpdateTotalDeathsText();
            OnPropertyChanged(nameof(RuntimeReaderStatusText));
            return;
        }

        bool blackMythWukongSaveIsUnconfigured = state.SelectedGameId == GameId.BlackMythWukong && state.BlackMythWukongSave.LocalPath is null;
        runtimeReaderGameId = result?.GameId;
        if (result is not null && result.GameId == state.SelectedGameId && !blackMythWukongSaveIsUnconfigured)
        {
            runtimeReaderStatus = result.Status;
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
            runtimeObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }
        else
        {
            runtimeReaderStatus = RuntimeGameReaderStatus.Unavailable;
            runtimeObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }

        UpdateTotalDeathsText();
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
    public void SetOverlayUrls(string totalDeathsUrl, string bossListUrl) { TotalDeathsOverlayUrl = totalDeathsUrl; BossListOverlayUrl = bossListUrl; }
    internal void SetOverlayReady() => LocalOverlayStatus = LocalOverlayReadyMessage;
    public void SetOverlayUnavailable()
    {
        TotalDeathsOverlayUrl = "Overlay endpoint unavailable. Close the conflicting local application and restart SoulsTracker.";
        BossListOverlayUrl = TotalDeathsOverlayUrl;
        LocalOverlayStatus = LocalOverlayUnavailableMessage;
    }
    internal void SetGlobalHotkeyStatus(string status) => GlobalHotkeyStatus = string.IsNullOrWhiteSpace(status)
        ? throw new ArgumentException("A global hotkey status is required.", nameof(status))
        : status;
    internal void ConfigureDeathSoundPlayback(IDeathSoundPlayer player)
    {
        if (deathSoundPlayer is not null)
        {
            deathSoundPlayer.PlaybackEnded -= DeathSoundPlayer_PlaybackEnded;
            deathSoundPlayer.PlaybackFailed -= DeathSoundPlayer_PlaybackFailed;
        }
        deathSoundPlayer = player ?? throw new ArgumentNullException(nameof(player));
        deathSoundPlayer.PlaybackEnded += DeathSoundPlayer_PlaybackEnded;
        deathSoundPlayer.PlaybackFailed += DeathSoundPlayer_PlaybackFailed;
        RefreshDeathSoundStatus();
        if (state is not null)
        {
            DeathSoundVolumeText = state.DeathSound.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Starts a local preview using the already-saved configuration; it never persists or changes tracking state.</summary>
    public void PreviewDeathSound()
    {
        if (!CanPreviewDeathSound || deathSoundPlayer is null)
        {
            DeathSoundStatus = "Death sound is unavailable.";
            return;
        }

        DeathSoundStatus = "Playing death sound.";
        deathSoundPlayer.Play(state!.DeathSound);
    }

    internal void SetTextExportStatus(bool succeeded) => TextExportStatus = succeeded ? "Text exports ready." : "Text export is unavailable.";

    public Task SetDeathSoundFileAsync(string selectedLocalPath, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || string.IsNullOrWhiteSpace(selectedLocalPath) || !File.Exists(selectedLocalPath))
        {
            DeathSoundStatus = "Death sound is unavailable.";
            return Task.CompletedTask;
        }
        try { return SaveDeathSoundAsync(new DeathSoundConfiguration(selectedLocalPath, IsDeathSoundEnabled, DeathSoundVolume), cancellationToken); }
        catch (ArgumentException) { DeathSoundStatus = "Death sound must be a WAV or MP3 file."; return Task.CompletedTask; }
    }
    public Task ClearDeathSoundAsync(CancellationToken cancellationToken = default) => SaveDeathSoundAsync(new DeathSoundConfiguration(null, IsDeathSoundEnabled, DeathSoundVolume), cancellationToken);
    public Task SetDeathSoundEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => SaveDeathSoundAsync(new DeathSoundConfiguration(state?.DeathSound.LocalPath, enabled, DeathSoundVolume), cancellationToken);
    public Task SetDeathSoundVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        try { return SaveDeathSoundAsync(new DeathSoundConfiguration(state?.DeathSound.LocalPath, IsDeathSoundEnabled, volume), cancellationToken); }
        catch (ArgumentOutOfRangeException) { DeathSoundStatus = DeathSoundVolumeValidationMessage; return Task.CompletedTask; }
    }
    public async Task SetDeathSoundVolumeTextAsync(string? volumeText, CancellationToken cancellationToken = default)
    {
        DeathSoundVolumeText = volumeText ?? string.Empty;
        await CommitDeathSoundVolumeTextAsync(cancellationToken);
    }

    /// <summary>Debounces an in-progress edit so partial values are never written during normal typing.</summary>
    public async Task QueueDeathSoundVolumeTextSaveAsync(CancellationToken cancellationToken = default)
    {
        long version = Interlocked.Increment(ref deathSoundVolumeEditVersion);
        await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken);
        await PersistDeathSoundVolumeTextAsync(version, cancellationToken);
    }

    /// <summary>Immediately commits the current volume draft on focus loss or Enter.</summary>
    public async Task CommitDeathSoundVolumeTextAsync(CancellationToken cancellationToken = default)
    {
        long version = Interlocked.Increment(ref deathSoundVolumeEditVersion);
        await PersistDeathSoundVolumeTextAsync(version, cancellationToken);
    }
    public Task SetDeathsExportPathAsync(string path, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(path, IsDeathsExportEnabled, state?.TextExports.BossListPath, IsBossExportEnabled), cancellationToken);
    public Task SetBossExportPathAsync(string path, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(state?.TextExports.DeathsPath, IsDeathsExportEnabled, path, IsBossExportEnabled), cancellationToken);
    public Task SetDeathsExportEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(state?.TextExports.DeathsPath, enabled, state?.TextExports.BossListPath, IsBossExportEnabled), cancellationToken);
    public Task SetBossExportEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(state?.TextExports.DeathsPath, IsDeathsExportEnabled, state?.TextExports.BossListPath, enabled), cancellationToken);
    public Task ClearDeathsExportAsync(CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(null, IsDeathsExportEnabled, state?.TextExports.BossListPath, IsBossExportEnabled), cancellationToken);
    public Task ClearBossExportAsync(CancellationToken cancellationToken = default) => SaveExportsAsync(new TextExportConfiguration(state?.TextExports.DeathsPath, IsDeathsExportEnabled, null, IsBossExportEnabled), cancellationToken);
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
            candidates = await Task.Run(async () => await eldenRingSaveDiscovery.DiscoverAsync(cancellationToken), cancellationToken);
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
        await SaveBlackMythWukongSaveAsync(new BlackMythWukongSaveConfiguration(localPath), cancellationToken);
        if (!IsCurrentWukongSelectionOperation(selectionVersion, localPath)) return;
        SetBlackMythWukongSaveDiscoveryStatus(CustomSaveTrackingStatus(localPath));
        IsBlackMythWukongChangeMode = false;
        WukongSaveSourceState = LocalSaveSourceState.CustomSelection;
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
        try
        {
            candidates = await Task.Run(async () => await blackMythWukongSaveDiscovery.DiscoverAsync(cancellationToken), cancellationToken);
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

    public async Task SetBossListScopeAsync(BossListScopeChoice? scope, CancellationToken cancellationToken = default)
    {
        if (!ControlsEnabled || scope is null || !scope.IsAvailable || scope.Value == state?.BossListScope) return;
        await SubmitAsync(new UpdateBossListScopeCommand(scope.Value), cancellationToken);
    }

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
        if (!IsManualGameSelected || !ControlsEnabled) return;

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
            BossSearchQuery = string.Empty;
        }

        if (state?.SelectedGameId == SoulsTracker.Domain.GameId.EldenRing)
        {
            await RescanEldenRingSavesAsync(cancellationToken);
        }
        if (state?.SelectedGameId == SoulsTracker.Domain.GameId.BlackMythWukong)
        {
            await RescanBlackMythWukongSavesAsync(cancellationToken);
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

    public Task SetBossDefeatedAsync(BossChoice? boss, bool isDefeated, CancellationToken cancellationToken = default) =>
        boss is null || state is null || !ControlsEnabled
            ? Task.CompletedTask
            : SubmitAsync(new SetBossDefeatedCommand(state.SelectedGameId, boss.BossId, isDefeated), cancellationToken);

    public Task SetTotalDeathsOverlayEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled || isEnabled == IsTotalDeathsOverlayEnabled
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(isEnabled, ShowTotalDeathsGameName, IsBossListOverlayEnabled, BossListVisibilityMode, cancellationToken);

    public Task SetShowTotalDeathsGameNameAsync(bool showGameName, CancellationToken cancellationToken = default) =>
        !CanConfigureTotalDeathsGameName || showGameName == ShowTotalDeathsGameName
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(IsTotalDeathsOverlayEnabled, showGameName, IsBossListOverlayEnabled, BossListVisibilityMode, cancellationToken);

    public Task SetBossListOverlayEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled || isEnabled == IsBossListOverlayEnabled
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(IsTotalDeathsOverlayEnabled, ShowTotalDeathsGameName, isEnabled, BossListVisibilityMode, cancellationToken);

    public Task SetBossListVisibilityModeAsync(BossListVisibilityMode visibilityMode, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled || !Enum.IsDefined(visibilityMode) || visibilityMode == BossListVisibilityMode
            ? Task.CompletedTask
            : SubmitOverlayPresentationAsync(IsTotalDeathsOverlayEnabled, ShowTotalDeathsGameName, IsBossListOverlayEnabled, visibilityMode, cancellationToken);

    public Task ResetOverlayAppearanceAsync(bool totalDeaths, CancellationToken cancellationToken = default) =>
        !PresentationControlsEnabled ? Task.CompletedTask : SubmitAsync(new ResetOverlayAppearanceCommand(totalDeaths), cancellationToken);

    public async Task ApplyOverlayAppearanceAsync(bool totalDeaths, CancellationToken cancellationToken = default)
    {
        if (!PresentationControlsEnabled || state is null) return;
        try
        {
            OverlayAppearance appearance = totalDeaths
                ? TotalDeathsAppearanceDraft.ToDomain(OverlayTextAlignment.Left)
                : BossListAppearanceDraft.ToDomain(BossListAppearanceDraft.Alignment);
            int maximumVisible = int.TryParse(DraftMaximumVisibleCount, out int count) ? count : throw new ArgumentException("Maximum visible bosses must be a whole number.");
            // The selector is the sole source of truth. Legacy booleans are projected only for compatibility.
            bool checkmark = !IsCenterBossAlignment && DraftBossMarker.Value == "Checkmark";
            bool skull = !IsCenterBossAlignment && DraftBossMarker.Value == "Skull";
            // Appearance drafts are isolated from Main-tab operational errors.
            // A failed Apply must leave the last applied style/URL/preview intact.
            await SubmitAsync(new UpdateOverlayAppearanceCommand(totalDeaths, appearance, false, true, DraftBossListMode, DraftDefeatedColor, DraftDefeatedTreatment, checkmark, DraftCheckmarkAccent, maximumVisible, DraftTitleIconModeChoice.Value, skull, DraftCenterMarkerAlignment), cancellationToken);
            SetAppearanceFeedback(totalDeaths, ErrorMessage is null
                ? $"{(totalDeaths ? "Total Deaths" : "Boss List")} appearance applied."
                : $"{(totalDeaths ? "Total Deaths" : "Boss List")} appearance could not be applied.");
        }
        catch (ArgumentException exception)
        {
            OverlayAppearanceDraft draft = totalDeaths ? TotalDeathsAppearanceDraft : BossListAppearanceDraft;
            string detail = draft.ValidationMessage ?? exception.Message.Split(Environment.NewLine)[0];
            SetAppearanceFeedback(totalDeaths, detail);
            return;
        }
    }

    private Task SubmitOverlayPresentationAsync(
        bool totalDeathsEnabled,
        bool showGameName,
        bool bossListEnabled,
        BossListVisibilityMode visibilityMode,
        CancellationToken cancellationToken) =>
        SubmitAsync(new UpdateOverlayPresentationCommand(totalDeathsEnabled, showGameName, bossListEnabled, visibilityMode), cancellationToken);

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

            if (command is IncrementManualBloodborneDeathsCommand && result.Status is TrackerCommandExecutionStatus.Applied or TrackerCommandExecutionStatus.DeliveryFailed)
            {
                deathSoundPlayer?.Play(state!.DeathSound);
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
        state = committedState ?? throw new ArgumentNullException(nameof(committedState));
        if (state.SelectedGameId != GameId.BlackMythWukong ||
            !string.Equals(previousWukongSavePath, state.BlackMythWukongSave.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            SetBlackMythWukongSaveMetadata(null);
        }
        bool blackMythWukongSaveIsUnconfigured = state.SelectedGameId == GameId.BlackMythWukong && state.BlackMythWukongSave.LocalPath is null;
        if (runtimeReaderGameId != state.SelectedGameId || blackMythWukongSaveIsUnconfigured)
        {
            runtimeObservation = null;
            runtimeReaderStatus = blackMythWukongSaveIsUnconfigured
                ? RuntimeGameReaderStatus.WaitingForSaveFile
                : RuntimeGameReaderStatus.Unavailable;
            runtimeReaderGameId = blackMythWukongSaveIsUnconfigured ? GameId.BlackMythWukong : null;
        }
        IsTotalDeathsOverlayEnabled = state.OverlayConfiguration.TotalDeaths.IsEnabled;
        ShowTotalDeathsGameName = state.OverlayConfiguration.TotalDeaths.ShowGameName;
        IsBossListOverlayEnabled = state.OverlayConfiguration.BossList.IsEnabled;
        BossListVisibilityMode = state.OverlayConfiguration.BossList.VisibilityMode;
        TotalDeathsAppearanceDraft.Load(state.OverlayConfiguration.TotalDeaths.Appearance);
        BossListAppearanceDraft.Load(state.OverlayConfiguration.BossList.Appearance);
        TotalDeathsAppearanceDraft.FontFamily = ResolveLocalFont(TotalDeathsAppearanceDraft.FontFamily);
        BossListAppearanceDraft.FontFamily = ResolveLocalFont(BossListAppearanceDraft.FontFamily);
        DraftTitleIconModeChoice = TitleIconModes.Single(choice => choice.Value == state.OverlayConfiguration.TotalDeaths.TitleIconMode);
        DraftBossListMode = state.OverlayConfiguration.BossList.VisibilityMode;
        DraftDefeatedColor = state.OverlayConfiguration.BossList.DefeatedColor;
        DraftDefeatedTreatment = state.OverlayConfiguration.BossList.DefeatedTreatment;
        DraftShowCheckmark = state.OverlayConfiguration.BossList.ShowCheckmark;
        DraftShowDefeatedSkull = state.OverlayConfiguration.BossList.ShowDefeatedSkull;
        DraftBossMarker = BossMarkers.Single(choice => choice.Value == (DraftShowDefeatedSkull ? "Skull" : DraftShowCheckmark ? "Checkmark" : "None"));
        DraftCheckmarkAccent = state.OverlayConfiguration.BossList.CheckmarkAccent;
        DraftCenterMarkerAlignment = state.OverlayConfiguration.BossList.CenterMarkerAlignment;
        DraftMaximumVisibleCount = state.OverlayConfiguration.BossList.MaximumVisibleCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RefreshDeathSoundStatus();
        GameId? selectedId = state.SelectedGameId;
        SelectedGame = GameChoices.Single(choice => choice.GameId == selectedId);
        SelectedEldenRingProfileSlot = EldenRingProfileSlots.SingleOrDefault(slot => slot.Index == state.EldenRingSave.SlotIndex);
        BossListScopes = BossListScopeChoice.For(GameCatalog.GetRequired(selectedId));
        SelectedBossListScope = BossListScopes.Single(scope => scope.Value == state.BossListScope);
        OnPropertyChanged(nameof(SelectedGame));
        OnPropertyChanged(nameof(SelectedEldenRingProfileSlot));
        OnPropertyChanged(nameof(SelectedBossListScope));
        OnPropertyChanged(nameof(BossDescription));

        Bosses.Clear();
        if (selectedId is not null)
        {
            GameDefinition game = GameCatalog.GetRequired(selectedId);
            foreach (BossDefinition boss in BossCatalogDisplayFilter.Apply(game, state.BossListScope))
            {
                Bosses.Add(new BossChoice(boss, state.BossProgress.IsDefeated(selectedId, boss.Id)));
            }
        }
        RefreshFilteredBosses();

        UpdateTotalDeathsText();
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
        NotifyTrackerProperties();
        NotifyEldenRingSaveSourceProperties();
        NotifyWukongSaveSourceProperties();
        OnPropertyChanged(nameof(TotalDeathsSceneUrl));
        OnPropertyChanged(nameof(BossListSceneUrl));
        OnPropertyChanged(nameof(TotalDeathsSceneUrlDisplay));
        OnPropertyChanged(nameof(BossListSceneUrlDisplay));
        OnPropertyChanged(nameof(TotalDeathsPreviewUri));
        OnPropertyChanged(nameof(BossListPreviewUri));
    }

    private void UpdateTotalDeathsText()
    {
        if (state is null)
        {
            return;
        }

        GameId selectedId = state.SelectedGameId;
        TotalDeathsText = runtimeObservation?.GameId == selectedId
            ? runtimeObservation.TotalDeaths.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : IsManualGame(selectedId)
                ? ManualDeaths.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : runtimeReaderStatus == RuntimeGameReaderStatus.WaitingForActiveCharacter
                    ? GameTotalDeathsWaitingForActiveCharacterMessage
                    : runtimeReaderStatus == RuntimeGameReaderStatus.WaitingForSaveFile
                        ? WaitingForSaveFileMessage(selectedId) + " to begin tracking."
                        : GameTotalDeathsUnavailableMessage;
    }

    private void RefreshFilteredBosses()
    {
        string query = BossSearchQuery.Trim();
        FilteredBosses.Clear();
        foreach (BossChoice boss in Bosses)
        {
            if (query.Length == 0 ||
                boss.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                boss.DlcLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                FilteredBosses.Add(boss);
            }
        }

        OnPropertyChanged(nameof(IsBossSearchNoResults));
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
        OverlayAppearance appearance = totalDeaths
            ? state.OverlayConfiguration.TotalDeaths.Appearance
            : state.OverlayConfiguration.BossList.Appearance;
        bool inline = totalDeaths && state.OverlayConfiguration.TotalDeaths.CompactTitle;
        bool gameName = totalDeaths && state.OverlayConfiguration.TotalDeaths.ShowGameName;
        DefeatedBossTreatment treatment = state.OverlayConfiguration.BossList.DefeatedTreatment;
        string marker = state.OverlayConfiguration.BossList.ShowDefeatedSkull
            ? "Skull"
            : state.OverlayConfiguration.BossList.ShowCheckmark
                ? "Checkmark"
                : "None";
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
        if (totalDeaths)
        {
            values["inline"] = "true";
            values["titleIcon"] = state.OverlayConfiguration.TotalDeaths.TitleIconMode.ToString();
        }
        else
        {
            BossListOverlayOptions boss = state.OverlayConfiguration.BossList;
            values["alignment"] = appearance.Alignment.ToString();
            values["mode"] = boss.VisibilityMode.ToString();
            values["defeatedColor"] = boss.DefeatedColor;
            values["treatment"] = treatment.ToString();
            values["marker"] = marker;
            values["maximumVisible"] = boss.MaximumVisibleCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["bossRowSpacing"] = appearance.Padding.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["centerMarkerAlignment"] = boss.CenterMarkerAlignment.ToString();
        }
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
        if (totalDeaths) TotalDeathsAppearanceStatus = message; else BossListAppearanceStatus = message;
        _ = ClearAppearanceFeedbackAfterDelayAsync(totalDeaths, message);
    }

    private async Task ClearAppearanceFeedbackAfterDelayAsync(bool totalDeaths, string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (totalDeaths && TotalDeathsAppearanceStatus == message) TotalDeathsAppearanceStatus = null;
        if (!totalDeaths && BossListAppearanceStatus == message) BossListAppearanceStatus = null;
    }

    private void NotifyTrackerProperties()
    {
        OnPropertyChanged(nameof(ControlsEnabled));
        OnPropertyChanged(nameof(ManualDeaths));
        OnPropertyChanged(nameof(IsBloodborneSelected));
        OnPropertyChanged(nameof(IsEldenRingSelected));
        OnPropertyChanged(nameof(IsBlackMythWukongSelected));
        OnPropertyChanged(nameof(CanSelectEldenRingProfile));
        OnPropertyChanged(nameof(EldenRingCharacterStatus));
        OnPropertyChanged(nameof(IsManualGameSelected));
        OnPropertyChanged(nameof(IsCenterBossAlignment));
        OnPropertyChanged(nameof(AreBossMarkerControlsVisible));
        OnPropertyChanged(nameof(IsBossMarkerSelected));
        OnPropertyChanged(nameof(ShowBossMarkerColor));
        OnPropertyChanged(nameof(CanDecrementManualDeaths));
        OnPropertyChanged(nameof(PresentationControlsEnabled));
        OnPropertyChanged(nameof(CanConfigureTotalDeathsGameName));
        OnPropertyChanged(nameof(DeathSoundFileName));
        OnPropertyChanged(nameof(IsDeathSoundEnabled));
        NotifyDeathSoundControlAvailability();
        OnPropertyChanged(nameof(DeathSoundVolume));
        OnPropertyChanged(nameof(DeathSoundVolumeText));
        OnPropertyChanged(nameof(DeathsExportFileName)); OnPropertyChanged(nameof(BossExportFileName)); OnPropertyChanged(nameof(IsDeathsExportEnabled)); OnPropertyChanged(nameof(IsBossExportEnabled));
        NotifyTextExportControlAvailability();
    }

    private void NotifyTextExportControlAvailability()
    {
        OnPropertyChanged(nameof(CanChooseDeathsExport));
        OnPropertyChanged(nameof(CanClearDeathsExport));
        OnPropertyChanged(nameof(CanChooseBossExport));
        OnPropertyChanged(nameof(CanClearBossExport));
    }

    private void NotifyDeathSoundControlAvailability()
    {
        OnPropertyChanged(nameof(CanBrowseDeathSound));
        OnPropertyChanged(nameof(CanClearDeathSound));
        OnPropertyChanged(nameof(CanPreviewDeathSound));
        OnPropertyChanged(nameof(CanEditDeathSoundVolume));
    }

    private void DeathSoundPlayer_PlaybackFailed(object? sender, EventArgs e) =>
        DeathSoundStatus = "Unable to play death sound.";

    private void DeathSoundPlayer_PlaybackEnded(object? sender, EventArgs e)
    {
        if (DeathSoundStatus == "Playing death sound.")
        {
            RefreshDeathSoundStatus();
        }
    }

    private static string WaitingForSaveFileMessage(GameId gameId) => gameId == GameId.BlackMythWukong
        ? BlackMythWukongWaitingForSaveFileMessage
        : GameWaitingForSaveFileMessage;

    private static bool IsManualGame(GameId gameId) => gameId == GameId.Bloodborne || gameId == GameId.DemonsSouls;

    private async Task PersistDeathSoundVolumeTextAsync(long version, CancellationToken cancellationToken)
    {
        string volumeText = DeathSoundVolumeText;
        if (!int.TryParse(volumeText, out int volume) || volume is < 0 or > 100)
        {
            DeathSoundStatus = DeathSoundVolumeValidationMessage;
            OnPropertyChanged(nameof(DeathSoundVolume));
            return;
        }

        Task queued;
        lock (deathSoundVolumeSaveSync)
        {
            queued = PersistDeathSoundVolumeTextAfterAsync(deathSoundVolumeSaveTail, version, volume, cancellationToken);
            deathSoundVolumeSaveTail = queued;
        }

        await queued;
    }

    private async Task PersistDeathSoundVolumeTextAfterAsync(Task prior, long version, int volume, CancellationToken cancellationToken)
    {
        try
        {
            await prior;
            if (version != Interlocked.Read(ref deathSoundVolumeEditVersion))
            {
                return;
            }

            if (state?.DeathSound.Volume != volume)
            {
                await SaveDeathSoundAsync(new DeathSoundConfiguration(state?.DeathSound.LocalPath, IsDeathSoundEnabled, volume), cancellationToken);
            }

            if (state?.DeathSound.Volume == volume)
            {
                DeathSoundStatus = $"Volume changed to {volume}%";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SaveDeathSoundAsync(DeathSoundConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!ControlsEnabled) return;
        IsBusy = true;
        string volumeDraft = DeathSoundVolumeText;
        try
        {
            ApplyCommittedState(await coordinator.SetDeathSoundConfigurationAsync(configuration, cancellationToken));
            DeathSoundVolumeText = volumeDraft;
        }
        catch { DeathSoundStatus = "Death sound settings could not be saved."; }
        finally { IsBusy = false; NotifyTrackerProperties(); }
    }

    private async Task SaveExportsAsync(TextExportConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!ControlsEnabled) return;
        try { ApplyCommittedState(await coordinator.SetTextExportConfigurationAsync(configuration, cancellationToken)); }
        catch { ErrorMessage = "Text export settings could not be saved."; }
    }

    private async Task SaveEldenRingSaveAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try { ApplyCommittedState(await coordinator.SetEldenRingSaveConfigurationAsync(configuration, cancellationToken)); }
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
                await coordinator.SetBlackMythWukongSaveConfigurationAsync(configuration, cancellationToken),
                preserveWukongMetadataOperation: true);
        }
        catch { ErrorMessage = "The Black Myth: Wukong save selection could not be saved."; }
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
        IReadOnlyList<EldenRingProfileSlotChoice> choices = localPath is null ? [] : await ReadEldenRingProfileChoicesAsync(localPath, cancellationToken);
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

    private void RefreshDeathSoundStatus()
    {
        // A committed-state refresh can arrive after a routed Save/Enter action. Do not
        // erase the explicit volume result the user just requested; it remains until the
        // next volume attempt (or a new app session) so the acknowledgement is observable.
        if (IsDeathSoundVolumeUpdateSuccessful || IsDeathSoundVolumeValidationError)
        {
            return;
        }

        DeathSoundStatus = state?.DeathSound.LocalPath is null ? null : File.Exists(state.DeathSound.LocalPath) ? "Death sound ready." : "Death sound is unavailable.";
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
    public string DisplayName => GameId == SoulsTracker.Domain.GameId.Bloodborne
        ? "Bloodborne [Manual]"
        : GameId == SoulsTracker.Domain.GameId.DemonsSouls
            ? "Demon Souls [Manual]"
        : definition!.DisplayName;
    public bool IsSelectable => definition!.IsSelectable;
    public string AvailabilityLabel => IsSelectable ? string.Empty : "SOON";
}

public sealed class BossChoice(BossDefinition definition, bool isDefeated)
{
    private readonly BossDefinition definition = definition ?? throw new ArgumentNullException(nameof(definition));
    public BossId BossId => definition.Id;
    public string DisplayName => definition.DisplayName;
    public string? DlcLabel => definition.DlcLabel;
    public bool IsDefeated { get; } = isDefeated;
}
