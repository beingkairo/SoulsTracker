using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedConnectionTests;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class HostedConnectionPresentationTests
{
    private const string Origin = "https://overlay.beingkairo.com";
    private static readonly string OverlayId = "a1" + new string('0', 62);

    [Fact]
    public async Task CopyFeedbackFloatsBelowItsButtonWithoutAffectingLayout() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => new HostedOverlayPublisher(config, new Server()));
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator); vm.ConfigureHostedOverlay(connection);
            var expiries = new List<Expiry>();
            var window = new MainWindow((_, _) => null, _ => { }, (delay, callback) =>
            {
                var expiry = new Expiry(delay, callback);
                expiries.Add(expiry);
                return () => expiry.Cancelled = true;
            })
            { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                await Idle();
                var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
                var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
                double extent = scroll.ExtentHeight;
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle();
                var feedback = (TextBlock)window.FindName("HostedCopyStatus");
                Assert.Equal("URL copied", feedback.Text);
                var overlay = (Border)window.FindName("HostedCopyFeedbackOverlay");
                var layer = (Canvas)window.FindName("FloatingFeedbackLayer");
                Assert.Same(layer, overlay.Parent);
                Assert.Equal(new Size(), layer.DesiredSize);
                Assert.Equal(extent, scroll.ExtentHeight);
                Rect copyBounds = copy.TransformToVisual(layer).TransformBounds(new Rect(copy.RenderSize));
                Rect feedbackBounds = overlay.TransformToVisual(layer).TransformBounds(new Rect(overlay.RenderSize));
                Assert.True(feedbackBounds.Top >= copyBounds.Bottom || feedbackBounds.Bottom <= copyBounds.Top);
                Assert.InRange(Math.Abs(feedbackBounds.Right - copyBounds.Right), 0, 1);
                Assert.Null(window.FindName("HostedCopyFeedbackFooter"));
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, expiries.Count);
                Assert.Equal(TimeSpan.FromSeconds(5), expiries[0].Delay);
                Assert.True(expiries[0].Cancelled);
                Assert.False(expiries[1].Cancelled);
                expiries[0].Callback();
                Assert.Equal("URL copied", feedback.Text);
                expiries[1].Callback();
                Assert.Empty(feedback.Text);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                foreach (var element in Tree(window))
                {
                    string surface = AutomationProperties.GetName(element) + AutomationProperties.GetHelpText(element)
                        + (element is FrameworkElement f ? f.ToolTip?.ToString() : "") + (element is TextBlock t ? t.Text : "");
                    Assert.DoesNotContain(new string('b', 64), surface);
                    Assert.DoesNotContain(new string('c', 64), surface);
                }
                await connection.DisposeAsync(); await Idle();
                Assert.Empty(connection.CopyFeedbackText);
                Assert.False(((Button)window.FindName("CopyTotalDeathsOverlayUrlButton")).IsEnabled);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public async Task MinimalUrlRowFitsViewportAndUsesCopyIconGeometry(double width, double height) => await OnDispatcher(async () =>
    {
        var window = new MainWindow { Width = width, Height = height, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle(); window.UpdateLayout();
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
            Assert.Equal("Copy URL", AutomationProperties.GetName(copy));
            Assert.Equal("Copy URL", copy.ToolTip);
            Assert.Equal(string.Empty, copy.Content);
            var icon = Tree(copy).OfType<System.Windows.Shapes.Path>().Single();
            Assert.Equal(14, icon.Width);
            Assert.Equal(16, icon.Height);
            var url = Tree(window).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Overlay URL");
            var bounds = url.TransformToAncestor(scroll).TransformBounds(new Rect(url.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= scroll.ActualWidth + 1);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public async Task ConfiguredUrlAndCopyStayInOneCompactRow(double width, double height) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => new HostedOverlayPublisher(config, new Server()));
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator); vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                await Idle(); window.UpdateLayout();

                var url = Tree(window).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Overlay URL");
                var ordinary = new TextBox { Style = (Style)window.FindResource(typeof(TextBox)), Text = "Single line" };
                var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
                var row = Assert.IsType<Grid>(url.Parent);
                Assert.Same(row, copy.Parent);
                Assert.Equal(Grid.GetRow(url), Grid.GetRow(copy));
                Assert.Equal(VerticalAlignment.Center, url.VerticalAlignment);
                Assert.Equal(VerticalAlignment.Center, copy.VerticalAlignment);
                Assert.Equal(TextWrapping.NoWrap, url.TextWrapping);

                Rect urlBounds = url.TransformToAncestor(row).TransformBounds(new Rect(url.RenderSize));
                Rect copyBounds = copy.TransformToAncestor(row).TransformBounds(new Rect(copy.RenderSize));
                Assert.True(urlBounds.Top < copyBounds.Bottom && copyBounds.Top < urlBounds.Bottom);
                Assert.InRange(Math.Abs((urlBounds.Top + urlBounds.Height / 2) - (copyBounds.Top + copyBounds.Height / 2)), 0, 1);
                Assert.Equal(url.MinHeight, url.ActualHeight, 1);
                Assert.Equal(ordinary.Padding, url.Padding);
                Assert.Equal(ordinary.BorderThickness, url.BorderThickness);
                Assert.Equal(ordinary.Background, url.Background);
                Assert.IsNotType<Border>(row.Parent);
                Assert.InRange(row.ActualHeight, 32, 34);

                var urlScroll = Tree(url).OfType<ScrollViewer>().Single();
                Assert.True(urlScroll.ExtentWidth > urlScroll.ViewportWidth);
                Assert.True(urlScroll.ExtentHeight <= urlScroll.ViewportHeight + 1);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task LongUrlKeepsHorizontalAccessAndTraversesDirectlyBetweenFieldAndCopy() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => new HostedOverlayPublisher(config, new Server()));
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator); vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
            try
            {
                window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                await Idle(); window.UpdateLayout();

                var url = Tree(window).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Overlay URL");
                var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
                var urlScroll = Tree(url).OfType<ScrollViewer>().Single();
                Assert.Equal(ScrollBarVisibility.Hidden, url.HorizontalScrollBarVisibility);
                Assert.Empty(Tree(url).OfType<ScrollBar>());
                Assert.Empty(Tree(url).OfType<RepeatButton>());
                Assert.DoesNotContain(Tree(url).OfType<Control>(), control =>
                    !ReferenceEquals(control, url)
                    && control.Focusable
                    && string.IsNullOrEmpty(AutomationProperties.GetName(control)));
                Assert.True(url.Text.Length > 100);
                Assert.True(urlScroll.ExtentWidth > urlScroll.ViewportWidth);

                url.SelectAll();
                Assert.Equal(url.Text.Length, url.SelectionLength);
                Assert.Equal(url.Text, url.SelectedText);
                Clipboard.Clear();
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(url.Text, Clipboard.GetText());
                Assert.True(url.Focus());
                url.CaretIndex = url.Text.Length;
                await Idle();
                Assert.True(urlScroll.HorizontalOffset > 0);

                Assert.True(url.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(copy, Keyboard.FocusedElement);

                Assert.True(copy.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
                Assert.Same(url, Keyboard.FocusedElement);
            }
            finally { Clipboard.Clear(); window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task PreparingFailureRetryAndHealthyStatesKeepOnlyContextualControlsVisible() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int requests = 0;
            var handler = new Handler(async (_, _) =>
            {
                int request = Interlocked.Increment(ref requests);
                if (request == 1) { firstEntered.SetResult(); await firstRelease.Task; }
                if (request <= 3) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (request == 4) { retryEntered.SetResult(); await retryRelease.Task; }
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"{{\"v\":1,\"status\":\"provisioned\",\"overlayId\":\"{OverlayId}\"}}", Encoding.UTF8, "application/json") };
            });
            var active = new HostedPublisherConfigurationStore(Path.Combine(root, "active.private"), new CurrentUserDpapiSecretProtector(), [Origin]);
            var pending = new HostedProvisioningStateStore(Path.Combine(root, "pending.private"), new CurrentUserDpapiSecretProtector(), [Origin]);
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active,
                config => new HostedOverlayPublisher(config, new Server()), pending,
                new HostedOverlayProvisioningClient(Origin, handler), (_, _) => Task.CompletedTask);
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator); vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await firstEntered.Task; await Idle();
                var url = Tree(window).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Overlay URL");
                var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
                var retry = Tree(window).OfType<Button>().Single(x => x.Content?.ToString() == "Try again");
                Assert.Equal("Preparing overlay URL...", url.Text);
                Assert.False(copy.IsEnabled);
                Assert.False(retry.IsVisible);

                firstRelease.SetResult();
                await WaitUntil(() => connection.CanRetry);
                Assert.True(retry.IsVisible && retry.IsEnabled);
                Assert.False(copy.IsEnabled);
                Assert.Contains("could not be prepared", connection.StatusText, StringComparison.Ordinal);

                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await retryEntered.Task; await Idle();
                Assert.Equal("Preparing overlay URL...", url.Text);
                Assert.False(retry.IsVisible);
                Assert.False(copy.IsEnabled);

                retryRelease.SetResult();
                await WaitUntil(() => connection.CanCopy);
                Assert.Equal(4, requests);
                Assert.True(copy.IsEnabled);
                Assert.False(retry.IsVisible);
                Assert.Equal(string.Empty, connection.StatusText);
                Assert.Equal(Origin + "/soulstracker/#id=" + OverlayId, url.Text[..url.Text.IndexOf("&read=", StringComparison.Ordinal)]);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) { await Task.Delay(10); await Idle(); }
        Assert.True(condition());
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Expiry(TimeSpan delay, Action callback)
    {
        internal TimeSpan Delay { get; } = delay;
        internal Action Callback { get; } = callback;
        internal bool Cancelled { get; set; }
    }
}
