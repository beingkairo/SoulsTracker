using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Size = System.Windows.Size;

namespace SoulsTracker.Desktop;

/// <summary>Positions one non-interactive message without participating in content measurement.</summary>
internal sealed class FloatingFeedback(Canvas layer, Border surface)
{
    private FrameworkElement? anchor;
    private FrameworkElement? viewport;

    internal void Show(FrameworkElement target, ScrollViewer scroll)
    {
        anchor = target;
        viewport = FindViewport(scroll);
        Update();
    }

    internal void Hide()
    {
        anchor = null;
        viewport = null;
        surface.Visibility = Visibility.Collapsed;
    }

    internal void Update()
    {
        if (anchor is null || viewport is null) return;
        if (!anchor.IsVisible || !viewport.IsVisible || layer.ActualWidth <= 0 || layer.ActualHeight <= 0)
        {
            surface.Visibility = Visibility.Collapsed;
            return;
        }

        Rect available = viewport.TransformToVisual(layer).TransformBounds(new Rect(viewport.RenderSize));
        available.Intersect(new Rect(layer.RenderSize));
        Rect target = anchor.TransformToVisual(layer).TransformBounds(new Rect(anchor.RenderSize));
        if (available.IsEmpty || !available.IntersectsWith(target))
        {
            surface.Visibility = Visibility.Collapsed;
            return;
        }

        surface.MaxWidth = available.Width;
        surface.Visibility = Visibility.Visible;
        surface.Measure(new Size(available.Width, double.PositiveInfinity));
        Size size = surface.DesiredSize;
        double left = Math.Clamp(target.Right - size.Width, available.Left, Math.Max(available.Left, available.Right - size.Width));
        double top = target.Bottom + 4;
        if (top + size.Height > available.Bottom) top = target.Top - size.Height - 4;
        top = Math.Clamp(top, available.Top, Math.Max(available.Top, available.Bottom - size.Height));
        Canvas.SetLeft(surface, left);
        Canvas.SetTop(surface, top);
    }

    private static FrameworkElement? FindViewport(DependencyObject root)
    {
        if (root is ScrollContentPresenter viewport) return viewport;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindViewport(VisualTreeHelper.GetChild(root, i)) is { } result) return result;
        return null;
    }
}
