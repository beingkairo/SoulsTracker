using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class SaveDirectoryControlTests
{
    [Theory]
    [InlineData("er", 560d)]
    [InlineData("wk", 560d)]
    [InlineData("lp", 560d)]
    [InlineData("er", 1060d)]
    [InlineData("wk", 1060d)]
    [InlineData("lp", 1060d)]
    public Task DirectoryButtonUsesPickerAndShowsAccessiblePath(string game, double width) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "synthetic-selected-directory-with-a-long-name");
        string path = CreateSave(game, directory);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var constructor = typeof(MainWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(Func<string, string?, string?>)], null);
        Assert.NotNull(constructor);
        int picks = 0;
        Func<string, string?, string?> picker = (title, current) => { picks++; return picks == 1 ? directory : null; };
        var window = (MainWindow)constructor.Invoke([picker]);
        window.DataContext = vm; window.Width = width; window.Height = 760; window.ShowActivated = false; window.ShowInTaskbar = false;
        try
        {
            window.Show();
            await Idle();
            Button choose = Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, "Choose directory"));
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => ConfiguredPath(repository.State, game) == path && !vm.IsBusy);
            await Idle();
            Assert.Equal(1, picks);
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "Chosen Directory");
            TextBlock shown = Tree(window).OfType<TextBlock>().Single(x => x.IsVisible && x.Text == directory);
            Assert.Equal(directory, shown.ToolTip);
            Assert.Equal(directory, AutomationProperties.GetHelpText(shown));
            var scroll = (ScrollViewer)window.FindName("MainContentScrollViewer");
            shown.BringIntoView(); await Idle();
            Rect bounds = shown.TransformToAncestor(scroll).TransformBounds(new Rect(shown.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= scroll.ActualWidth + 1);
            Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, "Change…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            Assert.Equal(2, picks);
            Assert.Equal(path, ConfiguredPath(repository.State, game));
            Assert.Equal(directory, DirectoryPath(vm, game));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public Task SourceSelectorAndCancelKeepPendingDirectorySeparate(string game) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "chosen");
        CreateSave(game, Path.Combine(directory, "a"));
        string selected = CreateSave(game, Path.Combine(directory, "b"), 2);
        string replacement = Path.Combine(fixture.Root, "replacement");
        CreateSave(game, Path.Combine(replacement, "a")); CreateSave(game, Path.Combine(replacement, "b"), 2);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        int picks = 0;
        var window = new MainWindow((_, _) => ++picks == 1 ? directory : replacement) { DataContext = vm, Width = 560, Height = 760, ShowActivated = false, ShowInTaskbar = false };
        Button Button(string content) => Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, content));
        try
        {
            window.Show(); await Idle();
            Button("Choose directory").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => Choices(vm, game).Count == 2);
            await Idle();
            Assert.Null(ConfiguredPath(repository.State, game));
            var selector = (ComboBox)window.FindName(Prefix(game) + "SaveSelector");
            Assert.True(selector.IsVisible && selector.IsEnabled);
            selector.SelectedItem = Choices(vm, game).Single(x => x.LocalPath == selected);
            await HostedDesktopPublisherTests.WaitUntil(() => ConfiguredPath(repository.State, game) == selected && !vm.IsBusy);
            await Idle();
            Assert.Single(repository.Saves);
            Button("Change…").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Idle();
            Button("Choose directory").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => DirectoryPath(vm, game) == replacement);
            await Idle();
            Assert.Equal(selected, ConfiguredPath(repository.State, game));
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "Pending directory");
            Button("Cancel").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Idle();
            Assert.Equal(directory, DirectoryPath(vm, game));
            Assert.Equal(directory, ConfiguredDirectory(repository.State, game));
            Assert.Equal(selected, ConfiguredPath(repository.State, game));
            Assert.Single(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task BoundRescanDoesNotSelectAnEldenCharacter() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "chosen");
        string path = CreateSave("er", directory);
        var repository = new MemoryRepository(Game("er")) { State = new(1, Game("er"), SoulsTracker.Domain.OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true, eldenRingSave: new(path, -1, directory)) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        var window = new MainWindow((_, _) => null) { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); await vm.InitializeAsync(); await Idle();
            await vm.RescanEldenRingSavesAsync(); await Idle();
            Assert.Empty(repository.Saves);
            Assert.Equal(-1, repository.State.EldenRingSave.SlotIndex);
            Assert.Null(vm.SelectedEldenRingProfileSlot);
        }
        finally { window.Close(); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<DependencyObject> Tree(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (DependencyObject child in Tree(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
