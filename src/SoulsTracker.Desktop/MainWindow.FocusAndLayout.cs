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

    internal void RestoreTrayFocus()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || !IsActive) return;
            if (trayFocus is not { IsVisible: true, IsEnabled: true, Focusable: true } target || !target.Focus())
                WorkspaceTabs.Focus();
        }, DispatcherPriority.Input);
    }

    private void RestoreOverlayWorkspace()
    {
        overlayRestoreOperation = null;
        var scroll = OverlayConfigurationScrollViewer;
        try
        {
            if (!OverlayWorkspaceLayout.IsVisible || LocalAppearancePreview.IsDisposed || hotkeyRecordingOrigin is not null) return;
            scroll.UpdateLayout();
            var presenter = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            presenter.SetVerticalOffset(savedOverlayOffset ?? 0);
            scroll.UpdateLayout();
            // Reuse the normal geometry transaction for any resize while away.
            restoringOverlayWorkspace = false;
            UpdatePreviewDock(null, EventArgs.Empty);
            scroll.UpdateLayout();
            restoringOverlayWorkspace = true;
            var previous = lastOverlayFocus;
            bool visible = previous is { IsVisible: true, IsEnabled: true, Focusable: true } &&
                OverlayWorkspaceLayout.IsAncestorOf(previous);
            if (visible && presenter.IsAncestorOf(previous!))
            {
                var bounds = previous!.TransformToAncestor(presenter).TransformBounds(new Rect(previous.RenderSize));
                visible = bounds.Width > 0 && bounds.Height > 0 &&
                    new Rect(presenter.RenderSize).Contains(bounds);
            }
            if (!visible || !previous!.Focus()) AppearanceActions.Focus();
        }
        finally
        {
            restoringOverlayWorkspace = false;
            lastOverlayFocus = Keyboard.FocusedElement as FrameworkElement;
        }
    }

    internal void ConfigureAppearancePreview(string dataRoot)
    {
        LocalAppearancePreview.IsVisibleChanged += async (_, _) =>
        {
            if (LocalAppearancePreview.IsVisible) await LocalAppearancePreview.StartBrowserAsync(dataRoot);
        };
    }

    private void AppearanceFieldBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (restoringOverlayWorkspace || e.TargetObject is not FrameworkElement field) return;
        bool wasDocked = ReferenceEquals(LocalAppearancePreview.Parent, DockedPreviewSlot);
        Dispatcher.BeginInvoke(() =>
        {
            // A visibility request may itself cross the docking boundary. Finish
            // its visibility request using the newly reduced editable viewport.
            if (!LocalAppearancePreview.IsDisposed && field.IsVisible &&
                wasDocked != ReferenceEquals(LocalAppearancePreview.Parent, DockedPreviewSlot)) field.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void UpdatePreviewDock(object? sender, EventArgs e)
    {
        if (restoringOverlayWorkspace || updatingPreviewDock || LocalAppearancePreview.IsDisposed || !OverlayWorkspaceLayout.IsVisible || NormalPreviewSlot.ActualWidth <= 0) return;
        var scroll = OverlayConfigurationScrollViewer;
        if (scroll.Content is not FrameworkElement content) return;
        var presenter = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        UpdateOverlayScrollBar();
        if (previewAnchorOffset is double requestedOffset)
        {
            // Inset and offset belong to one arrange transaction. Do not make
            // another threshold decision using only its partially applied inset.
            double arrangedOffset = Math.Clamp(requestedOffset, 0, Math.Max(0, presenter.ExtentHeight - presenter.ViewportHeight));
            if (!presenter.IsArrangeValid || Math.Abs(presenter.VerticalOffset - arrangedOffset) > 0.01) return;
            previewAnchorOffset = null;
        }
        updatingPreviewDock = true;
        try
        {
            bool docked = ReferenceEquals(LocalAppearancePreview.Parent, DockedPreviewSlot);
            if (docked) NormalPreviewSlot.Height = 180;
            // Compare in normal-page coordinates, excluding the offset used to
            // anchor the content when the compact card insets the presenter.
            double bottom = NormalPreviewSection.TranslatePoint(new System.Windows.Point(0, NormalPreviewSection.ActualHeight), content).Y;
            double inset = presenter.Margin.Top - scroll.Padding.Top;
            bool dock = presenter.VerticalOffset - inset > bottom;
            double barWidth = scroll.ActualWidth - NormalPreviewSection.ActualWidth - 48;
            DockedPreviewCard.Margin = new Thickness(24, 0, 24 + Math.Max(0, barWidth), 0);
            if (docked)
            {
                LocalAppearancePreview.RowDefinitions[0].Height = new GridLength(scroll.ActualHeight < 300 ? 72 : 96);
                scroll.UpdateLayout();
                SetPreviewViewport(new Thickness(24, DockedPreviewCard.ActualHeight + 4, 24, DockedPreviewBottomInset));
            }
            if (dock == docked) return;

            // Reparenting the native preview can transiently clear keyboard focus.
            // Preserve an existing, usable focus target across this layout-only move.
            var focusedBeforeMove = Keyboard.FocusedElement as FrameworkElement;

            if (dock)
            {
                NormalPreviewSlot.Height = LocalAppearancePreview.ActualHeight;
                NormalPreviewSlot.Children.Remove(LocalAppearancePreview);
                LocalAppearancePreview.RowDefinitions[0].Height = new GridLength(scroll.ActualHeight < 300 ? 72 : 96);
                DockedPreviewSlot.Children.Add(LocalAppearancePreview);
                DockedPreviewCard.Visibility = Visibility.Visible;
                scroll.UpdateLayout();
                SetPreviewViewport(new Thickness(24, DockedPreviewCard.ActualHeight + 4, 24, DockedPreviewBottomInset));
            }
            else
            {
                DockedPreviewSlot.Children.Remove(LocalAppearancePreview);
                LocalAppearancePreview.RowDefinitions[0].Height = new GridLength(180);
                NormalPreviewSlot.Children.Add(LocalAppearancePreview);
                NormalPreviewSlot.Height = double.NaN;
                DockedPreviewCard.Visibility = Visibility.Collapsed;
                SetPreviewViewport(new Thickness(24, 24, 24, NormalPreviewBottomInset));
            }
            if (Keyboard.FocusedElement is null && focusedBeforeMove is { IsVisible: true, IsEnabled: true, Focusable: true })
                focusedBeforeMove.Focus();
            previewFocusRestoreOperation?.Abort();
            previewFocusRestoreOperation = focusedBeforeMove is null ? null : Dispatcher.BeginInvoke(() =>
            {
                previewFocusRestoreOperation = null;
                if (Keyboard.FocusedElement is null && focusedBeforeMove is { IsVisible: true, IsEnabled: true, Focusable: true })
                    focusedBeforeMove.Focus();
            }, DispatcherPriority.Loaded);
        }
        finally { updatingPreviewDock = false; UpdateOverlayScrollBar(); }
    }

    private void UpdateOverlayScrollBar()
    {
        var scroll = OverlayConfigurationScrollViewer;
        if (!scroll.IsLoaded || previewAnchorOffset.HasValue) return;
        overlayVerticalScrollBar ??= scroll.Template.FindName("OverlayVerticalScrollBar", scroll) as System.Windows.Controls.Primitives.ScrollBar;
        if (overlayVerticalScrollBar is null) return;
        var presenter = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;
        if (presenter is null) return;
        double inset = presenter.Margin.Top - scroll.Padding.Top;
        // The compact Preview reduces the editable viewport. Keep the thumb in
        // page coordinates so docking cannot jump it during a drag or wheel step.
        overlayVerticalScrollBar.ViewportSize = scroll.ViewportHeight + inset + presenter.Margin.Bottom - NormalPreviewBottomInset;
        overlayVerticalScrollBar.LargeChange = Math.Max(1, scroll.ViewportHeight);
        // The docked editor has less trailing margin. Its final
        // reachable page coordinate is correspondingly lower.
        overlayVerticalScrollBar.Maximum = Math.Max(0, scroll.ExtentHeight - overlayVerticalScrollBar.ViewportSize -
            (NormalPreviewBottomInset - DockedPreviewBottomInset));
        overlayVerticalScrollBar.Value = Math.Clamp(presenter.VerticalOffset - inset, 0, overlayVerticalScrollBar.Maximum);
    }

    private void OverlayVerticalScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        var scroll = OverlayConfigurationScrollViewer;
        var presenter = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        presenter.SetVerticalOffset(e.NewValue + presenter.Margin.Top - scroll.Padding.Top);
    }

    private void SetPreviewViewport(Thickness margin)
    {
        var scroll = OverlayConfigurationScrollViewer;
        var presenter = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        if (presenter.Margin == margin) return;
        double displacement = margin.Top - presenter.Margin.Top;
        double offset = presenter.VerticalOffset + displacement;
        previewAnchorOffset = offset;
        scroll.Tag = margin;
        presenter.SetVerticalOffset(offset);
    }

    private void ContextHelp_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ToolTip: System.Windows.Controls.ToolTip help } button)
        {
            help.PlacementTarget = button;
            help.IsOpen = !restoringOverlayWorkspace;
        }
    }

    private void ContextHelp_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ToolTip: System.Windows.Controls.ToolTip help }) help.IsOpen = false;
    }
}
