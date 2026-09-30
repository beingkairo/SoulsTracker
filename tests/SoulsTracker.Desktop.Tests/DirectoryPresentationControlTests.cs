using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class DirectoryPresentationControlTests
{
    [Theory]
    [InlineData("er", 560d)]
    [InlineData("wk", 560d)]
    [InlineData("lp", 560d)]
    [InlineData("er", 1060d)]
    [InlineData("wk", 1060d)]
    [InlineData("lp", 1060d)]
    public Task AutomaticHelpAndZeroStatusUseActualBoundControls(string game, double width) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string path = CreateSave(game, fixture.Root);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new Discovery(new(path, "Character 1"));
        var vm = new DesktopTrackerViewModel(coordinator, eldenRingSaveDiscovery: discovery, blackMythWukongSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, Width = width, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); await vm.InitializeAsync(); await Idle();
            Button help = Tree(window).OfType<Button>().Single(x => x.IsVisible && AutomationProperties.GetName(x).EndsWith("directory discovery help", StringComparison.Ordinal));
            Assert.Equal("?", help.Content);
            Assert.True(ToolTipService.GetIsEnabled(help));
            Assert.Equal("Chosen Directory", ((Panel)help.Parent).Children.OfType<TextBlock>().Single().Text);
            help.BringIntoView(); await Idle();
            Assert.True(help.Focus());
            Assert.True(help.IsKeyboardFocused);
            var tooltip = Assert.IsType<ToolTip>(help.ToolTip);
            Assert.True(tooltip.IsOpen);
            Assert.Equal("Save directory was found automatically", tooltip.Content);
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "Save found automatically");
            var zero = new RuntimeGameObservation(Game(game), 0, DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(repository.State));
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.NoDeathsRecorded(zero)); await Idle();
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            Assert.Equal("Synced", status.Text);
            Assert.Equal("No deaths recorded yet, the tracker will update after your first death", total.Text);
            Assert.DoesNotContain('—', total.Text);
            tooltip.IsOpen = false;
            ((ScrollViewer)window.FindName("MainContentScrollViewer")).ScrollToTop(); await Idle();
            RecordRender(window, game + "-automatic-" + width);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(Game(game))); await Idle();
            Assert.NotEqual("Synced", status.Text);
            Assert.NotEqual(DesktopTrackerViewModel.NoDeathsRecordedMessage, total.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er", 560d, 400d)]
    [InlineData("wk", 560d, 400d)]
    [InlineData("lp", 560d, 400d)]
    [InlineData("er", 1060d, 760d)]
    [InlineData("wk", 1060d, 760d)]
    [InlineData("lp", 1060d, 760d)]
    public Task PathActionsAreCompactAccessibleAndCopyDisplayedPendingPath(string game, double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "synthetic-long-directory-name-for-streaming-account");
        CreateSave(game, directory);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        string? copied = null;
        bool failCopy = false;
        var constructor = typeof(MainWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(Func<string, string?, string?>), typeof(Action<string>)], null);
        Assert.NotNull(constructor);
        Action<string> copy = value => { if (failCopy) throw new IOException("Synthetic failure"); copied = value; };
        var window = (MainWindow)constructor.Invoke([new Func<string, string?, string?>((_, _) => directory), copy]);
        window.DataContext = vm; window.Width = width; window.Height = height; window.ShowInTaskbar = false;
        try
        {
            window.Show(); await Idle();
            Assert.DoesNotContain(Tree(window).OfType<Button>(), x => x.IsVisible && AutomationProperties.GetName(x) == "Copy directory path");
            var choose = Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, "Choose Directory"));
            var heading = Tree(window).OfType<TextBlock>().Single(x => x.IsVisible && x.Text == "Save Directory");
            Assert.NotSame(heading.Parent, choose.Parent);
            Assert.True(choose.Focusable && choose.IsTabStop);
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => DirectoryPath(vm, game) == directory && !vm.IsBusy);
            await Idle();
            TextBlock path = Tree(window).OfType<TextBlock>().Single(x => x.IsVisible && x.Text == directory);
            var grid = Assert.IsType<Grid>(path.Parent);
            var container = Assert.IsType<Border>(grid.Parent);
            Button copyButton = grid.Children.OfType<Button>().Single();
            Assert.Equal("Copy directory path", AutomationProperties.GetName(copyButton));
            Assert.NotEqual("Copy", copyButton.Content);
            Assert.Equal(directory, AutomationProperties.GetHelpText(path));
            Assert.Equal(TextWrapping.Wrap, path.TextWrapping);
            Assert.Equal("Consolas", path.FontFamily.Source);
            Assert.True(copyButton.Focusable && copyButton.IsTabStop);
            Assert.True(copyButton.Focus());
            Assert.True(copyButton.IsKeyboardFocused);
            copyButton.BringIntoView(); await Idle();
            Rect copyBounds = copyButton.TransformToAncestor(grid).TransformBounds(new Rect(copyButton.RenderSize));
            Assert.InRange(grid.ActualWidth - copyBounds.Right, -1, 1);
            Assert.True(container.ActualWidth <= ((ScrollViewer)window.FindName("MainContentScrollViewer")).ViewportWidth + 1);
            container.BringIntoView(); await Idle();
            RecordRender(window, game + "-path-" + width + "-" + height);
            if (game == "er")
            {
                var selector = (ComboBox)window.FindName("EldenRingProfileSlotSelector");
                var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
                var statusContainer = (Border)((StackPanel)status.Parent).Parent;
                var common = (Visual)window.Content;
                double gap = statusContainer.TransformToAncestor(common).Transform(new Point()).Y
                    - selector.TransformToAncestor(common).Transform(new Point(0, selector.ActualHeight)).Y;
                Assert.InRange(gap, 13, 15);
            }
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(directory, copied);
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == SelectedChoice(vm, game)!.Label);
            if (game == "er")
            {
                var selector = (ComboBox)window.FindName("EldenRingProfileSlotSelector");
                Assert.True(selector.IsVisible && selector.IsEnabled);
                Assert.True(vm.IsEldenRingMissedDeathAdjustmentAvailable);
                Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => AutomationProperties.GetName(x) == "Elden Ring character selection status");
            }
            string replacement = Path.Combine(fixture.Root, "pending");
            CreateSave(game, Path.Combine(replacement, "a")); CreateSave(game, Path.Combine(replacement, "b"), 2);
            await Choose(vm, game, replacement); await Idle();
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(replacement, copied);
            Assert.Equal(directory, ConfiguredDirectory(repository.State, game));
            failCopy = true;
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(replacement, copied);
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "The directory path could not be copied. Try again.");
            Cancel(vm, game); await Idle();
            Assert.Equal(directory, DirectoryPath(vm, game));
        }
        finally { window.Close(); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static void RecordRender(MainWindow window, string name)
    {
        string? output = Environment.GetEnvironmentVariable("SOULSTRACKER_PRESENTATION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        var metrics = Tree(window).OfType<FrameworkElement>().Where(x => x.IsVisible && (x is Button or TextBlock or ComboBox))
            .Select(x => new
            {
                type = x.GetType().Name,
                name = AutomationProperties.GetName(x),
                text = (x as TextBlock)?.Text,
                bounds = x.TransformToAncestor(content).TransformBounds(new Rect(x.RenderSize)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                enabled = x.IsEnabled
            });
        File.WriteAllText(Path.Combine(output, name + ".json"), System.Text.Json.JsonSerializer.Serialize(metrics));
    }
    private sealed class Discovery(DiscoveredLocalSave choice) : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([choice]);
    }
    internal static IEnumerable<DependencyObject> Tree(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
