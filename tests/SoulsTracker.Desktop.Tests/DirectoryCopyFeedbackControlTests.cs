using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class DirectoryCopyFeedbackControlTests
{
    [Theory]
    [InlineData("er", true)]
    [InlineData("wk", true)]
    [InlineData("lp", true)]
    [InlineData("er", false)]
    [InlineData("wk", false)]
    [InlineData("lp", false)]
    public Task CopyFeedbackClearsWhenGameChanges(string game, bool fail) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "chosen");
        CreateSave(game, directory);
        string other = game == "wk" ? "lp" : "wk";
        string otherDirectory = Path.Combine(fixture.Root, "other");
        CreateSave(other, otherDirectory);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, game, directory);
        await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == Game(other)));
        await Choose(vm, other, otherDirectory);
        await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == Game(game)));
        repository.Saves.Clear();
        var copies = new List<string>();
        var window = new MainWindow((_, _) => null, path =>
        {
            copies.Add(path);
            if (fail) throw new IOException("Synthetic clipboard failure");
        }) { DataContext = vm, Width = 560, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var feedback = (TextBlock)window.FindName("DirectoryCopyStatus");
            foreach (GameId target in new[] { GameId.DemonsSouls, Game(other) })
            {
                Copy(window);
                Assert.Equal(directory, copies.Last());
                Assert.Equal(Message(fail), feedback.Text);
                Assert.Empty(repository.Saves);
                int attempts = copies.Count;
                await Switch(window, vm, target);
                Assert.Empty(feedback.Text);
                Assert.Equal(Visibility.Collapsed, feedback.Visibility);
                Assert.Equal(attempts, copies.Count);
                Assert.Single(repository.Saves); // Explicit game selection only.
                if (target == GameId.DemonsSouls)
                    Assert.DoesNotContain(Tree(window).OfType<Button>(), x => x.IsVisible && Equals(x.Content, "Copy") && x.Tag is string);
                else
                    Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == otherDirectory);
                await Switch(window, vm, Game(game));
                Assert.Empty(feedback.Text);
                Assert.Equal(2, repository.Saves.Count);
                Assert.Equal(attempts, copies.Count);
                repository.Saves.Clear();
            }
            fail = !fail;
            Copy(window);
            Assert.Equal(Message(fail), feedback.Text);
            Assert.Equal(directory, copies.Last());
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er", true)]
    [InlineData("wk", true)]
    [InlineData("lp", true)]
    [InlineData("er", false)]
    [InlineData("wk", false)]
    [InlineData("lp", false)]
    public Task CopyFeedbackTracksPendingAndCancelledPaths(string game, bool fail) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "chosen");
        CreateSave(game, directory);
        string pending = Path.Combine(fixture.Root, "pending");
        CreateSave(game, Path.Combine(pending, "a"));
        CreateSave(game, Path.Combine(pending, "b"), 2);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync(); await Choose(vm, game, directory);
        repository.Saves.Clear();
        var copies = new List<string>();
        var window = new MainWindow((_, _) => pending, path =>
        {
            copies.Add(path);
            if (fail) throw new IOException("Synthetic clipboard failure");
        }) { DataContext = vm, Width = 560, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var feedback = (TextBlock)window.FindName("DirectoryCopyStatus");
            Copy(window);
            Assert.Equal(Message(fail), feedback.Text);
            Assert.Equal(directory, Assert.Single(copies));
            Click(window, "Change Directory"); await Idle();
            Assert.Equal(Message(fail), feedback.Text);
            Click(window, "Choose Directory");
            await HostedDesktopPublisherTests.WaitUntil(() => DirectoryPath(vm, game) == pending && !vm.IsBusy);
            await Idle();
            Assert.Empty(feedback.Text);
            Assert.Single(copies);
            Assert.Empty(repository.Saves);
            Copy(window);
            Assert.Equal(pending, copies.Last());
            Assert.Equal(Message(fail), feedback.Text);
            string? discoveryFeedback = Status(vm, game);
            Assert.False(string.IsNullOrEmpty(discoveryFeedback));
            Click(window, "Cancel"); await Idle();
            Assert.Equal(directory, DirectoryPath(vm, game));
            Assert.Empty(feedback.Text);
            Assert.Equal(Visibility.Collapsed, feedback.Visibility);
            Assert.Equal(2, copies.Count);
            Assert.Empty(repository.Saves);
            fail = !fail;
            Copy(window);
            Assert.Equal(directory, copies.Last());
            Assert.Equal(Message(fail), feedback.Text);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er", true)]
    [InlineData("wk", true)]
    [InlineData("lp", true)]
    [InlineData("er", false)]
    [InlineData("wk", false)]
    [InlineData("lp", false)]
    public Task CopyFeedbackSurvivesUnchangedContextRefresh(string game, bool fail) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "chosen");
        CreateSave(game, directory);
        string rejected = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(rejected);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync(); await Choose(vm, game, directory);
        repository.Saves.Clear();
        var copies = new List<string>();
        var window = new MainWindow((_, _) => rejected, path =>
        {
            copies.Add(path);
            if (fail) throw new IOException("Synthetic clipboard failure");
        }) { DataContext = vm, Width = 560, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var feedback = (TextBlock)window.FindName("DirectoryCopyStatus");
            Copy(window);
            Assert.Equal(Message(fail), feedback.Text);
            await Rescan(vm, game); await Idle();
            Assert.Equal(Message(fail), feedback.Text);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(Game(game))); await Idle();
            Assert.Equal(Message(fail), feedback.Text);
            Assert.NotEqual("Synced", ((TextBlock)window.FindName("RuntimeReaderStatusTextBlock")).Text);
            Click(window, "Change Directory"); await Idle();
            Click(window, "Choose Directory");
            await HostedDesktopPublisherTests.WaitUntil(() => Status(vm, game)?.Contains("attempted directory", StringComparison.Ordinal) == true);
            await Idle();
            string? rejection = Status(vm, game);
            Assert.Equal(directory, DirectoryPath(vm, game));
            Assert.Equal(Message(fail), feedback.Text);
            fail = !fail;
            Copy(window);
            Assert.Equal(Message(fail), feedback.Text);
            Assert.Equal(rejection, Status(vm, game));
            Click(window, "Cancel"); await Idle();
            Assert.Equal(Message(fail), feedback.Text);
            Assert.Equal(new[] { directory, directory }, copies);
            Assert.Empty(repository.Saves);
            window.DataContext = null; await Idle();
            Assert.Empty(feedback.Text);
            window.DataContext = vm; await Idle();
            Assert.Empty(feedback.Text);
            Copy(window);
            Assert.Equal(Message(fail), feedback.Text);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    private static string Message(bool fail) => fail ? "The directory path could not be copied. Try again." : "Directory path copied.";
    private static void Copy(MainWindow window) => Click(window, "Copy");
    private static void Click(MainWindow window, string content) => Tree(window).OfType<Button>()
        .Single(x => x.IsVisible && Equals(x.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Switch(MainWindow window, DesktopTrackerViewModel vm, GameId game)
    {
        ((ComboBox)window.FindName("GameSelector")).SelectedItem = vm.GameChoices.Single(x => x.GameId == game);
        await HostedDesktopPublisherTests.WaitUntil(() => vm.SelectedGame?.GameId == game && !vm.IsBusy);
        await Idle();
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
