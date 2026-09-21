using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class CopyErrorReadabilityTests
{
    [Theory]
    [InlineData("er", 560d, 400d)]
    [InlineData("wk", 560d, 400d)]
    [InlineData("lp", 560d, 400d)]
    [InlineData("er", 560d, 760d)]
    [InlineData("wk", 560d, 760d)]
    [InlineData("lp", 560d, 760d)]
    [InlineData("er", 1060d, 760d)]
    [InlineData("wk", 1060d, 760d)]
    [InlineData("lp", 1060d, 760d)]
    public Task PersistentCopyErrorLeavesWholeTotalReachable(string game, double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "synthetic-long-directory-name-for-streaming-account");
        CreateSave(game, directory);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync(); await Choose(vm, game, directory);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new(Game(game), 12345,
            DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(repository.State))));
        bool fail = true;
        var window = new MainWindow((_, _) => null, _ => { if (fail) throw new IOException("Private synthetic detail"); })
        { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var scroll = (ScrollViewer)window.FindName("MainContentScrollViewer");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            var overlay = (Border)window.FindName("CopyFeedbackOverlay");
            var feedback = (TextBlock)window.FindName("DirectoryCopyStatus");
            var copy = Tree(window).OfType<Button>().Single(x => x.IsVisible && AutomationProperties.GetName(x) == "Copy directory path");
            var tabs = (TabControl)window.FindName("WorkspaceTabs");
            tabs.Focus(); await Idle();
            var focus = Keyboard.FocusedElement;
            scroll.ScrollToEnd(); await Idle();
            Assert.Equal("12345", total.Text);
            Assert.Equal(42, total.FontSize);
            double ordinaryExtent = scroll.ExtentHeight;
            RenderTotal(window, total, overlay, $"{game}-{width}x{height}-before");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Same(focus, Keyboard.FocusedElement);
            Assert.False(overlay.IsHitTestVisible);
            Assert.Same(copy.CommandParameter, overlay.Parent);
            Assert.InRange(Bounds(overlay, window).Top - Bounds(copy, window).Bottom, 0, 28);
            Assert.InRange(Math.Abs(Bounds(overlay, window).Right - Bounds(copy, window).Right), 0, 16);
            await Task.Delay(TimeSpan.FromSeconds(5.2)); await Idle();
            Assert.Equal("The directory path could not be copied. Try again.", feedback.Text);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            overlay.BringIntoView(); await Idle();
            RenderAnchor(window, copy, overlay, $"{game}-{width}x{height}-error-anchor");
            scroll.ScrollToTop(); await Idle();
            Assert.Equal(0, scroll.VerticalOffset);
            scroll.ScrollToEnd(); await Idle();
            RenderTotal(window, total, overlay, $"{game}-{width}x{height}-persistent-error");
            int clearPositions = 0;
            for (double offset = 0; offset <= Math.Ceiling(scroll.ScrollableHeight); offset++)
            {
                scroll.ScrollToVerticalOffset(offset); await Idle();
                Rect bounds = Bounds(total, window);
                if (Bounds(viewport, window).Contains(bounds) && !Bounds(overlay, window).IntersectsWith(bounds)) clearPositions++;
            }
            Assert.True(clearPositions > 0, $"No unobscured total position for {game} at {width}x{height}; total {Bounds(total, window)}; overlay {Bounds(overlay, window)}; extent {scroll.ExtentHeight}.");
            Assert.Equal("12345", total.Text);
            Assert.False(((Border)window.FindName("HostedCopyFeedbackOverlay")).IsVisible);
            Assert.Same(focus, Keyboard.FocusedElement);
            copy.BringIntoView(); await Idle();
            Assert.True(copy.Focus()); await Idle();
            Assert.True(copy.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))); await Idle();
            Assert.NotSame(copy, Keyboard.FocusedElement);
            Assert.Equal("The directory path could not be copied. Try again.", feedback.Text);
            fail = false;
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal(ordinaryExtent, scroll.ExtentHeight);
            Assert.Equal("Directory path copied", feedback.Text);
            overlay.BringIntoView(); await Idle();
            RenderAnchor(window, copy, overlay, $"{game}-{width}x{height}-success-anchor");
            var point = total.TranslatePoint(new Point(), window);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal(ordinaryExtent, scroll.ExtentHeight);
            Assert.Equal(point, total.TranslatePoint(new Point(), window));
            double originalWidth = window.Width;
            window.Width = originalWidth == 560 ? 1060 : 560; await Idle();
            Assert.InRange(Bounds(overlay, window).Top - Bounds(copy, window).Bottom, 0, 28);
            Assert.InRange(Math.Abs(Bounds(overlay, window).Right - Bounds(copy, window).Right), 0, 16);
            Assert.False(Bounds(overlay, window).IntersectsWith(Bounds(total, window)));
            window.Width = originalWidth; await Idle();
            fail = true;
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal(ordinaryExtent, scroll.ExtentHeight);
            window.DataContext = null; window.DataContext = vm; await Idle();
            Assert.Equal(ordinaryExtent, scroll.ExtentHeight);
            Assert.Empty(feedback.Text);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            window.Close();
            Assert.Empty(feedback.Text);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        }
        finally { window.Close(); }
    });

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void RenderAnchor(MainWindow window, Button copy, Border feedback, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("SOULSTRACKER_COPY_ERROR_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        // Keep local fixture paths outside the crop. Geometry records relate
        // the unmodified feedback render to the separate Copy control crop.
        Rect anchor = Bounds(copy, content);
        Rect message = Bounds(feedback, content);
        File.WriteAllText(Path.Combine(directory, name + ".json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            copy = anchor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            feedback = message.ToString(System.Globalization.CultureInfo.InvariantCulture),
            width = window.Width, height = window.Height,
        }));
        SaveCrop(anchor, "-copy");
        SaveCrop(message, "-feedback");
        void SaveCrop(Rect bounds, string suffix)
        {
            bounds.Intersect(new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            Assert.False(bounds.IsEmpty);
            int x = (int)Math.Ceiling(bounds.Left), y = (int)Math.Ceiling(bounds.Top);
            int w = (int)Math.Floor(bounds.Right) - x, h = (int)Math.Floor(bounds.Bottom) - y;
            Assert.True(w > 0 && h > 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, new Int32Rect(x, y, w, h))));
            using var output = File.Create(Path.Combine(directory, name + suffix + ".png")); encoder.Save(output);
        }
    }

    private static void RenderTotal(MainWindow window, TextBlock total, Border overlay, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("SOULSTRACKER_COPY_ERROR_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        // Capture only the counter/feedback area; synthetic fixture paths stay out of images.
        int top = Math.Max(0, (int)Math.Floor(Bounds(total, content).Top));
        int bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(Math.Max(Bounds(total, content).Bottom, Bounds(overlay, content).Bottom)));
        if (bottom <= top) return;
        var crop = new CroppedBitmap(bitmap, new Int32Rect(0, top, bitmap.PixelWidth, bottom - top));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
}
