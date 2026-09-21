using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class UniformPresentationControlTests
{
    [Fact]
    public Task ConfiguredEmptyEldenRingSlotHasOneCanonicalSelectionStatus() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string path = CreateSave("er", fixture.Root);
        byte[] bytes = File.ReadAllBytes(path);
        // Keep occupied profile metadata, but give the selected slot a bounded,
        // zero-version record so the real death reader reports EmptySlot.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x48), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x50), 0x500);
        File.WriteAllBytes(path, bytes);
        var configuration = new EldenRingSaveConfiguration(path, 0);
        var repository = new MemoryRepository(GameId.EldenRing)
        {
            State = new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, GameId.EldenRing,
                OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true, eldenRingSave: configuration),
        };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        Assert.Equal(0, vm.SelectedEldenRingProfileSlot?.Index);
        Assert.Null(vm.EldenRingDirectoryStatus);
        var reader = new EldenRingSaveDeathReader();
        reader.Configure(configuration);
        RuntimeGameReadResult result = Assert.IsType<RuntimeGameReadResult>(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, result.Status);
        Assert.Null(result.Observation);
        vm.ApplyRuntimeReaderResult(result);
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            Assert.Equal(configuration, repository.State.EldenRingSave);
            Assert.Empty(repository.Saves);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            Assert.Equal("Unable to read total deaths.", total.Text);
            Assert.False(vm.IsTotalDeathsValueNumeric);
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            Assert.Equal("Choose a character to continue.", status.Text);
            Assert.Equal(status.Text, vm.RuntimeReaderStatusText);
            Assert.Same(status, Assert.Single(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == status.Text));
            Assert.Null(vm.EldenRingDirectoryActionFeedback);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public Task SelectionStatusHasOneVisibleOwner(string game) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        var discovery = new Discovery(new(Path.Combine(fixture.Root, "one.sav"), "Character 1"),
            new(Path.Combine(fixture.Root, "two.sav"), "Character 2"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, eldenRingSaveDiscovery: discovery,
            blackMythWukongSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await vm.InitializeAsync(); await Idle();
            TextBlock status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            Assert.Equal("Choose a character to continue.", status.Text);
            Assert.Same(status, Assert.Single(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == status.Text));
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == fixture.Root);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public Task DirectoryControlsShareActionRowAndIcon(string game) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        CreateSave(game, fixture.Root);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        string? copied = null;
        var window = new MainWindow((_, _) => fixture.Root, text => copied = text) { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            Button choose = Tree(window).OfType<Button>().Single(x => x.IsVisible && Equals(x.Content, "Choose Directory"));
            var row = Assert.IsAssignableFrom<Panel>(choose.Parent);
            Button refresh = row.Children.OfType<Button>().Single(x => Equals(x.Content, "Refresh"));
            Assert.True(row.Children.IndexOf(choose) < row.Children.IndexOf(refresh));
            Assert.DoesNotContain(row.Children.OfType<TextBlock>(), x => x.Text == "Save Directory");
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => DirectoryPath(vm, game) == fixture.Root && !vm.IsBusy);
            await Idle();
            Button copy = Tree(window).OfType<Button>().Single(x => x.IsVisible && AutomationProperties.GetName(x) == "Copy directory path");
            Assert.Equal("Copy directory path", copy.ToolTip);
            Assert.NotEqual("Copy", copy.Content);
            Assert.True(copy.Focusable && copy.IsTabStop);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(fixture.Root, copied);
            Assert.Contains(row.Children.OfType<Button>(), x => x.IsVisible && Equals(x.Content, "Change Directory"));
            Assert.DoesNotContain(Tree(window).OfType<Button>(), x => x.IsVisible && Equals(x.Content, "Rescan"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task HotkeysLiveInSettingsWithFocusHelp() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var increment = (TextBox)window.FindName("IncrementHotkeyTextBox");
            Assert.False(increment.IsVisible);
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
            Assert.True(increment.IsVisible);
            var help = (Button)window.FindName("GlobalHotkeysHelpButton");
            Assert.Equal("?", help.Content);
            help.BringIntoView(); await Idle();
            Assert.True(help.Focus());
            Assert.True(((ToolTip)help.ToolTip).IsOpen);
            Assert.True(ToolTipService.GetIsEnabled(help));
            ((TabControl)window.FindName("WorkspaceTabs")).Focus(); await Idle();
            Assert.False(((ToolTip)help.ToolTip).IsOpen);
            var missed = (Button)window.FindName("MissedDeathsHelpButton");
            Assert.EndsWith("Customizable hotkeys for increment and decrement of deaths can be found in the Settings tab.", AutomationProperties.GetHelpText(missed));
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CopyUsesOneAnchoredConfirmationWithoutMovingContent() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        CreateSave("lp", fixture.Root);
        var repository = new MemoryRepository(GameId.LiesOfP);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync(); await Choose(vm, "lp", fixture.Root);
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            Point before = total.TranslatePoint(new Point(), window);
            var copy = Tree(window).OfType<Button>().Single(x => x.IsVisible && AutomationProperties.GetName(x) == "Copy directory path");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            var toast = Assert.IsType<Border>(window.FindName("CopyFeedbackOverlay"));
            Assert.NotSame(window.Content, toast.Parent);
            var copyBounds = copy.TransformToAncestor(window).TransformBounds(new Rect(copy.RenderSize));
            var feedbackBounds = toast.TransformToAncestor(window).TransformBounds(new Rect(toast.RenderSize));
            Assert.InRange(feedbackBounds.Top - copyBounds.Bottom, 0, 28);
            Assert.InRange(Math.Abs(feedbackBounds.Right - copyBounds.Right), 0, 16);
            Assert.False(toast.IsHitTestVisible);
            Assert.Equal(before, total.TranslatePoint(new Point(), window));
            Assert.Equal("Directory path copied", ((TextBlock)window.FindName("DirectoryCopyStatus")).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CopySuccessExpiresOnTheWindowDispatcher() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        CreateSave("lp", fixture.Root);
        var repository = new MemoryRepository(GameId.LiesOfP);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync(); await Choose(vm, "lp", fixture.Root);
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            Tree(window).OfType<Button>().Single(x => x.IsVisible && AutomationProperties.GetName(x) == "Copy directory path")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(TimeSpan.FromSeconds(5.5)); await Idle();
            Assert.Empty(((TextBlock)window.FindName("DirectoryCopyStatus")).Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public Task UnreadableReasonIsNotDuplicatedInTotal(string game) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        CreateSave(game, fixture.Root);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        await Choose(vm, game, fixture.Root);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.SelectedSaveUnreadable(Game(game)));
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            Assert.Same(status, Assert.Single(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == status.Text));
            Assert.Contains("Refresh", status.Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    [InlineData("demons")]
    public Task SharedCountAndReaderControlsRespectCapabilities(string id) => HostedConnectionTests.OnDispatcher(async () =>
    {
        GameId game = id == "demons" ? GameId.DemonsSouls : GameId.Parse(id);
        var repository = new MemoryRepository(game);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            Assert.Equal(id == "demons" ? "Manual" : "Game unavailable", status.Text);
            if (id != "demons") vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new(game, 0, DateTimeOffset.UtcNow, game.Value)));
            await Idle();
            Assert.Equal(id == "demons" ? "Manual" : "Synced", status.Text);
            Assert.Same(status, Assert.Single(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == status.Text));
            Assert.Equal("0", total.Text);
            Assert.Equal(42, total.FontSize);
            Assert.DoesNotContain(Tree(window).OfType<Button>(), x => x.IsVisible && (Equals(x.Content, "Refresh") || AutomationProperties.GetName(x) == "Copy directory path"));
            Assert.Equal(id == "demons", Tree(window).OfType<Button>().Any(x => x.IsVisible && AutomationProperties.GetName(x) == "Increase manual deaths"));
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
            Assert.True(((TextBox)window.FindName("IncrementHotkeyTextBox")).IsVisible);
            Assert.Equal(id == "demons", ((TextBox)window.FindName("IncrementHotkeyTextBox")).IsEnabled);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private sealed class Discovery(params DiscoveredLocalSave[] choices) : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>(choices);
    }
}
