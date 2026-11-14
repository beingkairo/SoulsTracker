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
        try
        {
            if (copyFeedbackClosed || origin is null) return;
            // Captured-origin restoration owns focus through tab selection and
            // layout. A queued generic return must not replace its target.
            overlayRestoreOperation?.Abort();
            overlayRestoreOperation = null;
            restoringOverlayWorkspace = false;
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
        finally { hotkeyRecordingOrigin = null; }
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
        bool applied = await viewModel.ApplyGlobalHotkeysAsync();
        if (copyFeedbackClosed || !ReferenceEquals(DataContext, viewModel) || version != hotkeyOperationVersion) return;
        if (applied) ShowHotkeyFeedback(ApplyHotkeysButton);
        else if (SettingsWorkspaceTab.IsSelected && !string.IsNullOrWhiteSpace(viewModel.GlobalHotkeyStatus))
        {
            // Realize the wrapped explanation before asking the existing scroll
            // owner to reveal it. This does not move keyboard focus.
            SettingsContentScrollViewer.UpdateLayout();
            if (GlobalHotkeyStatusTextBlock.IsVisible) GlobalHotkeyStatusTextBlock.BringIntoView();
        }
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
}
