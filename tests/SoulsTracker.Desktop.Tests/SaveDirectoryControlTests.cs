using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class SaveDirectoryControlTests
{
    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public Task ChangeInvokesPickerBeforeMutationAndCancelOrSameDirectoryIsNoOp(string game) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "original");
        CreateSave(game, directory);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync(); await Choose(vm, game, directory);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new(Game(game), 12345, DateTimeOffset.UtcNow,
            EffectiveDeathTotalResult.SourceIdentityFor(repository.State))));
        var committed = repository.State;
        var selected = SelectedChoice(vm, game);
        var choices = Choices(vm, game).ToArray();
        string? status = vm.RuntimeReaderStatusText;
        repository.Saves.Clear();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        int picks = 0;
        string? result = null;
        var observed = new List<(int Changes, bool SameState, bool SameChoice)>();
        var window = new MainWindow((_, current) =>
        {
            picks++;
            observed.Add((notifications.Count, ReferenceEquals(committed, repository.State), ReferenceEquals(selected, SelectedChoice(vm, game))));
            return result;
        }, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle(); notifications.Clear();
            foreach (string? candidate in new[] { null, directory, Path.Combine(directory, ".") + Path.DirectorySeparatorChar })
            {
                result = candidate;
                await Invoke(window, "Change Directory");
                Assert.Equal(observed.Count, picks);
                Assert.NotEmpty(observed);
                Assert.All(observed, x => { Assert.Equal(0, x.Changes); Assert.True(x.SameState); Assert.True(x.SameChoice); });
                Assert.Empty(notifications);
                Assert.Same(committed, repository.State);
                Assert.Same(selected, SelectedChoice(vm, game));
                Assert.Equal(choices, Choices(vm, game));
                Assert.Equal(directory, DirectoryPath(vm, game));
                Assert.Equal(status, vm.RuntimeReaderStatusText);
                Assert.Equal("12345", ((TextBlock)window.FindName("TotalDeathsTextBlock")).Text);
                Assert.DoesNotContain(Tree(window).OfType<Button>(), x => x.IsVisible && Equals(x.Content, "Cancel"));
                Assert.Empty(repository.Saves);
            }
            Assert.Equal(3, picks);
            string replacement = Path.Combine(fixture.Root, "replacement");
            string replacementPath = CreateSave(game, replacement);
            result = replacement;
            await Invoke(window, "Change Directory");
            await HostedDesktopPublisherTests.WaitUntil(() => ConfiguredPath(repository.State, game) == replacementPath && !vm.IsBusy);
            await Idle();
            Assert.Equal(4, picks);
            Assert.Equal((0, true, true), observed.Last());
            Assert.Equal(replacement, DirectoryPath(vm, game));
            Assert.Equal(replacementPath, SelectedChoice(vm, game)?.LocalPath);
            Assert.Single(repository.Saves);
            Assert.DoesNotContain(Tree(window).OfType<Button>(), x => x.IsVisible && Equals(x.Content, "Cancel"));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    public Task LiesStartupRetainsSourceAndEnablesKnownCharacters(bool selectedDirectory, int characters) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "account");
        string selected = CreateSave("lp", directory);
        string paired = Path.Combine(directory, "SaveData-1_Character_2.sav");
        File.Copy(selected, paired);
        File.SetLastWriteTimeUtc(paired, DateTime.UtcNow.AddMinutes(1));
        if (characters > 1) CreateSave("lp", directory, 2);
        var before = Directory.GetFiles(directory).ToDictionary(x => x, File.ReadAllBytes);
        var repository = new MemoryRepository(Game("lp")) { State = new(1, Game("lp"), OverlayConfiguration.Default,
            liesOfPSave: new(selected, selectedDirectory ? directory : null)) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        for (int restart = 0; restart < 2; restart++)
        {
            var vm = CreateViewModel(coordinator);
            var window = new MainWindow((_, _) => null) { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); await vm.InitializeAsync(); await Idle();
                var selector = (ComboBox)window.FindName("LiesOfPSaveSelector");
                Assert.Equal(characters, selector.Items.Count);
                Assert.Equal(selected, vm.SelectedLiesOfPSaveChoice?.LocalPath);
                Assert.Empty(repository.Saves);
                if (characters > 1)
                {
                    Assert.True(selector.IsVisible);
                    Assert.True(selector.IsEnabled);
                    if (restart == 1)
                    {
                        selector.SelectedItem = vm.LiesOfPSaveChoices.Single(x => x.LocalPath != selected);
                        await HostedDesktopPublisherTests.WaitUntil(() => repository.Saves.Count == 1);
                        Assert.Equal(((DiscoveredLocalSave)selector.SelectedItem).LocalPath, repository.State.LiesOfPSave.LocalPath);
                    }
                }
                else Assert.False(selector.IsVisible);
                foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
            }
            finally { window.Close(); }
        }
    });

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
            Button choose = Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, "Choose Directory"));
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
            Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, "Change Directory")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        Button Button(string content)
        {
            var buttons = Tree(window).OfType<Button>().Where(x => x.IsVisible).ToArray();
            if (content == "Choose Directory" && !buttons.Any(x => Equals(x.Content, content))) content = "Change Directory";
            return buttons.Single(x => Equals(x.Content, content));
        }
        try
        {
            window.Show(); await Idle();
            Button("Choose Directory").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => Choices(vm, game).Count == 2);
            await Idle();
            Assert.Null(ConfiguredPath(repository.State, game));
            var selector = (ComboBox)window.FindName(Prefix(game) + "SaveSelector");
            Assert.True(selector.IsVisible && selector.IsEnabled);
            selector.SelectedItem = Choices(vm, game).Single(x => x.LocalPath == selected);
            await HostedDesktopPublisherTests.WaitUntil(() => ConfiguredPath(repository.State, game) == selected && !vm.IsBusy);
            await Idle();
            Assert.Single(repository.Saves);
            Button("Change Directory").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Idle();
            await HostedDesktopPublisherTests.WaitUntil(() => DirectoryPath(vm, game) == replacement);
            await Idle();
            Assert.Equal(selected, ConfiguredPath(repository.State, game));
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "Pending Directory");
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

    [Theory]
    [InlineData("er", false)]
    [InlineData("wk", false)]
    [InlineData("lp", false)]
    [InlineData("er", true)]
    [InlineData("wk", true)]
    [InlineData("lp", true)]
    public Task RejectedDirectoryCancelRestoresCommittedFeedback(string game, bool unusable) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string original = Path.Combine(fixture.Root, "original");
        string path = CreateSave(game, original);
        string rejected = Path.Combine(fixture.Root, "rejected");
        Directory.CreateDirectory(rejected);
        string? rejectedPath = unusable ? CreateSave(game, rejected) : null;
        if (rejectedPath is not null) File.WriteAllBytes(rejectedPath, [1, 2]);
        byte[] before = File.ReadAllBytes(path);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, game, original);
        var committed = repository.State;
        var source = EffectiveDeathTotalResult.SourceIdentityFor(committed);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new RuntimeGameObservation(Game(game), 37, DateTimeOffset.UtcNow, source)));
        repository.Saves.Clear();
        string? committedStatus = Status(vm, game);
        var window = new MainWindow((_, _) => rejected) { DataContext = vm, Width = 560, Height = 760, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            await Invoke(window, "Change…");
            await HostedDesktopPublisherTests.WaitUntil(() => Status(vm, game) != committedStatus);
            await Idle();
            Assert.Equal(original, DirectoryPath(vm, game));
            Assert.Same(committed, repository.State);
            Assert.Empty(repository.Saves);
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "37");
            string? rejectionStatus = Status(vm, game);
            await Invoke(window, "Cancel");
            Assert.Equal(committedStatus, Status(vm, game));
            Assert.Contains("previous selection is unchanged", rejectionStatus, StringComparison.Ordinal);
            Assert.Contains("attempted directory", rejectionStatus, StringComparison.Ordinal);
            Assert.Null(DirectoryPresentationTests.Feedback(vm, game));
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == rejectionStatus);
            Assert.Equal(original, DirectoryPath(vm, game));
            Assert.Equal(path, ConfiguredPath(repository.State, game));
            Assert.Equal(source, EffectiveDeathTotalResult.SourceIdentityFor(repository.State));
            Assert.Equal(committed.EldenRingSave.SlotIndex, repository.State.EldenRingSave.SlotIndex);
            Assert.Same(committed, repository.State);
            Assert.Empty(repository.Saves);
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "37");
            Assert.Equal(before, File.ReadAllBytes(path));
            if (rejectedPath is not null) Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(rejectedPath));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public Task RejectedDirectoryRecoveryPreservesCurrentErrorsAndSourceChoices(string game) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string original = Path.Combine(fixture.Root, "original");
        string path = CreateSave(game, original);
        byte[] originalBytes = File.ReadAllBytes(path);
        string empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(empty);
        string multiple = Path.Combine(fixture.Root, "multiple");
        string first = CreateSave(game, Path.Combine(multiple, "a"));
        string second = CreateSave(game, Path.Combine(multiple, "b"), 2);
        byte[] firstBytes = File.ReadAllBytes(first);
        byte[] secondBytes = File.ReadAllBytes(second);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, game, original);
        var committed = repository.State;
        var source = EffectiveDeathTotalResult.SourceIdentityFor(committed);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new RuntimeGameObservation(Game(game), 37, DateTimeOffset.UtcNow, source)));
        repository.Saves.Clear();
        string? committedStatus = Status(vm, game);
        string? picked = empty;
        var window = new MainWindow((_, _) => picked) { DataContext = vm, Width = 560, Height = 760, ShowActivated = false, ShowInTaskbar = false };
        async Task Pick(string? directory)
        {
            picked = directory;
            await Invoke(window, "Choose directory");
        }
        async Task WaitForStatus(string? status)
        {
            await HostedDesktopPublisherTests.WaitUntil(() => Status(vm, game) == status && !vm.IsBusy);
            await Idle();
        }
        try
        {
            window.Show(); await Idle();
            await Pick(empty);
            await HostedDesktopPublisherTests.WaitUntil(() => Status(vm, game) != committedStatus);
            string? rejectedStatus = Status(vm, game);
            await Pick(null);
            Assert.Equal(rejectedStatus, Status(vm, game));
            await Pick(multiple);
            await HostedDesktopPublisherTests.WaitUntil(() => DirectoryPath(vm, game) == multiple);
            await Idle();
            Assert.Null(SelectedChoice(vm, game));
            await Pick(empty);
            await WaitForStatus(rejectedStatus);
            await Invoke(window, "Cancel");
            Assert.Equal(committedStatus, Status(vm, game));
            Assert.Equal(original, DirectoryPath(vm, game));
            Assert.Equal(path, SelectedChoice(vm, game)!.LocalPath);
            Assert.Same(committed, repository.State);
            Assert.Empty(repository.Saves);

            // Rescan after a rejected attempt restores the committed directory.
            await Pick(empty);
            await WaitForStatus(rejectedStatus);
            await Invoke(window, "Rescan");
            await WaitForStatus(committedStatus);
            Assert.Empty(repository.Saves);
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "37");

            // A real failure of the committed source must survive Cancel, both
            // on its own and after a separate rejected replacement.
            File.WriteAllBytes(path, [1, 2]);
            await Pick(empty);
            await WaitForStatus(rejectedStatus);
            await Invoke(window, "Rescan");
            await HostedDesktopPublisherTests.WaitUntil(() => Status(vm, game) != rejectedStatus);
            await Idle();
            string? unavailableStatus = Status(vm, game);
            Assert.Contains("No usable saves", unavailableStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("attempted directory", unavailableStatus, StringComparison.Ordinal);
            await Invoke(window, "Cancel");
            Assert.Equal(unavailableStatus, Status(vm, game));
            Assert.Equal(unavailableStatus, ((TextBlock)window.FindName("RuntimeReaderStatusTextBlock")).Text);
            await Pick(empty);
            await WaitForStatus(rejectedStatus);
            await Invoke(window, "Cancel");
            Assert.Equal(unavailableStatus, Status(vm, game));
            Assert.Same(committed, repository.State);
            Assert.Empty(repository.Saves);
            Assert.Equal(source, EffectiveDeathTotalResult.SourceIdentityFor(repository.State));

            File.WriteAllBytes(path, originalBytes);
            await Invoke(window, "Rescan");
            await WaitForStatus(committedStatus);
            await Pick(null);
            Assert.DoesNotContain(Tree(window).OfType<Button>(), x => x.IsVisible && Equals(x.Content, "Cancel"));
            Assert.Equal(committedStatus, Status(vm, game));
            Assert.Empty(repository.Saves);

            // Recovery still requires explicit selection from multiple sources.
            await Pick(multiple);
            await HostedDesktopPublisherTests.WaitUntil(() => DirectoryPath(vm, game) == multiple);
            await Idle();
            Assert.Null(SelectedChoice(vm, game));
            Assert.Same(committed, repository.State);
            var selector = (ComboBox)window.FindName(Prefix(game) + "SaveSelector");
            selector.SelectedItem = Choices(vm, game).Single(x => x.LocalPath == second);
            await HostedDesktopPublisherTests.WaitUntil(() => ConfiguredPath(repository.State, game) == second && !vm.IsBusy);
            await Idle();
            Assert.Single(repository.Saves);
            Assert.Equal(multiple, ConfiguredDirectory(repository.State, game));
            Assert.Equal(SelectedChoice(vm, game)!.Label, Status(vm, game));
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Equal(firstBytes, File.ReadAllBytes(first));
            Assert.Equal(secondBytes, File.ReadAllBytes(second));
        }
        finally { window.Close(); }
    });

    private static async Task Invoke(MainWindow window, string content)
    {
        // Retain the prior interaction sequences while migrating visible labels.
        content = content switch { "Change…" => "Change Directory", "Choose directory" => "Choose Directory", "Rescan" => "Refresh", _ => content };
        if (content == "Choose Directory" && !Tree(window).OfType<Button>().Any(x => x.IsVisible && Equals(x.Content, content))) content = "Change Directory";
        Button button = Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, content));
        Assert.True(button.IsEnabled);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await Idle();
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<DependencyObject> Tree(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (DependencyObject child in Tree(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
