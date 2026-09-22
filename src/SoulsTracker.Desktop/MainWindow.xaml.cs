using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Navigation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.Win32;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Hosts the P3-01 manual tracking surface.</summary>
public partial class MainWindow : Window
{
    private bool isRestoringEldenRingProfileSelection;
    private bool isRestoringEldenRingSaveSelection;
    private bool isRestoringBlackMythWukongSaveSelection;
    private bool isRestoringLiesOfPSaveSelection;
    private readonly Func<string, string?, string?> chooseSaveDirectory;
    private readonly Action<string> copyDirectoryPath = text => System.Windows.Clipboard.SetText(text);
    private DesktopTrackerViewModel? directoryCopyViewModel;
    private (GameId? Game, string? Path) directoryCopyContext;
    private readonly Func<TimeSpan, Action, Action> scheduleCopyExpiry;
    private Action? cancelCopyExpiry;
    private long copyFeedbackVersion;
    private bool copyFeedbackClosed;
    private HostedOverlayConnection? copyFeedbackConnection;
    private string lastHostedCopyFeedback = string.Empty;
    private bool hostedCopyFeedbackVisible;
    private TabItem? hotkeyRecordingOrigin;
    private FrameworkElement? hotkeyRecordingAnchor;
    private FloatingFeedback? directoryFeedback;
    private FloatingFeedback? hotkeyFeedback;
    private Action? cancelHotkeyExpiry;
    private long hotkeyOperationVersion;
    private bool savingRecordedHotkey;

    public MainWindow()
    {
        chooseSaveDirectory = ChooseSaveDirectory;
        scheduleCopyExpiry = ScheduleCopyExpiry;
        InitializeComponent();
        directoryFeedback = new FloatingFeedback(FloatingFeedbackLayer, CopyFeedbackOverlay);
        hotkeyFeedback = new FloatingFeedback(FloatingFeedbackLayer, HotkeySuccessOverlay);
        AppearanceApplyStatus.GotKeyboardFocus += (_, _) =>
        {
            var tooltip = (System.Windows.Controls.ToolTip)AppearanceApplyStatus.ToolTip;
            tooltip.PlacementTarget = AppearanceApplyStatus;
            tooltip.IsOpen = AppearanceApplyStatus.Text.Length > 0;
        };
        AppearanceApplyStatus.LostKeyboardFocus += (_, _) => ((System.Windows.Controls.ToolTip)AppearanceApplyStatus.ToolTip).IsOpen = false;
        LayoutUpdated += (_, _) => { directoryFeedback.Update(); hotkeyFeedback.Update(); };
        WorkspaceTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, WorkspaceTabs)) return;
            if (!hostedCopyFeedbackVisible) ClearCopyFeedback();
            if (hotkeyRecordingOrigin is null) ClearHotkeyFeedback();
        };
    }

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory) : this() =>
        this.chooseSaveDirectory = chooseSaveDirectory;

    private void AppearanceRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var row = (Grid)sender;
        if (row.Children.Count != 2) return;
        bool narrow = e.NewSize.Width < 350;
        if (row.RowDefinitions.Count == 0)
        {
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var label = (FrameworkElement)row.Children[0];
        var field = (FrameworkElement)row.Children[1];
        Grid.SetColumnSpan(label, narrow ? 2 : 1);
        Grid.SetRow(field, narrow ? 1 : 0);
        Grid.SetColumn(field, narrow ? 0 : 1);
        Grid.SetColumnSpan(field, narrow ? 2 : 1);
        label.Margin = narrow ? new Thickness(0, 0, 0, 4) : new Thickness(0);
    }

    internal void ConfigureAppearancePreview(string dataRoot)
    {
        LocalAppearancePreview.IsVisibleChanged += async (_, _) =>
        {
            if (LocalAppearancePreview.IsVisible) await LocalAppearancePreview.StartBrowserAsync(dataRoot);
        };
    }

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory, Action<string> copyDirectoryPath) : this(chooseSaveDirectory) =>
        this.copyDirectoryPath = copyDirectoryPath;

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory, Action<string> copyDirectoryPath,
        Func<TimeSpan, Action, Action> scheduleCopyExpiry) : this(chooseSaveDirectory, copyDirectoryPath) =>
        this.scheduleCopyExpiry = scheduleCopyExpiry;

    private Action ScheduleCopyExpiry(TimeSpan delay, Action expire)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = delay };
        EventHandler tick = (_, _) => expire();
        timer.Tick += tick;
        timer.Start();
        return () => { timer.Stop(); timer.Tick -= tick; };
    }

    private void CopyDirectoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path } anchor || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            copyDirectoryPath(path);
            ShowCopyFeedback("Directory path copied", success: true);
        }
        catch { ShowCopyFeedback("The directory path could not be copied. Try again.", success: false); }
        if (!copyFeedbackClosed) directoryFeedback?.Show(anchor, MainContentScrollViewer);
    }

    private void ShowCopyFeedback(string message, bool success, bool hosted = false)
    {
        if (copyFeedbackClosed) return;
        ClearCopyFeedback();
        hostedCopyFeedbackVisible = hosted;
        var cue = (System.Windows.Media.Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        if (hosted)
        {
            HostedCopyStatus.Text = message;
            CopyFeedbackKind.Text = success ? "Copied" : "Copy failed";
            CopyFeedbackKind.Foreground = cue;
            HostedCopyFeedbackOverlay.BorderBrush = cue;
            HostedCopyFeedbackOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            DirectoryCopyStatus.Text = message;
            DirectoryCopyStatus.Foreground = cue;
            CopyFeedbackOverlay.BorderBrush = cue;
            CopyFeedbackOverlay.Visibility = Visibility.Visible;
        }
        if (success)
        {
            long version = copyFeedbackVersion;
            cancelCopyExpiry = scheduleCopyExpiry(TimeSpan.FromSeconds(5), () =>
            {
                if (!copyFeedbackClosed && version == copyFeedbackVersion) ClearCopyFeedback();
            });
        }
    }

    private void ClearCopyFeedback()
    {
        copyFeedbackVersion++;
        cancelCopyExpiry?.Invoke();
        cancelCopyExpiry = null;
        hostedCopyFeedbackVisible = false;
        DirectoryCopyStatus.Text = string.Empty;
        HostedCopyStatus.Text = string.Empty;
        CopyFeedbackKind.Text = string.Empty;
        directoryFeedback?.Hide();
        CopyFeedbackOverlay.Visibility = Visibility.Collapsed;
        HostedCopyFeedbackOverlay.Visibility = Visibility.Collapsed;
    }

    private string? ChooseSaveDirectory(string title, string? currentDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (System.IO.Directory.Exists(currentDirectory)) dialog.InitialDirectory = currentDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void ContextHelp_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ToolTip: System.Windows.Controls.ToolTip help } button)
        {
            help.PlacementTarget = button;
            help.IsOpen = true;
        }
    }

    private void ContextHelp_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ToolTip: System.Windows.Controls.ToolTip help }) help.IsOpen = false;
    }

    private void Window_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ClearHotkeyFeedback();
        (e.OldValue as DesktopTrackerViewModel)?.CancelHotkeyRecording();
        hotkeyRecordingOrigin = null;
        hotkeyRecordingAnchor = null;
        if (directoryCopyViewModel is not null) directoryCopyViewModel.PropertyChanged -= DirectoryCopyContext_PropertyChanged;
        directoryCopyViewModel = e.NewValue as DesktopTrackerViewModel;
        directoryCopyContext = GetDirectoryCopyContext(directoryCopyViewModel);
        ClearCopyFeedback();
        ObserveHostedCopyFeedback();
        if (directoryCopyViewModel is not null) directoryCopyViewModel.PropertyChanged += DirectoryCopyContext_PropertyChanged;
        if (e.NewValue is DesktopTrackerViewModel vm && vm.IsEldenRingNoticeVisible)
        {
            Dispatcher.BeginInvoke(() =>
                FocusEldenRingNoticePrimaryAction());
        }
    }


    private static (GameId? Game, string? Path) GetDirectoryCopyContext(DesktopTrackerViewModel? vm) =>
        (vm?.SelectedGame?.GameId, vm?.SelectedGame?.GameId switch
        {
            var game when game == GameId.EldenRing => vm?.EldenRingDirectoryPath,
            var game when game == GameId.BlackMythWukong => vm?.BlackMythWukongDirectoryPath,
            var game when game == GameId.LiesOfP => vm?.LiesOfPDirectoryPath,
            _ => null
        });

    private void DirectoryCopyContext_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopTrackerViewModel.TotalDeathsAppearanceStatus))
            Dispatcher.BeginInvoke(UpdateAppearanceFeedback);
        if (e.PropertyName == nameof(DesktopTrackerViewModel.IsEldenRingNoticeVisible))
            Dispatcher.BeginInvoke(FocusEldenRingNoticePrimaryAction);
        if (e.PropertyName is null or "" or nameof(DesktopTrackerViewModel.HostedOverlay))
        {
            if (Dispatcher.CheckAccess()) ObserveHostedCopyFeedback();
            else _ = Dispatcher.BeginInvoke(ObserveHostedCopyFeedback);
        }
        if (e.PropertyName is not (null or "" or nameof(DesktopTrackerViewModel.SelectedGame)
            or nameof(DesktopTrackerViewModel.EldenRingDirectoryPath)
            or nameof(DesktopTrackerViewModel.BlackMythWukongDirectoryPath)
            or nameof(DesktopTrackerViewModel.LiesOfPDirectoryPath)) || sender is not DesktopTrackerViewModel vm) return;

        // Capture each transition before dispatch so returning to an earlier
        // context cannot revive feedback when notifications arrive off-thread.
        var context = GetDirectoryCopyContext(vm);
        void ApplyContext()
        {
            if (!ReferenceEquals(directoryCopyViewModel, vm) || directoryCopyContext == context) return;
            if (directoryCopyContext.Game != context.Game) ClearHotkeyFeedback();
            directoryCopyContext = context;
            ClearCopyFeedback();
        }
        if (Dispatcher.CheckAccess()) ApplyContext();
        else _ = Dispatcher.BeginInvoke(ApplyContext);
    }

    protected override void OnClosed(EventArgs e)
    {
        LocalAppearancePreview.Dispose();
        copyFeedbackClosed = true;
        ClearCopyFeedback();
        ClearHotkeyFeedback();
        (DataContext as DesktopTrackerViewModel)?.CancelHotkeyRecording();
        hotkeyRecordingOrigin = null;
        hotkeyRecordingAnchor = null;
        if (directoryCopyViewModel is not null) directoryCopyViewModel.PropertyChanged -= DirectoryCopyContext_PropertyChanged;
        directoryCopyViewModel = null;
        ObserveHostedCopyFeedback();
        base.OnClosed(e);
    }

    private void ObserveHostedCopyFeedback()
    {
        var connection = directoryCopyViewModel?.HostedOverlay;
        if (ReferenceEquals(copyFeedbackConnection, connection)) return;
        if (copyFeedbackConnection is not null) copyFeedbackConnection.PropertyChanged -= HostedCopyFeedback_PropertyChanged;
        if (hostedCopyFeedbackVisible) ClearCopyFeedback();
        copyFeedbackConnection = connection;
        // Rebinding never revives an old copy notification.
        lastHostedCopyFeedback = connection?.CopyFeedbackText ?? string.Empty;
        if (connection is not null) connection.PropertyChanged += HostedCopyFeedback_PropertyChanged;
    }

    private void HostedCopyFeedback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (copyFeedbackClosed || sender is not HostedOverlayConnection connection || !ReferenceEquals(connection, copyFeedbackConnection)) return;
        string message = connection.CopyFeedbackText;
        if (message == lastHostedCopyFeedback) return;
        lastHostedCopyFeedback = message;
        if (message.Length == 0)
        {
            if (hostedCopyFeedbackVisible) ClearCopyFeedback();
        }
        else ShowCopyFeedback(message, message == "Read-only OBS URL copied. Keep the URL private.", hosted: true);
    }

    private async void GameSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        GameChoice? choice = e.AddedItems.OfType<GameChoice>().LastOrDefault();
        if (DataContext is DesktopTrackerViewModel viewModel && choice is not null)
        {
            // One-way binding also raises SelectionChanged when committed state
            // reapplies the current choice. That is presentation sync, not a
            // new user request; ignoring it prevents an initial no-op request
            // from racing a subsequent user selection.
            if (ReferenceEquals(choice, viewModel.SelectedGame))
            {
                return;
            }

            if (viewModel.RequestGameSelection(choice))
            {
                await SelectGameOnWindowDispatcherAsync(viewModel, choice);
            }
            else
            {
                GameSelector.SelectedItem = viewModel.SelectedGame;
            }
        }
    }

    private async Task SelectGameOnWindowDispatcherAsync(DesktopTrackerViewModel viewModel, GameChoice choice)
        => await RunOnWindowDispatcherAsync(() => viewModel.SelectGameAsync(choice));

    private async Task RunOnWindowDispatcherAsync(Func<Task> operation)
    {
        SynchronizationContext? priorContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
            await operation();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(priorContext);
        }
    }

    private async void ConfirmEldenRingNotice_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmEldenRingNoticeAsync();
    }

    private void CancelEldenRingNotice_Click(object sender, RoutedEventArgs e)
    {
        CancelEldenRingNotice();
    }

    private void GameSelector_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ShouldSuppressClosedGameSelectorWheel(GameSelector.IsDropDownOpen)) e.Handled = true;
    }

    internal static bool ShouldSuppressClosedGameSelectorWheel(bool isDropDownOpen) => !isDropDownOpen;

    private async void IncrementDeaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.IncrementManualDeathsAsync();
    }

    private async void IncrementEldenRingMissedDeaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.IncrementEldenRingMissedDeathsAsync();
    }

    private async void DecrementEldenRingMissedDeaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.DecrementEldenRingMissedDeathsAsync();
    }

    private async void DecrementDeaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.DecrementManualDeathsAsync();
    }

    private async void BrowseEldenRingSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel &&
            chooseSaveDirectory("Choose Elden Ring save directory", viewModel.EldenRingDirectoryPath) is { } directory)
        {
            await viewModel.SetEldenRingSaveDirectoryAsync(directory);
        }
    }

    private async void RescanEldenRingSaves_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.RescanEldenRingSavesAsync();
    }

    private void ChangeEldenRingSave_Click(object sender, RoutedEventArgs e)
    {
        BrowseEldenRingSave_Click(sender, e);
    }

    private void CancelEldenRingChange_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.CancelEldenRingChange();
    }

    private async void EldenRingSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringEldenRingSaveSelection) return;
        if (sender is System.Windows.Controls.ComboBox selector
            && e.AddedItems.OfType<DiscoveredLocalSave>().FirstOrDefault() is { } choice
            && DataContext is DesktopTrackerViewModel viewModel)
        {
            if (ReferenceEquals(choice, viewModel.SelectedEldenRingSaveChoice)) return;
            await viewModel.SelectEldenRingSaveChoiceAsync(choice);
            DiscoveredLocalSave? committedChoice = viewModel.SelectedEldenRingSaveChoice;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(selector.SelectedItem, committedChoice)) return;
                try
                {
                    isRestoringEldenRingSaveSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!ReferenceEquals(selector.SelectedItem, committedChoice))
                    {
                        selector.SetCurrentValue(System.Windows.Controls.ComboBox.SelectedItemProperty, committedChoice);
                    }
                }
                finally
                {
                    isRestoringEldenRingSaveSelection = false;
                }
            });
        }
    }

    private async void BrowseBlackMythWukongSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel &&
            chooseSaveDirectory("Choose Black Myth: Wukong save directory", viewModel.BlackMythWukongDirectoryPath) is { } directory)
        {
            await viewModel.SetBlackMythWukongSaveDirectoryAsync(directory);
        }
    }

    private async void RescanBlackMythWukongSaves_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.RescanBlackMythWukongSavesAsync();
    }

    private void ChangeBlackMythWukongSave_Click(object sender, RoutedEventArgs e)
    {
        BrowseBlackMythWukongSave_Click(sender, e);
    }

    private void CancelBlackMythWukongChange_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.CancelBlackMythWukongChange();
    }

    private async void BlackMythWukongSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringBlackMythWukongSaveSelection) return;
        if (sender is System.Windows.Controls.ComboBox selector
            && e.AddedItems.OfType<DiscoveredLocalSave>().FirstOrDefault() is { } choice
            && DataContext is DesktopTrackerViewModel viewModel)
        {
            if (ReferenceEquals(choice, viewModel.SelectedBlackMythWukongSaveChoice)) return;
            await viewModel.SelectBlackMythWukongSaveChoiceAsync(choice);
            DiscoveredLocalSave? committedChoice = viewModel.SelectedBlackMythWukongSaveChoice;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(selector.SelectedItem, committedChoice)) return;
                try
                {
                    isRestoringBlackMythWukongSaveSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!ReferenceEquals(selector.SelectedItem, committedChoice))
                    {
                        selector.SetCurrentValue(System.Windows.Controls.ComboBox.SelectedItemProperty, committedChoice);
                    }
                }
                finally
                {
                    isRestoringBlackMythWukongSaveSelection = false;
                }
            });
        }
    }

    private async void BrowseLiesOfPSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel &&
            chooseSaveDirectory("Choose Lies of P save directory", viewModel.LiesOfPDirectoryPath) is { } directory)
        {
            await viewModel.SetLiesOfPSaveDirectoryAsync(directory);
        }
    }

    private async void RescanLiesOfPSaves_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.RescanLiesOfPSavesAsync();
    }

    private void ChangeLiesOfPSave_Click(object sender, RoutedEventArgs e)
    {
        BrowseLiesOfPSave_Click(sender, e);
    }

    private void CancelLiesOfPChange_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.CancelLiesOfPChange();
    }

    private async void LiesOfPSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringLiesOfPSaveSelection) return;
        if (sender is System.Windows.Controls.ComboBox selector &&
            e.AddedItems.OfType<DiscoveredLocalSave>().FirstOrDefault() is { } choice &&
            DataContext is DesktopTrackerViewModel viewModel)
        {
            if (ReferenceEquals(choice, viewModel.SelectedLiesOfPSaveChoice)) return;
            await viewModel.SelectLiesOfPSaveChoiceAsync(choice);
            DiscoveredLocalSave? committedChoice = viewModel.SelectedLiesOfPSaveChoice;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    isRestoringLiesOfPSaveSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!ReferenceEquals(selector.SelectedItem, committedChoice)) selector.SelectedItem = committedChoice;
                }
                finally
                {
                    isRestoringLiesOfPSaveSelection = false;
                }
            });
        }
    }

    private async void EldenRingProfileSlot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringEldenRingProfileSelection) return;
        if (DataContext is DesktopTrackerViewModel viewModel
            && sender is System.Windows.Controls.ComboBox { SelectedItem: EldenRingProfileSlotChoice slot } selector
            && viewModel.SelectedEldenRingProfileSlot != slot)
        {
            await viewModel.SetEldenRingProfileSlotAsync(slot);
            EldenRingProfileSlotChoice? committedSlot = viewModel.SelectedEldenRingProfileSlot;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                if (Equals(selector.SelectedItem, committedSlot)) return;
                try
                {
                    isRestoringEldenRingProfileSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!Equals(selector.SelectedItem, committedSlot))
                    {
                        selector.SetCurrentValue(System.Windows.Controls.ComboBox.SelectedItemProperty, committedSlot);
                    }
                }
                finally
                {
                    isRestoringEldenRingProfileSelection = false;
                }
            });
        }
    }

    private void IncrementHotkeyTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => BeginHotkeyRecording(increment: true);

    private void DecrementHotkeyTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => BeginHotkeyRecording(increment: false);

    private void BeginHotkeyRecording(bool increment)
    {
        if (DataContext is not DesktopTrackerViewModel viewModel || viewModel.IsHotkeyRecording || savingRecordedHotkey) return;

        viewModel.BeginHotkeyRecording(increment);
        if (viewModel.IsHotkeyRecording)
        {
            ClearHotkeyFeedback();
            hotkeyRecordingOrigin = WorkspaceTabs.SelectedItem as TabItem;
            hotkeyRecordingAnchor = increment ? IncrementHotkeyTextBox : DecrementHotkeyTextBox;
            Dispatcher.BeginInvoke(() =>
            {
                if (!copyFeedbackClosed && ReferenceEquals(DataContext, viewModel) && viewModel.IsHotkeyRecording)
                    Keyboard.Focus(HotkeyRecordingOverlay);
            });
        }
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (await HandleEldenRingNoticeKeyAsync(key))
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not DesktopTrackerViewModel { IsHotkeyRecording: true } viewModel) return;

        if (key == Key.Escape)
        {
            ClearHotkeyFeedback();
            viewModel.CancelHotkeyRecording();
            e.Handled = true;
            ReturnToHotkeyRecordingOrigin();
            return;
        }

        if (key == Key.Enter)
        {
            e.Handled = true;
            if (savingRecordedHotkey) return;
            ClearHotkeyFeedback();
            long version = hotkeyOperationVersion;
            var origin = hotkeyRecordingOrigin;
            savingRecordedHotkey = true;
            bool applied;
            try { applied = await viewModel.SaveRecordedHotkeyAsync(); }
            finally { savingRecordedHotkey = false; }
            if (!copyFeedbackClosed && ReferenceEquals(DataContext, viewModel) && origin is not null && ReferenceEquals(origin, hotkeyRecordingOrigin))
            {
                bool current = version == hotkeyOperationVersion;
                ReturnToHotkeyRecordingOrigin();
                if (applied && current) ShowHotkeyFeedback(hotkeyRecordingAnchor ?? ApplyHotkeysButton);
            }
            return;
        }

        viewModel.CaptureRecordedHotkey(key, Keyboard.Modifiers);
        e.Handled = true;
    }

    internal async Task<bool> HandleEldenRingNoticeKeyAsync(Key key)
    {
        if (DataContext is not DesktopTrackerViewModel { IsEldenRingNoticeVisible: true }) return false;

        if (key == Key.Escape)
        {
            CancelEldenRingNotice();
            return true;
        }

        if (key == Key.Enter)
        {
            await ConfirmEldenRingNoticeAsync();
            return true;
        }

        return false;
    }

    private async Task ConfirmEldenRingNoticeAsync()
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.ConfirmEldenRingNoticeAsync();
            void ApplyConfirmedSelection()
            {
                GameSelector.SelectedItem = viewModel.SelectedGame;
                RestoreGameSelectorFocus();
            }

            if (Dispatcher.CheckAccess()) ApplyConfirmedSelection();
            else _ = Dispatcher.BeginInvoke(ApplyConfirmedSelection);
        }
    }

    private void CancelEldenRingNotice()
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            viewModel.CancelEldenRingNotice();
            GameSelector.SelectedItem = viewModel.SelectedGame;
            RestoreGameSelectorFocus();
        }
    }

    private void FocusEldenRingNoticePrimaryAction()
    {
        if (DataContext is not DesktopTrackerViewModel { IsEldenRingNoticeVisible: true }) return;
        FocusManager.SetFocusedElement(EldenRingNoticeOverlay, EldenRingNoticeDialog);
        Keyboard.Focus(EldenRingNoticeDialog);
    }

    private void RestoreGameSelectorFocus()
    {
        if (!IsLoaded || DataContext is DesktopTrackerViewModel { IsEldenRingNoticeVisible: true }) return;
        Keyboard.Focus(GameSelector);
    }

    private void Window_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is not DesktopTrackerViewModel { IsEldenRingNoticeVisible: true } ||
            e.NewFocus is not DependencyObject nextFocus || IsDescendantOf(nextFocus, EldenRingNoticeOverlay)) return;

        e.Handled = true;
        Dispatcher.BeginInvoke(FocusEldenRingNoticePrimaryAction);
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? current = child; current is not null;)
        {
            if (ReferenceEquals(current, ancestor)) return true;

            current = current is Visual visual
                ? VisualTreeHelper.GetParent(visual)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ReturnToHotkeyRecordingOrigin()
    {
        var origin = hotkeyRecordingOrigin;
        hotkeyRecordingOrigin = null;
        if (copyFeedbackClosed || origin is null) return;
        WorkspaceTabs.SelectedItem = origin;
        WorkspaceTabs.UpdateLayout();
        var content = FindScrollViewer(origin.Content as DependencyObject);
        if (content is not null)
        {
            content.Focusable = true;
            Keyboard.Focus(content);
        }
        else Keyboard.Focus(WorkspaceTabs);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? element)
    {
        if (element is ScrollViewer scroll) return scroll;
        if (element is null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } result) return result;
        return null;
    }

    private async void ApplyHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopTrackerViewModel viewModel) return;
        ClearHotkeyFeedback();
        long version = hotkeyOperationVersion;
        if (await viewModel.ApplyGlobalHotkeysAsync() && !copyFeedbackClosed &&
            ReferenceEquals(DataContext, viewModel) && version == hotkeyOperationVersion)
            ShowHotkeyFeedback(ApplyHotkeysButton);
    }

    private void ClearHotkeyFeedback()
    {
        hotkeyOperationVersion++;
        cancelHotkeyExpiry?.Invoke();
        cancelHotkeyExpiry = null;
        if (HotkeySuccessStatus is not null) HotkeySuccessStatus.Text = string.Empty;
        hotkeyFeedback?.Hide();
    }

    private void ShowHotkeyFeedback(FrameworkElement anchor)
    {
        if (!SettingsWorkspaceTab.IsSelected) return;
        ClearHotkeyFeedback();
        HotkeySuccessStatus.Text = "Hotkeys applied successfully";
        hotkeyFeedback?.Show(anchor, SettingsContentScrollViewer, ApplyHotkeysButton, GlobalHotkeysHelpButton);
        long version = hotkeyOperationVersion;
        cancelHotkeyExpiry = scheduleCopyExpiry(TimeSpan.FromSeconds(5), () =>
        {
            if (!copyFeedbackClosed && version == hotkeyOperationVersion) ClearHotkeyFeedback();
        });
    }

    private async void TotalDeathsOverlayEnabled_Checked(object sender, RoutedEventArgs e) =>
        await SetTotalDeathsOverlayEnabledAsync(isEnabled: true);


    private async void TotalDeathsOverlayEnabled_Unchecked(object sender, RoutedEventArgs e) =>
        await SetTotalDeathsOverlayEnabledAsync(isEnabled: false);

    private async Task SetTotalDeathsOverlayEnabledAsync(bool isEnabled)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && viewModel.IsTotalDeathsOverlayEnabled != isEnabled)
        {
            await viewModel.SetTotalDeathsOverlayEnabledAsync(isEnabled);
        }
    }

    private async void TotalDeathsGameName_Checked(object sender, RoutedEventArgs e) =>
        await SetTotalDeathsGameNameAsync(showGameName: true);

    private async void TotalDeathsGameName_Unchecked(object sender, RoutedEventArgs e) =>
        await SetTotalDeathsGameNameAsync(showGameName: false);

    private async Task SetTotalDeathsGameNameAsync(bool showGameName)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && viewModel.ShowTotalDeathsGameName != showGameName)
        {
            await viewModel.SetShowTotalDeathsGameNameAsync(showGameName);
        }
    }


    private async void ApplyTotalDeathsAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            AppearanceApplyStatus.Text = "";
            await viewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);
            if (!copyFeedbackClosed) UpdateAppearanceFeedback();
        }
    }

    private void UpdateAppearanceFeedback()
    {
        if (copyFeedbackClosed || DataContext is not DesktopTrackerViewModel vm) return;
        string? message = vm.TotalDeathsAppearanceStatus;
        if (string.IsNullOrEmpty(message) && !vm.IsAppearanceApplySuccessful) return;
        AppearanceApplyStatus.Tag = message;
        System.Windows.Automation.AutomationProperties.SetHelpText(AppearanceApplyStatus, message ?? "");
        AppearanceApplyStatus.Text = message is { Length: > 80 } ? "Correct the highlighted appearance fields, then Apply." : message ?? "";
    }

    private bool appearanceResetConfirmationOpen;
    private async void ResetSelectedOverlayAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (appearanceResetConfirmationOpen || DataContext is not DesktopTrackerViewModel { PresentationControlsEnabled: true } viewModel) return;
        appearanceResetConfirmationOpen = true;
        try
        {
            var dialog = new AppearanceResetDialog { Owner = this, Resources = Resources };
            if (dialog.ShowDialog() == true) await viewModel.ResetOverlayAppearanceAsync(totalDeaths: true);
        }
        finally
        {
            appearanceResetConfirmationOpen = false;
            if (!copyFeedbackClosed) AppearanceActions.Focus();
        }
    }


    private void CopyTotalDeathsOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopTrackerViewModel { HostedOverlay: { CanCopy: true } connection }) return;
        string previous = lastHostedCopyFeedback;
        bool success = connection.CopyReadUrl(copyDirectoryPath);
        // Repeated identical copies still reset the same notification's lifetime.
        if (previous == lastHostedCopyFeedback) ShowCopyFeedback(connection.CopyFeedbackText, success, hosted: true);
    }

    private async void ImportHostedPairing_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopTrackerViewModel { HostedOverlay: { CanImport: true } connection } || HostedConsentCheckBox.IsChecked != true) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Pairing JSON (*.json)|*.json", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) await connection.ImportAsync(dialog.FileName, HostedConsentCheckBox.IsChecked == true);
    }

    private async void ReconnectHosted_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { HostedOverlay: { } connection }) await connection.ReconnectAsync();
    }

    private async void RemoveHostedPairing_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopTrackerViewModel { HostedOverlay: { CanRemove: true } connection }) return;
        bool confirmed = System.Windows.MessageBox.Show(this,
            "Stop hosted publication and remove protected pairing from this PC? Cloud state and read/write capabilities will NOT be revoked or deleted. Contact the operator for remote revocation or deletion.",
            "Remove local pairing", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        await connection.RemoveAsync(confirmed);
    }

    private static void CopyOverlayUrl(System.Windows.Controls.Button? button, string? value)
    {
        if (button is null || string.IsNullOrWhiteSpace(value)) return;
        System.Windows.Clipboard.SetText(value);
        button.Content = "Copied";
        System.Windows.Automation.AutomationProperties.SetHelpText(button, "URL copied.");
        var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        reset.Tick += (_, _) => { reset.Stop(); button.Content = "Copy"; System.Windows.Automation.AutomationProperties.SetHelpText(button, "Copy URL."); };
        reset.Start();
    }
    private static Microsoft.Win32.SaveFileDialog CreateTextExportDialog() => new() { Filter = "Text files (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true, OverwritePrompt = false };
    private async void ChooseDeathsExport_Click(object sender, RoutedEventArgs e) { var dialog = CreateTextExportDialog(); if (dialog.ShowDialog(this) == true && DataContext is DesktopTrackerViewModel viewModel) await viewModel.SetDeathsExportPathAsync(dialog.FileName); }
    private async void ClearDeathsExport_Click(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ClearDeathsExportAsync(); }
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.CheckForUpdatesAsync(); }
    private void OpenUpdateReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.OpenAvailableUpdateReleasePage();
    }
    // These controls intentionally use one-way bindings: committed state remains
    // the source of truth after asynchronous persistence completes. Do not gate a
    // routed toggle event on the currently committed value, though. A second user
    // interaction can arrive before the first save returns, and stale state would
    // otherwise drop that interaction (and make the checkbox visibly flicker).
    private async void DeathsExportEnabled_Checked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetDeathsExportEnabledAsync(true); }
    private async void DeathsExportEnabled_Unchecked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetDeathsExportEnabledAsync(false); }

    private async void ReviewLegacyImport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { LegacyImport: not null } viewModel && sender is System.Windows.Controls.Button { Tag: SoulsTracker.Infrastructure.LegacyImportCandidate candidate })
        {
            await viewModel.LegacyImport.ReviewAsync(candidate);
        }
    }

    private async void ImportReviewedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { LegacyImport: not null } viewModel) await viewModel.LegacyImport.ImportReviewedSettingsAsync();
    }

    private void CancelLegacyImport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel { LegacyImport: not null } viewModel) viewModel.LegacyImport.Cancel();
    }

    private void BeingKairoAttributionHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Attribution is optional and must never affect local tracking when no browser can launch.
        }

        e.Handled = true;
    }
}
