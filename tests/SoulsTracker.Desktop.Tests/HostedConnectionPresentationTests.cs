using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    [Fact]
    public async Task CopyFeedbackIsTransientCapabilitySafeAndBelowAppearanceActions() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => new HostedOverlayPublisher(config, new Server()));
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator); vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                Assert.True(connection.CopyReadUrl(_ => { }));
                await Idle();
                var feedback = (TextBlock)window.FindName("HostedCopyStatus");
                Assert.Equal("URL copied", feedback.Text);
                var overlay = (Border)window.FindName("HostedCopyFeedbackOverlay");
                var footerContent = Assert.IsType<Grid>(overlay.Parent);
                var footer = Assert.IsType<Border>(footerContent.Parent);
                Assert.Same(window.Content, footer.Parent);
                Assert.Equal(2, Grid.GetRow(footer));
                var actions = (FrameworkElement)window.FindName("AppearanceActions");
                Assert.True(overlay.TranslatePoint(new Point(), window).Y >= actions.TranslatePoint(new Point(0, actions.ActualHeight), window).Y);
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

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
