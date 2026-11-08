using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
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
public sealed class HollowKnightRefinementTests
{
    private sealed class Fixture : IDisposable
    {
        private static string Area => Path.Combine(Path.GetTempPath(), "SoulsTracker.HkUi");
        public string Root { get; } = Path.Combine(Area, Guid.NewGuid().ToString("N"));
        public string Game => Path.Combine(Root, "game");
        public Guid Installation { get; } = Guid.NewGuid();
        public Guid Run { get; } = Guid.NewGuid();
        public string Plugin => Path.Combine(Game, HollowKnightPluginInstaller.PluginFolder, HollowKnightPluginInstaller.PluginName);
        public Fixture(bool installed)
        {
            string? repository = AppContext.BaseDirectory;
            while (repository is not null && !File.Exists(Path.Combine(repository, "SoulsTracker.sln"))) repository = Path.GetDirectoryName(repository);
            Assert.NotNull(repository);
            string managed = Path.Combine(Game, "hollow_knight_Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(Game, "hollow_knight.exe"), "synthetic fixture");
            using var archive = ZipFile.OpenRead(Path.Combine(repository, "plugins", "Soulstracker.DeathTracker", "obj", "api-v77", "api.zip"));
            foreach (var entry in archive.Entries.Where(x => x.Name.EndsWith(".dll", StringComparison.Ordinal)))
                entry.ExtractToFile(Path.Combine(managed, entry.Name));
            if (!installed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Plugin)!);
            File.WriteAllBytes(Plugin, [1, 2, 3]);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Plugin)!, "installation.id"), Installation.ToString("D"));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Plugin)!, "ownership.json"), JsonSerializer.Serialize(new { InstallationId = Installation, PluginHash = Convert.ToHexString(SHA256.HashData([1, 2, 3])) }));
        }
        public PersistentTrackerState State => new(1, GameId.HollowKnight, OverlayConfiguration.Default,
            textExports: new(Path.Combine(Root, "deaths.txt"), true), hollowKnight: new(Game, Installation));
        public RuntimeGameReadResult Read(PersistentTrackerState state, long total, Guid? run = null) => RuntimeGameReadResult.FromHollowKnight(
            new(EffectiveDeathTotalResult.SourceIdentityFor(state), run ?? Run, 1, total, 1, DateTimeOffset.UtcNow), "Tracking active");
        public void Dispose()
        {
            Assert.StartsWith(Path.GetFullPath(Area) + Path.DirectorySeparatorChar, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [Theory]
    [InlineData(560d, 760d)]
    [InlineData(560d, 400d)]
    [InlineData(1060d, 760d)]
    public Task BoundControlsUseVerifiedInstallationAndSharedDirectoryPresentation(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new Fixture(true);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await vm.DiscoverHollowKnightAsync();
        string? copied = null;
        var window = new MainWindow((_, _) => null, value => copied = value) { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var buttons = Tree(window).OfType<Button>().ToArray();
            Assert.Single(buttons, x => x.IsVisible && Equals(x.Content, "Uninstall Death Tracking"));
            Assert.DoesNotContain(buttons, x => x.IsVisible && Equals(x.Content, "Install Death Tracking"));
            Assert.DoesNotContain(buttons, x => Equals(x.Content, "Roll Back Failed Install"));
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "DEATH TRACKER");
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.Text == "Choose the game currently being tracked.");
            var help = buttons.Single(x => AutomationProperties.GetName(x) == "Game session help");
            Assert.Equal("Choose the game you'd like to track.", Assert.IsType<ToolTip>(help.ToolTip).Content);
            Assert.Equal("Installs a DLL to capture death events after installing even while SoulsTracker is closed. If you died before installing the tracker won't include those deaths.",
                AutomationProperties.GetHelpText(buttons.Single(x => AutomationProperties.GetName(x) == "Hollow Knight death tracker help")));
            Assert.Equal("In case you've already started a playthrough you can manually adjust your deaths here and it will count towards your total.",
                AutomationProperties.GetHelpText(buttons.Single(x => AutomationProperties.GetName(x) == "Hollow Knight manual adjustment help")));
            var path = Tree(window).OfType<TextBlock>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight chosen directory");
            Assert.Equal(fixture.Game, path.Text);
            var copy = buttons.Single(x => x.IsVisible && Equals(x.Tag, fixture.Game));
            copy.BringIntoView(); await Idle(); copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(fixture.Game, copied);
            var increment = buttons.Single(x => AutomationProperties.GetName(x) == "Add Hollow Knight death");
            Assert.False(increment.IsEnabled);
            vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 0)); await Idle();
            Assert.Equal("Synced", vm.RuntimeReaderStatusText);
            Assert.Equal("0", vm.TotalDeathsText);
            Assert.True(increment.IsEnabled);
            increment.BringIntoView(); await Idle(); increment.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal("1", vm.TotalDeathsText);
            Assert.Equal(1, vm.HollowKnightAddedDeaths);
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            Assert.Equal(window.FindResource("SuccessBrush"), status.Foreground);
            Render(window, "hollow-knight-installed-" + width + "-" + height);
            ((ScrollViewer)window.FindName("MainContentScrollViewer")).ScrollToTop(); await Idle();
            Render(window, "hollow-knight-installed-top-" + width + "-" + height);
            File.WriteAllText(fixture.Plugin, "modified fixture plugin");
            await vm.DiscoverHollowKnightAsync(); await Idle();
            Assert.False(vm.IsHollowKnightInstalled);
            Assert.Single(buttons, x => x.IsVisible && Equals(x.Content, "Install Death Tracking"));
            Assert.DoesNotContain(buttons, x => x.IsVisible && Equals(x.Content, "Uninstall Death Tracking"));
            Assert.False(((TextBlock)window.FindName("TotalDeathsTextBlock")).IsVisible);
            Render(window, "hollow-knight-unavailable-" + width + "-" + height);
            ((ScrollViewer)window.FindName("MainContentScrollViewer")).ScrollToTop(); await Idle();
            Render(window, "hollow-knight-unavailable-top-" + width + "-" + height);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task AdjustmentsPublishImmediatelyWithoutMutatingRawRecordsAndFollowSaveIdentity()
    {
        using var fixture = new Fixture(true);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var text = new TextExportStatePublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, text);
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync(); await vm.DiscoverHollowKnightAsync();
        var raw = fixture.Read(repository.State, 0);
        vm.ApplyRuntimeReaderResult(raw);
        text.PublishRuntimeObservation(repository.State, raw);
        await vm.IncrementHollowKnightDeathsAsync();
        Assert.Equal(0, raw.PluginObservation!.Total);
        Assert.Equal("1", vm.TotalDeathsText);
        var preview = new AppearancePreviewModel(new OverlayAppearanceDraft(), OverlayAppearance.Default);
        preview.Update(OverlayTitleIconMode.Off, vm.TotalDeathsText);
        Assert.Equal("1", preview.Value);
        Assert.Equal("1", new HostedOverlayProjection().FromAcceptedPublication(repository.State, raw).Death!.Value);
        Guid other = Guid.NewGuid();
        vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 0, other));
        Assert.Equal("0", vm.TotalDeathsText);
        Assert.Equal(0, vm.HollowKnightAddedDeaths);
        await vm.IncrementHollowKnightDeathsAsync(); await vm.IncrementHollowKnightDeathsAsync();
        vm.ApplyRuntimeReaderResult(raw);
        Assert.Equal("1", vm.TotalDeathsText);
        await vm.DecrementHollowKnightDeathsAsync(); await vm.DecrementHollowKnightDeathsAsync();
        Assert.Equal("0", vm.TotalDeathsText);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.FromHollowKnight(null, "Tracking unavailable"));
        Assert.False(vm.CanAdjustHollowKnightDeaths);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
        await text.DisposeAsync();
        Assert.Equal("Total Deaths: 0", await File.ReadAllTextAsync(repository.State.TextExports.DeathsPath!));
    }

    [Fact]
    public void AnOlderReadCannotRepublishAnUnadjustedTotalAfterACommit()
    {
        using var fixture = new Fixture(false);
        var state = fixture.State;
        var session = new RuntimePublicationSession();
        var read = session.BeginRead(state);
        var changed = TrackerStateTransitionService.Apply(state, new AdjustHollowKnightDeathsCommand(fixture.Installation, fixture.Run, true)).State;
        session.SelectState(changed);
        int published = 0;
        session.CompleteRead(read, state, fixture.Read(state, 3), _ => published++, _ => published++);
        Assert.Equal(0, published);
        session.CompleteRead(session.BeginRead(changed), changed, fixture.Read(changed, 3), _ => published++, _ => published++);
        Assert.Equal(2, published);
    }

    [Fact]
    public async Task CommittedAdjustmentUpdatesActualHostedSenderAndTxtWithoutAnotherGameRead()
    {
        using var fixture = new Fixture(false);
        var state = fixture.State;
        var server = new HostedDesktopPublisherTests.Server();
        await using var sender = new HostedOverlayPublisher(HostedDesktopPublisherTests.Configuration(), server);
        await using var hosted = new HostedDesktopPublisher(sender, state);
        await using var text = new TextExportStatePublisher();
        var raw = fixture.Read(state, 0);
        hosted.PublishAccepted(state, raw);
        text.PublishRuntimeObservation(state, raw);
        await HostedDesktopPublisherTests.WaitUntil(() => server.Value == "0" && sender.Status == HostedPublisherStatus.Ready);
        var added = TrackerStateTransitionService.Apply(state, new AdjustHollowKnightDeathsCommand(fixture.Installation, fixture.Run, true)).State;
        await hosted.PublishAsync(new(added, TrackerCommandType.AdjustHollowKnightDeaths));
        await text.PublishAsync(new(added, TrackerCommandType.AdjustHollowKnightDeaths));
        await HostedDesktopPublisherTests.WaitUntil(() => server.Value == "1" && sender.Status == HostedPublisherStatus.Ready);
        await text.DisposeAsync();
        Assert.Equal("Total Deaths: 1", await File.ReadAllTextAsync(state.TextExports.DeathsPath!));
        Assert.Equal(0, raw.PluginObservation!.Total);
    }

    [Fact]
    public Task DirectoryChangeRefreshAndMalformedJournalPreserveFixtureFiles() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var original = new Fixture(true);
        using var replacement = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = original.State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync(); await vm.DiscoverHollowKnightAsync();
        byte[] oldPlugin = await File.ReadAllBytesAsync(original.Plugin);
        var window = new MainWindow((_, _) => replacement.Game, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            Tree(window).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Change Hollow Knight directory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => vm.HollowKnightGameDirectory == replacement.Game && vm.ControlsEnabled);
            Assert.False(vm.IsHollowKnightInstalled);
            Tree(window).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Refresh Hollow Knight directory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            Assert.Equal(oldPlugin, await File.ReadAllBytesAsync(original.Plugin));
            string journal = Path.Combine(replacement.Game, ".soulstracker-death-tracking", "pending.json");
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!); await File.WriteAllTextAsync(journal, "malformed fixture");
            Assert.False(vm.HasHollowKnightPendingRollback);
            Assert.Null(await vm.PrepareHollowKnightInstallAsync());
            Assert.NotNull(vm.ErrorMessage);
            Assert.Equal("malformed fixture", await File.ReadAllTextAsync(journal));
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task OverflowCannotPublishAndDecrementCanRepairTheLocalAddition()
    {
        using var fixture = new Fixture(true);
        var values = new HollowKnightDeathAdjustments([new(fixture.Installation, fixture.Run, long.MaxValue)]);
        var repository = new MemoryRepository(GameId.HollowKnight)
        {
            State = new(1, GameId.HollowKnight, OverlayConfiguration.Default,
            hollowKnight: fixture.State.HollowKnight with { DeathAdjustments = values })
        };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync(); await vm.DiscoverHollowKnightAsync();
        vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 1));
        Assert.False(vm.CanIncrementHollowKnightDeaths);
        Assert.True(vm.CanDecrementHollowKnightDeaths);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
        await vm.DecrementHollowKnightDeathsAsync();
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.TotalDeathsText);
    }

    [Fact]
    public async Task IncompleteTransactionKeepsTheInstallRecoveryActionEvenIfPayloadHashesMatch()
    {
        using var fixture = new Fixture(true);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync(); await vm.DiscoverHollowKnightAsync();
        Assert.True(vm.IsHollowKnightInstalled);
        string journal = Path.Combine(fixture.Game, ".soulstracker-death-tracking", "pending.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        const string incomplete = "{\"BackupDirectory\":\"fixture\",\"Completed\":false,\"Files\":[]}";
        await File.WriteAllTextAsync(journal, incomplete);
        await vm.DiscoverHollowKnightAsync();
        Assert.False(vm.IsHollowKnightInstalled);
        Assert.True(vm.IsHollowKnightInstallVisible);
        Assert.True(vm.HasHollowKnightPendingRollback);
        Assert.Equal(incomplete, await File.ReadAllTextAsync(journal));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.Plugin));
    }

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("bloodborne")]
    [InlineData("sekiro")]
    [InlineData("elden_ring")]
    [InlineData("black_myth_wukong")]
    [InlineData("lies_of_p")]
    [InlineData("hollow_knight")]
    public async Task FirstAutomaticReadLoadsThenShowsGenuineFailure(string id)
    {
        GameId game = GameId.Parse(id);
        var repository = new MemoryRepository(game);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        Assert.Equal("Loading.", vm.TotalDeathsText);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(game == GameId.Ds1 ? GameId.Ds2 : GameId.Ds1));
        Assert.Equal("Loading.", vm.TotalDeathsText);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(game));
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
    }

    [Fact]
    public async Task SwitchingGamesShowsLoadingUntilTheCurrentSourcesFirstRead()
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        Assert.Equal("0", vm.TotalDeathsText);
        foreach (var selected in new[] { GameId.Ds1, GameId.Ds2, GameId.Ds1 })
        {
            await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == selected));
            Assert.Equal("Loading.", vm.TotalDeathsText);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new(selected == GameId.Ds1 ? GameId.Ds2 : GameId.Ds1, 99,
                DateTimeOffset.UtcNow, "wrong source")));
            Assert.Equal("Loading.", vm.TotalDeathsText);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(selected));
            Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
        }
        await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == GameId.DemonsSouls));
        Assert.Equal("0", vm.TotalDeathsText);
        Assert.Equal("Manual", vm.RuntimeReaderStatusText);
    }

    [Fact]
    public async Task FirstCachedReadEndsLoadingWithoutAcceptingOrPublishingTheCachedValue()
    {
        var repository = new MemoryRepository(GameId.Ds1);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var session = new RuntimePublicationSession();
        var state = repository.State;
        int outputs = 0;
        var cached = RuntimeGameReadResult.Cached(RuntimeGameReadResult.Synced(new(GameId.Ds1, 99, DateTimeOffset.UtcNow,
            EffectiveDeathTotalResult.SourceIdentityFor(state))));
        session.CompleteRead(session.BeginRead(state), state, cached, vm.ApplyRuntimeReaderResult, _ => outputs++);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
        Assert.Equal(0, outputs);
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<DependencyObject> Tree(DependencyObject node) => DirectoryPresentationControlTests.Tree(node);
    private static void Render(MainWindow window, string name)
    {
        string? output = Environment.GetEnvironmentVariable("SOULSTRACKER_PRESENTATION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
}
