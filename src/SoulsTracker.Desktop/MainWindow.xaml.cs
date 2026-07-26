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
    private bool deathSoundVolumeEditorIsActive;
    private bool isRestoringEldenRingProfileSelection;
    private bool isRestoringEldenRingSaveSelection;
    private bool isRestoringBlackMythWukongSaveSelection;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshPreviews();
    }
    private DesktopTrackerViewModel? previewViewModel;

    private void Window_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (previewViewModel is not null) previewViewModel.PropertyChanged -= PreviewViewModel_PropertyChanged;
        previewViewModel = e.NewValue as DesktopTrackerViewModel;
        if (previewViewModel is not null) previewViewModel.PropertyChanged += PreviewViewModel_PropertyChanged;
        GameSelector.SelectedItem = previewViewModel?.SelectedGame;
        RefreshPreviews();
    }

    private void PreviewViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesktopTrackerViewModel.TotalDeathsPreviewUri) or nameof(DesktopTrackerViewModel.BossListPreviewUri)) RefreshPreviews();
        if (e.PropertyName == nameof(DesktopTrackerViewModel.IsEldenRingNoticeVisible))
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (previewViewModel?.IsEldenRingNoticeVisible == true) FocusEldenRingNoticePrimaryAction();
                else RestoreGameSelectorFocus();
            });
        }
    }

    private void RefreshPreviews()
    {
        if (previewViewModel is null || !IsLoaded) return;
        LiveOverlayPreview.Source = OverlayTypeTabs.SelectedIndex == 1
            ? previewViewModel.BossListPreviewUri
            : previewViewModel.TotalDeathsPreviewUri;
    }

    private void OverlayTypeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == OverlayTypeTabs) RefreshPreviews();
    }

    internal void DisposeLiveOverlayPreview()
    {
        if (previewViewModel is not null)
        {
            previewViewModel.PropertyChanged -= PreviewViewModel_PropertyChanged;
            previewViewModel = null;
        }

        try
        {
            LiveOverlayPreview.Source = null;
            LiveOverlayPreview.Dispose();
        }
        catch
        {
            // Window shutdown must continue even when the embedded browser has already stopped.
        }
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

    private async void DecrementDeaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.DecrementManualDeathsAsync();
    }

    private async void BrowseEldenRingSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Elden Ring save (ER0000.sl2)|ER0000.sl2", CheckFileExists = true, Multiselect = false, Title = "Choose ER0000.sl2" };
        if (dialog.ShowDialog(this) == true && DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.SetEldenRingSaveFileAsync(dialog.FileName);
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
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Black Myth: Wukong save (ArchiveSaveFile.*.sav)|ArchiveSaveFile.*.sav", CheckFileExists = true, Multiselect = false, Title = "Choose Black Myth: Wukong save" };
        if (dialog.ShowDialog(this) == true && DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.SetBlackMythWukongSaveFileAsync(dialog.FileName);
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

    private async void BossListScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && sender is System.Windows.Controls.ComboBox { SelectedItem: BossListScopeChoice scope } && viewModel.SelectedBossListScope != scope)
        {
            await viewModel.SetBossListScopeAsync(scope);
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

    private async void BossCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && sender is System.Windows.Controls.CheckBox { DataContext: BossChoice boss, IsChecked: bool isDefeated })
        {
            await viewModel.SetBossDefeatedAsync(boss, isDefeated);
        }
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

    private async void BossListOverlayEnabled_Checked(object sender, RoutedEventArgs e) =>
        await SetBossListOverlayEnabledAsync(isEnabled: true);

    private async void BossListOverlayEnabled_Unchecked(object sender, RoutedEventArgs e) =>
        await SetBossListOverlayEnabledAsync(isEnabled: false);

    private async Task SetBossListOverlayEnabledAsync(bool isEnabled)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && viewModel.IsBossListOverlayEnabled != isEnabled)
        {
            await viewModel.SetBossListOverlayEnabledAsync(isEnabled);
        }
    }

    private async void BossListVisibilityModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel &&
            BossListVisibilityModeSelector.SelectedItem is SoulsTracker.Domain.BossListVisibilityMode mode &&
            viewModel.BossListVisibilityMode != mode)
        {
            await viewModel.SetBossListVisibilityModeAsync(mode);
        }
    }

    private async void ApplyTotalDeathsAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);
    }

    private async void ApplyBossListAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);
    }

    private async void ResetSelectedOverlayAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.ResetOverlayAppearanceAsync(totalDeaths: OverlayTypeTabs.SelectedIndex != 1);
        }
    }


    private async void BrowseDeathSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Audio files (*.wav;*.mp3)|*.wav;*.mp3", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true && DataContext is DesktopTrackerViewModel viewModel) await viewModel.SetDeathSoundFileAsync(dialog.FileName);
    }
    private async void ClearDeathSound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ClearDeathSoundAsync();
    }
    private void PlayDeathSound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.PreviewDeathSound();
    }
    private async void DeathSoundEnabled_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && !viewModel.IsDeathSoundEnabled) await viewModel.SetDeathSoundEnabledAsync(true);
    }
    private async void DeathSoundEnabled_Unchecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel && viewModel.IsDeathSoundEnabled) await viewModel.SetDeathSoundEnabledAsync(false);
    }
    private async void DeathSoundVolume_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Return || sender is not System.Windows.Controls.TextBox textBox || DataContext is not DesktopTrackerViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        await viewModel.CommitDeathSoundVolumeTextAsync();
        await SynchronizeDeathSoundVolumeStatusAsync(viewModel);
    }

    private async void DeathSoundVolume_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (deathSoundVolumeEditorIsActive && sender is System.Windows.Controls.TextBox && DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.QueueDeathSoundVolumeTextSaveAsync();
            await SynchronizeDeathSoundVolumeStatusAsync(viewModel);
        }
    }

    private void DeathSoundVolume_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        deathSoundVolumeEditorIsActive = true;

    private async void DeathSoundVolume_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        deathSoundVolumeEditorIsActive = false;
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.CommitDeathSoundVolumeTextAsync();
            await SynchronizeDeathSoundVolumeStatusAsync(viewModel);
        }
    }

    // The coordinator intentionally completes on a worker thread, and its task uses
    // RunContinuationsAsynchronously. A routed async handler therefore cannot assume
    // that its continuation still owns this Window's dispatcher. Marshal the
    // already-authoritative VM result back to the visible live-region after the await.
    private async Task SynchronizeDeathSoundVolumeStatusAsync(DesktopTrackerViewModel viewModel)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            DeathSoundStatusTextBlock.Text = viewModel.DeathSoundStatus;
            DeathSoundStatusTextBlock.Foreground = viewModel.IsDeathSoundVolumeUpdateSuccessful
                ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
                : viewModel.IsDeathSoundVolumeValidationError
                    ? (System.Windows.Media.Brush)FindResource("DangerBrush")
                    : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
            System.Windows.Automation.AutomationProperties.SetName(DeathSoundStatusTextBlock, viewModel.DeathSoundStatus ?? string.Empty);
            DeathSoundStatusTextBlock.UpdateLayout();
        });
    }

    private void CopyTotalDeathsOverlayUrl_Click(object sender, RoutedEventArgs e) => CopyOverlayUrl(sender as System.Windows.Controls.Button, (DataContext as DesktopTrackerViewModel)?.TotalDeathsSceneUrl);
    private void CopyBossListOverlayUrl_Click(object sender, RoutedEventArgs e) => CopyOverlayUrl(sender as System.Windows.Controls.Button, (DataContext as DesktopTrackerViewModel)?.BossListSceneUrl);
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
    private async void ChooseBossExport_Click(object sender, RoutedEventArgs e) { var dialog = CreateTextExportDialog(); if (dialog.ShowDialog(this) == true && DataContext is DesktopTrackerViewModel viewModel) await viewModel.SetBossExportPathAsync(dialog.FileName); }
    private async void ClearDeathsExport_Click(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ClearDeathsExportAsync(); }
    private async void ClearBossExport_Click(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.ClearBossExportAsync(); }
    // These controls intentionally use one-way bindings: committed state remains
    // the source of truth after asynchronous persistence completes. Do not gate a
    // routed toggle event on the currently committed value, though. A second user
    // interaction can arrive before the first save returns, and stale state would
    // otherwise drop that interaction (and make the checkbox visibly flicker).
    private async void DeathsExportEnabled_Checked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetDeathsExportEnabledAsync(true); }
    private async void DeathsExportEnabled_Unchecked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetDeathsExportEnabledAsync(false); }
    private async void BossExportEnabled_Checked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetBossExportEnabledAsync(true); }
    private async void BossExportEnabled_Unchecked(object sender, RoutedEventArgs e) { if (DataContext is DesktopTrackerViewModel vm) await vm.SetBossExportEnabledAsync(false); }

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
