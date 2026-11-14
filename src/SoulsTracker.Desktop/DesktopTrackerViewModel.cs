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
public sealed partial class DesktopTrackerViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    internal const string LocalTrackerStateReadyMessage = "Local tracker state is ready.";
    internal const string LocalTrackerStateUnavailableMessage = "Local tracker state is unavailable. Tracker controls remain disabled.";

    internal const string GameUnavailableMessage = "Game unavailable";
    internal const string GameWaitingForSaveFileMessage = "Choose an Elden Ring save directory";
    internal const string BlackMythWukongWaitingForSaveFileMessage = "Choose a Black Myth: Wukong save directory";
    internal const string LiesOfPWaitingForSaveFileMessage = "Choose a Lies of P save directory";
    internal const string EldenRingChooseCharacterMessage = "Choose a character to continue.";
    internal const string GameSyncedMessage = "Synced";
    internal const string NoDeathsRecordedMessage = "No deaths recorded yet, the tracker will update after your first death";
    internal const string SelectedSaveUnreadableMessage = "Selected save cannot currently be read. Save in-game, then Refresh or change the selected save.";
    internal const string GameTotalDeathsUnavailableMessage = "Unable to read total deaths.";
    internal const string GameTotalDeathsWaitingForActiveCharacterMessage = "Unavailable, waiting for active character.";


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
    private Func<bool, Task>? saveTrayClosePreference;
    private bool minimizeToTrayOnClose;
    private bool isSavingTrayClosePreference;
    private string trayClosePreferenceStatus = string.Empty;

    private PersistentTrackerState? state;
    private bool isLoading = true;
    private bool isBusy;
    private string? errorMessage;
    private string? localTrackerStateStatus;

    private LegacyImportViewModel? legacyImport;
    private readonly TimeProvider timeProvider;
    private readonly Func<string, CancellationToken, Task<WukongSaveMetadataReadResult>> readWukongSaveMetadataAsync;

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
        updateCurrentVersion = NormalizeProductVersion(this.installedVersionProvider());
        updateLatestVersion = "Not checked";
        updateCheckStatus = "Not checked yet.";
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
    public bool MinimizeToTrayOnClose { get => minimizeToTrayOnClose; private set => SetField(ref minimizeToTrayOnClose, value); }
    public bool IsSavingTrayClosePreference => isSavingTrayClosePreference;
    public bool CanChangeTrayClosePreference => saveTrayClosePreference is not null && !isSavingTrayClosePreference;
    public string TrayClosePreferenceStatus { get => trayClosePreferenceStatus; private set => SetField(ref trayClosePreferenceStatus, value); }

    internal void ConfigureTrayClosePreference(bool enabled, Func<bool, Task> save)
    {
        saveTrayClosePreference = save ?? throw new ArgumentNullException(nameof(save));
        MinimizeToTrayOnClose = enabled;
        OnPropertyChanged(nameof(CanChangeTrayClosePreference));
    }

    public async Task SetMinimizeToTrayOnCloseAsync(bool enabled)
    {
        if (!CanChangeTrayClosePreference) return;
        isSavingTrayClosePreference = true;
        OnPropertyChanged(nameof(CanChangeTrayClosePreference));
        try
        {
            await saveTrayClosePreference!(enabled);
            MinimizeToTrayOnClose = enabled;
            TrayClosePreferenceStatus = string.Empty;
        }
        catch
        {
            TrayClosePreferenceStatus = "The close setting could not be saved. Your previous choice is still active.";
            OnPropertyChanged(nameof(MinimizeToTrayOnClose));
        }
        finally
        {
            isSavingTrayClosePreference = false;
            OnPropertyChanged(nameof(CanChangeTrayClosePreference));
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (SetField(ref isLoading, value))
            {
                OnPropertyChanged(nameof(ControlsEnabled));
                OnPropertyChanged(nameof(CanChangeUpdatePreference));
                OnPropertyChanged(nameof(CanCheckForUpdates));
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
                OnPropertyChanged(nameof(CanChangeUpdatePreference));
                OnPropertyChanged(nameof(CanCheckForUpdates));
                OnPropertyChanged(nameof(PresentationControlsEnabled));
                OnPropertyChanged(nameof(CanConfigureTotalDeathsGameName));
                OnPropertyChanged(nameof(CanSelectEldenRingProfile));
                NotifyTextExportControlAvailability();
                NotifyHollowKnight();
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
    public bool IsEldenRingSaveSelectorVisible => EldenRingSaveChoices.Count > 0 && (IsEldenRingChangeMode || EldenRingSaveSourceState == LocalSaveSourceState.MultipleCandidates);
    public bool IsEldenRingSaveSelectorEnabled => ControlsEnabled && EldenRingSaveSourceState != LocalSaveSourceState.Scanning && IsEldenRingSaveSelectorVisible;
    public bool IsEldenRingBrowseVisible => EldenRingSaveSourceState is LocalSaveSourceState.Scanning or LocalSaveSourceState.NoCandidate or LocalSaveSourceState.MultipleCandidates || IsEldenRingChangeMode;
    public bool IsEldenRingChangeVisible => !IsEldenRingChangeMode && EldenRingSaveSourceState is LocalSaveSourceState.AutomaticallySelected or LocalSaveSourceState.PersistedDiscovered or LocalSaveSourceState.CustomSelection or LocalSaveSourceState.UnavailableSelection;
    public bool IsEldenRingCancelVisible => IsEldenRingChangeMode;

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
    public string? LocalTrackerStateStatus { get => localTrackerStateStatus; private set => SetField(ref localTrackerStateStatus, value); }

    public LegacyImportViewModel? LegacyImport { get => legacyImport; private set => SetField(ref legacyImport, value); }
    public bool HasActiveLegacyImport => LegacyImport is { OfferVisible: true } or { ReviewVisible: true };
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



    public HostedOverlayConnection? HostedOverlay { get; private set; }
    internal void ConfigureHostedOverlay(HostedOverlayConnection connection)
    {
        HostedOverlay = connection;
        OnPropertyChanged(nameof(HostedOverlay));
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

            startupUpdateCheckEnabled = result.State!.CheckForUpdatesOnStartup;
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

    private void ApplyCommittedState(
        PersistentTrackerState committedState,
        bool preserveWukongMetadataOperation = false)
    {
        if (state is not null && state.SelectedGameId != committedState.SelectedGameId)
        {
            CancelDirectorySelection(state.SelectedGameId);
            Interlocked.Increment(ref eldenRingDiscoveryVersion);
            saveGameConfigurationWorkflow.BeginLiesSelection();
        }
        if (!preserveWukongMetadataOperation)
        {
            InvalidateWukongOperations();
        }
        string? previousWukongSavePath = state?.BlackMythWukongSave.LocalPath;
        string? previousSource = state is null ? null : EffectiveDeathTotalResult.SourceIdentityFor(state);
        if (previousSource != EffectiveDeathTotalResult.SourceIdentityFor(committedState))
        {
            saveReaderSelectionEpoch++;
            pluginObservation = null;
            pluginStatus = null;
        }
        isInitialDeathReadPending = previousSource != EffectiveDeathTotalResult.SourceIdentityFor(committedState)
            ? !IsManualGame(committedState.SelectedGameId) : isInitialDeathReadPending;
        state = committedState ?? throw new ArgumentNullException(nameof(committedState));
        OnPropertyChanged(nameof(CheckForUpdatesOnStartup));

        if (state.SelectedGameId != GameId.BlackMythWukong ||
            !string.Equals(previousWukongSavePath, state.BlackMythWukongSave.LocalPath, StringComparison.Ordinal))
        {
            SetBlackMythWukongSaveMetadata(null);
        }
        bool blackMythWukongSaveIsUnconfigured = state.SelectedGameId == GameId.BlackMythWukong && state.BlackMythWukongSave.LocalPath is null;
        bool liesOfPSaveIsUnconfigured = state.SelectedGameId == GameId.LiesOfP && state.LiesOfPSave.LocalPath is null;
        if (runtimeReaderGameId != state.SelectedGameId || blackMythWukongSaveIsUnconfigured || liesOfPSaveIsUnconfigured)
        {
            runtimeObservation = null;
            pluginObservation = null;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeReaderStatus = blackMythWukongSaveIsUnconfigured || liesOfPSaveIsUnconfigured
                ? RuntimeGameReaderStatus.WaitingForSaveFile
                : RuntimeGameReaderStatus.Unavailable;
            runtimeReaderGameId = blackMythWukongSaveIsUnconfigured ? GameId.BlackMythWukong : liesOfPSaveIsUnconfigured ? GameId.LiesOfP : null;
        }
        else if (!string.Equals(previousSource, EffectiveDeathTotalResult.SourceIdentityFor(state), StringComparison.Ordinal))
        {
            runtimeObservation = null;
            pluginObservation = null;
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
        GameChoice selectedChoice = GameChoices.Single(choice => choice.GameId == selectedId);
        if (!ReferenceEquals(SelectedGame, selectedChoice))
        {
            SelectedGame = selectedChoice;
            OnPropertyChanged(nameof(SelectedGame));
            OnPropertyChanged(nameof(GameSelectionAutomationName));
        }
        SelectedEldenRingProfileSlot = EldenRingProfileSlots.SingleOrDefault(slot => slot.Index == state.EldenRingSave.SlotIndex);

        OnPropertyChanged(nameof(SelectedEldenRingProfileSlot));


        UpdateTotalDeathsText();
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
        NotifyTrackerProperties();
        NotifyEldenRingSaveSourceProperties();
        NotifyWukongSaveSourceProperties();
        NotifyLiesOfPSaveSourceProperties();
        NotifyDirectoryPaths();


    }


    private static string LoadFailureMessage(TrackerStateLoadFailureKind kind) => kind switch
    {
        TrackerStateLoadFailureKind.UnsupportedVersion => "Stored tracker state is from an unsupported version. Update SoulsTracker or restore a compatible local backup.",
        TrackerStateLoadFailureKind.Integrity => "Local tracker state failed validation. It was not replaced; close the app and restore a known-good local backup.",
        _ => "Local tracker state could not be read. It was not replaced; close the app and try again.",
    };

    private void NotifyTrackerProperties()
    {
        OnPropertyChanged(nameof(ControlsEnabled));
        OnPropertyChanged(nameof(IsEldenRingSaveSelectorEnabled));
        OnPropertyChanged(nameof(IsWukongSaveSelectorEnabled));
        OnPropertyChanged(nameof(IsLiesOfPSaveSelectorEnabled));
        OnPropertyChanged(nameof(ManualDeaths));

        OnPropertyChanged(nameof(IsEldenRingSelected));
        OnPropertyChanged(nameof(IsBlackMythWukongSelected));
        OnPropertyChanged(nameof(IsLiesOfPSelected));
        NotifyHollowKnight();
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

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);


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
