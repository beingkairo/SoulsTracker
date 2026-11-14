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
    private const double NormalPreviewBottomInset = 70;
    private const double DockedPreviewBottomInset = 54;
    private bool isRestoringEldenRingProfileSelection;
    private bool isRestoringEldenRingSaveSelection;
    private bool isRestoringBlackMythWukongSaveSelection;
    private bool isRestoringLiesOfPSaveSelection;
    private readonly Func<string, string?, string?> chooseSaveDirectory;
    internal Func<string, MessageBoxResult>? HollowKnightInstallConfirmation { get; set; }
    internal Func<DesktopTrackerViewModel, Task<HollowKnightInstallPlan?>>? HollowKnightInstallPlanProvider { get; set; }
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
    private FloatingFeedback? hostedFeedback;
    private FloatingFeedback? hotkeyFeedback;
    private Action? cancelHotkeyExpiry;
    private long hotkeyOperationVersion;
    private bool savingRecordedHotkey;
    private bool updatingPreviewDock;
    private System.Windows.Controls.Primitives.ScrollBar? overlayVerticalScrollBar;
    private double? previewAnchorOffset;
    private FrameworkElement? lastOverlayFocus;
    private double? savedOverlayOffset;
    private bool restoringOverlayWorkspace;
    private DispatcherOperation? overlayRestoreOperation;
    private DispatcherOperation? previewFocusRestoreOperation;
    private DispatcherOperation? updateNoticePresentation;
    private StartupUpdateDialog? updateDialog;
    private bool updateDialogShown;
    private FrameworkElement? trayFocus;

    internal void RememberTrayFocus() => trayFocus = Keyboard.FocusedElement as FrameworkElement;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (DataContext is DesktopTrackerViewModel { IsUpdateNoticeVisible: true }) QueueUpdateNotice();
    }

    private void QueueUpdateNotice()
    {
        if (copyFeedbackClosed) return;
        updateNoticePresentation?.Abort();
        // Dispatch outside the update operation/lock, after initial rendering and startup.
        updateNoticePresentation = Dispatcher.BeginInvoke(PresentUpdateNotice, DispatcherPriority.Background);
    }

    private void PresentUpdateNotice()
    {
        updateNoticePresentation = null;
        if (copyFeedbackClosed) return;
        if (DataContext is not DesktopTrackerViewModel { IsUpdateNoticeVisible: true } vm)
        {
            updateDialog?.Close();
            return;
        }
        if (!IsVisible || updateDialogShown) return;
        updateDialogShown = true;
        var previousFocus = Keyboard.FocusedElement as FrameworkElement;
        updateDialog = new StartupUpdateDialog(vm.UpdateNoticeText, vm.OpenUpdateProductPage) { Owner = this, Resources = Resources };
        try { updateDialog.ShowDialog(); }
        finally
        {
            updateDialog = null;
            vm.DismissUpdateNotice();
            if (!copyFeedbackClosed && IsVisible && IsEnabled)
            {
                if (previousFocus is not { IsVisible: true, IsEnabled: true } || !previousFocus.Focus()) WorkspaceTabs.Focus();
            }
        }
    }

    public MainWindow()
    {
        chooseSaveDirectory = ChooseSaveDirectory;
        scheduleCopyExpiry = ScheduleCopyExpiry;
        InitializeComponent();
        LocalAppearancePreview.ScrollTarget = OverlayConfigurationScrollViewer;
        LocalAppearancePreview.SetBinding(DataContextProperty, new System.Windows.Data.Binding(nameof(DataContext)) { Source = this });
        OverlayConfigurationScrollViewer.ScrollChanged += (_, _) => UpdateOverlayScrollBar();
        LayoutUpdated += UpdatePreviewDock;
        DockedPreviewCard.MouseWheel += (_, e) =>
        {
            OverlayConfigurationScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            { RoutedEvent = MouseWheelEvent });
            e.Handled = true;
        };
        AppearanceFields.RequestBringIntoView += AppearanceFieldBringIntoView;
        OverlayWorkspaceLayout.IsVisibleChanged += (_, _) =>
        {
            if (!OverlayWorkspaceLayout.IsVisible)
            {
                overlayRestoreOperation?.Abort();
                overlayRestoreOperation = null;
                if (!restoringOverlayWorkspace) savedOverlayOffset = OverlayConfigurationScrollViewer.VerticalOffset;
                restoringOverlayWorkspace = false;
            }
            else if (savedOverlayOffset.HasValue && hotkeyRecordingOrigin is null)
            {
                // TabItem finishes its own focus navigation before this one
                // restoration. Do not redirect a nested focus request.
                restoringOverlayWorkspace = true;
                overlayRestoreOperation = Dispatcher.BeginInvoke(RestoreOverlayWorkspace, DispatcherPriority.Loaded);
            }
        };
        OverlayWorkspaceLayout.GotKeyboardFocus += (_, e) =>
        {
            if (!restoringOverlayWorkspace) lastOverlayFocus = e.NewFocus as FrameworkElement;
        };
        OverlayConfigurationScrollViewer.RequestBringIntoView += (_, e) =>
        {
            if (restoringOverlayWorkspace) e.Handled = true;
        };
        directoryFeedback = new FloatingFeedback(FloatingFeedbackLayer, CopyFeedbackOverlay);
        hostedFeedback = new FloatingFeedback(FloatingFeedbackLayer, HostedCopyFeedbackOverlay);
        hotkeyFeedback = new FloatingFeedback(FloatingFeedbackLayer, HotkeySuccessOverlay);
        AppearanceApplyStatus.GotKeyboardFocus += (_, _) =>
        {
            var tooltip = (System.Windows.Controls.ToolTip)AppearanceApplyStatus.ToolTip;
            tooltip.PlacementTarget = AppearanceApplyStatus;
            tooltip.IsOpen = AppearanceApplyStatus.Text.Length > 0;
        };
        AppearanceApplyStatus.LostKeyboardFocus += (_, _) => ((System.Windows.Controls.ToolTip)AppearanceApplyStatus.ToolTip).IsOpen = false;
        LayoutUpdated += (_, _) => { directoryFeedback.Update(); hostedFeedback.Update(); hotkeyFeedback.Update(); };
        WorkspaceTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, WorkspaceTabs)) return;
            if (OverlayWorkspaceTab.IsSelected && DataContext is DesktopTrackerViewModel { HostedOverlay: { } connection })
                _ = connection.EnsureProvisionedAsync();
            ClearCopyFeedback();
            if (hotkeyRecordingOrigin is null) ClearHotkeyFeedback();
        };
    }

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory) : this() =>
        this.chooseSaveDirectory = chooseSaveDirectory;

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory, Action<string> copyDirectoryPath) : this(chooseSaveDirectory) =>
        this.copyDirectoryPath = copyDirectoryPath;

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory, Action<string> copyDirectoryPath,
        Func<TimeSpan, Action, Action> scheduleCopyExpiry) : this(chooseSaveDirectory, copyDirectoryPath) =>
        this.scheduleCopyExpiry = scheduleCopyExpiry;

    private void Window_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        updateNoticePresentation?.Abort();
        updateDialog?.Close();
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
            var game when game == GameId.HollowKnight => vm?.HollowKnightGameDirectory,
            _ => null
        });

    private void DirectoryCopyContext_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopTrackerViewModel.IsUpdateNoticeVisible)) QueueUpdateNotice();
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
            or nameof(DesktopTrackerViewModel.HollowKnightGameDirectory)
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
        LayoutUpdated -= UpdatePreviewDock;
        LocalAppearancePreview.Dispose();
        copyFeedbackClosed = true;
        updateNoticePresentation?.Abort();
        updateDialog?.Close();
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
        if (DataContext is not DesktopTrackerViewModel viewModel) return;
        bool hadFocus = ReferenceEquals(Keyboard.FocusedElement, sender);
        await viewModel.DecrementManualDeathsAsync();
        if (hadFocus && !viewModel.CanDecrementManualDeaths && viewModel.IsManualGameSelected &&
            ReferenceEquals(DataContext, viewModel))
            Keyboard.Focus(IncrementManualDeathsButton);
    }

}
