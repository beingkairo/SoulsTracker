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

    public MainWindow()
    {
        chooseSaveDirectory = ChooseSaveDirectory;
        InitializeComponent();
    }

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory) : this() =>
        this.chooseSaveDirectory = chooseSaveDirectory;

    internal MainWindow(Func<string, string?, string?> chooseSaveDirectory, Action<string> copyDirectoryPath) : this(chooseSaveDirectory) =>
        this.copyDirectoryPath = copyDirectoryPath;

    private void CopyDirectoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            copyDirectoryPath(path);
            DirectoryCopyStatus.Text = "Directory path copied.";
        }
        catch { DirectoryCopyStatus.Text = "The directory path could not be copied. Try again."; }
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
        if (directoryCopyViewModel is not null) directoryCopyViewModel.PropertyChanged -= DirectoryCopyContext_PropertyChanged;
        directoryCopyViewModel = e.NewValue as DesktopTrackerViewModel;
        directoryCopyContext = GetDirectoryCopyContext(directoryCopyViewModel);
        DirectoryCopyStatus.Text = string.Empty;
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
            directoryCopyContext = context;
            DirectoryCopyStatus.Text = string.Empty;
        }
        if (Dispatcher.CheckAccess()) ApplyContext();
        else _ = Dispatcher.BeginInvoke(ApplyContext);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (directoryCopyViewModel is not null) directoryCopyViewModel.PropertyChanged -= DirectoryCopyContext_PropertyChanged;
        directoryCopyViewModel = null;
        base.OnClosed(e);
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
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.BeginEldenRingChange();
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
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.BeginBlackMythWukongChange();
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
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.BeginLiesOfPChange();
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
        if (DataContext is not DesktopTrackerViewModel viewModel) return;

        viewModel.BeginHotkeyRecording(increment);
        if (viewModel.IsHotkeyRecording)
        {
            Dispatcher.BeginInvoke(() => Keyboard.Focus(HotkeyRecordingOverlay));
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
            viewModel.CancelHotkeyRecording();
            e.Handled = true;
            ReturnToMainAfterHotkeyRecording();
            return;
        }

        if (key == Key.Enter)
        {
            e.Handled = true;
            await viewModel.SaveRecordedHotkeyAsync();
            ReturnToMainAfterHotkeyRecording();
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
        FocusManager.SetFocusedElement(EldenRingNoticeOverlay, EldenRingNoticeConfirmButton);
        Keyboard.Focus(EldenRingNoticeConfirmButton);
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

    private void ReturnToMainAfterHotkeyRecording()
    {
        WorkspaceTabs.SelectedItem = MainWorkspaceTab;
        Keyboard.Focus(WorkspaceTabs);
    }

    private async void ApplyHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ApplyGlobalHotkeysAsync();
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
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);
    }

    private async void ResetSelectedOverlayAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.ResetOverlayAppearanceAsync(totalDeaths: true);
        }
    }


    private void CopyTotalDeathsOverlayUrl_Click(object sender, RoutedEventArgs e) =>
        (DataContext as DesktopTrackerViewModel)?.HostedOverlay?.CopyReadUrl(System.Windows.Clipboard.SetText);

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
