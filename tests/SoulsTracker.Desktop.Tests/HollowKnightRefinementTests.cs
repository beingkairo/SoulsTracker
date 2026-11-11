using System.ComponentModel;
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

    [Fact]
    public Task HollowKnightSetupStatesUseTheSharedReaderStatusPanel() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight)
        {
            State = new(1, GameId.HollowKnight, OverlayConfiguration.Default)
        };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.HollowKnight));
        var discoveryResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.HollowKnightDirectoryDiscovery = () => discoveryResult.Task;
        Task discovery = vm.DiscoverHollowKnightAsync();
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, Width = 800, Height = 700, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var statusPanel = (Border)window.FindName("ReaderStatusPanel");
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            var totalPanel = (Border)window.FindName("DeathCounterPanel");
            Assert.True(statusPanel.IsVisible);
            Assert.True(totalPanel.IsVisible);
            Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => AutomationProperties.GetName(x) == "Hollow Knight directory setup guidance");
            Assert.Equal("Looking for Hollow Knight...", status.Text);
            Assert.Equal((Brush)window.FindResource("WaitingBrush"), status.Foreground);
            Render(window, "parity-hollow-knight-loading");
            discoveryResult.SetResult(null);
            await discovery;
            await HostedDesktopPublisherTests.WaitUntil(() => vm.HollowKnightDirectoryFeedback is not null);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.HollowKnight));
            await Idle();
            Assert.Equal("Hollow Knight wasn't found automatically. Click Choose Directory to select the game folder.", status.Text);
            Assert.Same(statusPanel, status.Parent is StackPanel stack ? stack.Parent : null);

            Assert.Null(await vm.PrepareHollowKnightInstallAsync());
            await Idle();
            Assert.Equal("Choose the Hollow Knight game folder before installing death tracking.", status.Text);

            await vm.DiscoverHollowKnightAsync();
            await Idle();
            Assert.Equal("Hollow Knight wasn't found automatically. Click Choose Directory to select the game folder.", status.Text);

            await vm.SetHollowKnightGameDirectoryAsync(Path.Combine(fixture.Root, "invalid"));
            await Idle();
            Assert.Equal("That folder couldn't be selected. Choose the Hollow Knight game folder and try again.", status.Text);

            repository.FailSave = true;
            Assert.False(await vm.SetHollowKnightGameDirectoryAsync(fixture.Game));
            await Idle();
            Assert.Equal("The selected folder couldn't be saved. Please try again.", status.Text);
            repository.FailSave = false;

            var notifications = new List<string?>();
            vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            await vm.SetHollowKnightGameDirectoryAsync(fixture.Game);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.HollowKnight));
            await Idle();
            await HostedDesktopPublisherTests.WaitUntil(() => status.Text == "Death tracking is not installed.");
            Assert.Null(vm.HollowKnightDirectoryFeedback);
            Assert.Null(vm.ErrorMessage);
            Assert.Equal("Death tracking is not installed.", status.Text);
            Assert.Contains(nameof(DesktopTrackerViewModel.HollowKnightDirectoryFeedback), notifications);
            await vm.DiscoverHollowKnightAsync(); await Idle();
            Assert.Null(vm.HollowKnightDirectoryFeedback);
            Assert.Equal("Death tracking is not installed.", status.Text);
            Assert.DoesNotContain("verified", status.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SuccessfulAutomaticDiscoveryShowsDirectoryWithoutSuccessBanner() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight)
        {
            State = new(1, GameId.HollowKnight, OverlayConfiguration.Default)
        };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.HollowKnight));
        vm.HollowKnightDirectoryDiscovery = () => Task.FromResult<string?>(fixture.Game);
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            await vm.DiscoverHollowKnightAsync(); await Idle();
            Assert.Equal(fixture.Game, vm.HollowKnightGameDirectory);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.HollowKnight));
            await Idle();
            Assert.True(vm.HollowKnightDirectoryWasAutomatic);
            Assert.Null(vm.HollowKnightDirectoryFeedback);
            Assert.Equal("Death tracking is not installed.", status.Text);
            Assert.DoesNotContain("verified", status.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(fixture.Game, vm.HollowKnightDirectoryPath);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public Task HollowKnightAndEldenRingShareMainPageStructure(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new Fixture(true);
        var repository = new MemoryRepository(GameId.HollowKnight)
        {
            State = new(1, GameId.HollowKnight, OverlayConfiguration.Default)
        };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.HollowKnight));
        vm.HollowKnightDirectoryDiscovery = () => Task.FromResult<string?>(null);
        await vm.DiscoverHollowKnightAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var sessionPanel = (Border)window.FindName("GameSessionPanel");
            var statusPanel = (Border)window.FindName("ReaderStatusPanel");
            var totalPanel = (Border)window.FindName("DeathCounterPanel");
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            Assert.True(statusPanel.IsVisible);
            Assert.True(totalPanel.IsVisible);
            Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, total.Text);
            await HostedDesktopPublisherTests.WaitUntil(() => status.Text == "Hollow Knight wasn't found automatically. Click Choose Directory to select the game folder.");
            Assert.Equal("Hollow Knight wasn't found automatically. Click Choose Directory to select the game folder.", status.Text);
            Assert.Equal((Brush)window.FindResource("ElevatedSurfaceBrush"), statusPanel.Background);
            Assert.Equal(new Thickness(10, 8, 10, 8), statusPanel.Padding);
            Assert.Equal(4d, statusPanel.CornerRadius.TopLeft);
            Assert.Equal("Choose Directory", Tree(window).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight game directory").Content);
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.Text == "DEATH TRACKER");
            Render(window, $"parity-hollow-knight-uninstalled-{width}x{height}");
            var hollowKnightBounds = MainBounds(window);

            var erRepository = new MemoryRepository(GameId.EldenRing)
            {
                State = new(1, GameId.EldenRing, OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true)
            };
            await using var erCoordinator = new SerializedTrackerCoordinator(erRepository, new NullPublisher());
            await using var erVm = CreateViewModel(erCoordinator);
            await erVm.InitializeAsync();
            erVm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.EldenRing));
            var erWindow = new MainWindow { DataContext = erVm, Width = width, Height = height, ShowInTaskbar = false };
            MainLayoutBounds eldenRingBounds;
            try
            {
                erWindow.Show(); await Idle();
                var erStatusPanel = (Border)erWindow.FindName("ReaderStatusPanel");
                var erTotalPanel = (Border)erWindow.FindName("DeathCounterPanel");
                Assert.True(erStatusPanel.IsVisible);
                Assert.True(erTotalPanel.IsVisible);
                Assert.Equal("ELDEN RING SAVE", Tree(erWindow).OfType<TextBlock>().Single(x => x.Text == "ELDEN RING SAVE").Text);
                Assert.Equal((Brush)erWindow.FindResource("ElevatedSurfaceBrush"), erStatusPanel.Background);
                Assert.Equal(new Thickness(10, 8, 10, 8), erStatusPanel.Padding);
                Render(erWindow, $"parity-elden-ring-{width}x{height}");
                eldenRingBounds = MainBounds(erWindow);
            }
            finally { erWindow.Close(); }

            AssertClose(hollowKnightBounds.Session.X, eldenRingBounds.Session.X, 1, "Game Session left edge");
            AssertClose(hollowKnightBounds.Session.Y, eldenRingBounds.Session.Y, 1, "Game Session top edge");
            AssertClose(hollowKnightBounds.Session.Width, eldenRingBounds.Session.Width, 1, "Game Session width");
            AssertClose(hollowKnightBounds.Session.Height, eldenRingBounds.Session.Height, 24, "Game Session height");
            AssertClose(hollowKnightBounds.Status.X, eldenRingBounds.Status.X, 1, "Reader Status left edge");
            AssertClose(hollowKnightBounds.Status.Width, eldenRingBounds.Status.Width, 1, "Reader Status width");
            AssertClose(hollowKnightBounds.Status.Y, eldenRingBounds.Status.Y, 24, "Reader Status vertical position");
            AssertClose(hollowKnightBounds.Total.X, eldenRingBounds.Total.X, 1, "Total Deaths left edge");
            AssertClose(hollowKnightBounds.Total.Width, eldenRingBounds.Total.Width, 1, "Total Deaths width");
            AssertClose(hollowKnightBounds.Total.Y, eldenRingBounds.Total.Y, 24, "Total Deaths vertical position");

            Assert.True(await vm.SetHollowKnightGameDirectoryAsync(fixture.Game));
            vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 12));
            await Idle();
            Assert.True(vm.IsHollowKnightInstalled);
            Assert.True(statusPanel.IsVisible);
            Assert.True(totalPanel.IsVisible);
            Assert.Equal("Synced", status.Text);
            Assert.Equal("12", total.Text);
            Assert.Contains(Tree(window).OfType<Button>(), x => x.IsVisible && AutomationProperties.GetName(x) == "Add Hollow Knight death");
            Render(window, $"parity-hollow-knight-installed-{width}x{height}");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task HollowKnightCounterCommitsKeepUnrelatedControlsStable() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new Fixture(true);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await vm.DiscoverHollowKnightAsync();
        vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 0));
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var increment = Tree(window).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Add Hollow Knight death");
            var decrement = Tree(window).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Remove Hollow Knight added death");
            var gameSelector = (ComboBox)window.FindName("GameSelector");
            var directoryButton = Tree(window).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight game directory");
            var refreshButton = Tree(window).OfType<Button>().Single(x => Equals(x.Content, "Refresh") && x.IsVisible);
            var saved = Tree(window).OfType<TextBlock>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight saved deaths");
            var added = Tree(window).OfType<TextBlock>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight added deaths");
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            Assert.Equal("0", total.Text); Assert.Equal("0", saved.Text); Assert.Equal("0", added.Text);
            Assert.False(decrement.IsEnabled);
            Assert.True(increment.Focus());

            var properties = new List<string?>();
            var controlChanges = new List<string>();
            vm.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
            gameSelector.IsEnabledChanged += (_, _) => controlChanges.Add("game enabled");
            directoryButton.IsEnabledChanged += (_, _) => controlChanges.Add("directory enabled");
            refreshButton.IsEnabledChanged += (_, _) => controlChanges.Add("refresh enabled");
            increment.IsEnabledChanged += (_, _) => controlChanges.Add("increment enabled");
            increment.IsVisibleChanged += (_, _) => controlChanges.Add("increment visible");
            increment.DataContextChanged += (_, _) => controlChanges.Add("increment context");

            await vm.IncrementHollowKnightDeathsAsync(); await Idle();
            Assert.Equal("1", total.Text); Assert.Equal("0", saved.Text); Assert.Equal("1", added.Text);
            Assert.True(decrement.IsEnabled);
            Assert.Same(increment, System.Windows.Input.Keyboard.FocusedElement);
            Assert.True(gameSelector.IsEnabled && directoryButton.IsEnabled && refreshButton.IsEnabled && increment.IsEnabled && increment.IsVisible);
            Assert.Empty(controlChanges);
            Assert.DoesNotContain(nameof(vm.IsBusy), properties);
            Assert.DoesNotContain(nameof(vm.ControlsEnabled), properties);
            Assert.DoesNotContain(nameof(vm.SelectedGame), properties);
            Assert.DoesNotContain(nameof(vm.RuntimeReaderStatusText), properties);
            Assert.Equal(new[] { nameof(vm.TotalDeathsText), nameof(vm.IsTotalDeathsValueNumeric), nameof(vm.HollowKnightAddedDeaths), nameof(vm.CanDecrementHollowKnightDeaths) }, properties);

            properties.Clear();
            await vm.DecrementHollowKnightDeathsAsync(); await Idle();
            Assert.Equal("0", total.Text); Assert.Equal("0", saved.Text); Assert.Equal("0", added.Text);
            Assert.False(decrement.IsEnabled);
            Assert.Empty(controlChanges);
            Assert.DoesNotContain(nameof(vm.IsBusy), properties);
            Assert.DoesNotContain(nameof(vm.ControlsEnabled), properties);
            Assert.Equal(new[] { nameof(vm.TotalDeathsText), nameof(vm.IsTotalDeathsValueNumeric), nameof(vm.HollowKnightAddedDeaths), nameof(vm.CanDecrementHollowKnightDeaths) }, properties);
            Assert.Equal(0, repository.State.HollowKnight.DeathAdjustments.Get(fixture.Installation, fixture.Run));

            repository.FailSave = true;
            properties.Clear();
            controlChanges.Clear();
            await vm.IncrementHollowKnightDeathsAsync(); await Idle();
            Assert.Equal("0", total.Text); Assert.Equal("0", added.Text);
            Assert.Equal(0, repository.State.HollowKnight.DeathAdjustments.Get(fixture.Installation, fixture.Run));
            Assert.False(vm.IsBusy);
            Assert.NotNull(vm.ErrorMessage);
            Assert.Empty(controlChanges);
            Assert.Equal(new[] { nameof(vm.ErrorMessage) }, properties);
            repository.FailSave = false;

            properties.Clear();
            await Task.WhenAll(vm.IncrementHollowKnightDeathsAsync(), vm.IncrementHollowKnightDeathsAsync());
            await Idle();
            Assert.Equal("2", total.Text); Assert.Equal("2", added.Text);
            Assert.Equal(2, repository.State.HollowKnight.DeathAdjustments.Get(fixture.Installation, fixture.Run));
            Assert.Same(increment, System.Windows.Input.Keyboard.FocusedElement);
            await Task.WhenAll(vm.DecrementHollowKnightDeathsAsync(), vm.DecrementHollowKnightDeathsAsync());
            await Idle();
            Assert.Equal("0", total.Text); Assert.Equal("0", added.Text);
            Assert.Equal(0, repository.State.HollowKnight.DeathAdjustments.Get(fixture.Installation, fixture.Run));
            Assert.Empty(controlChanges);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task UninstallRetainsHistoricalTotalAcrossPollingRefreshAndReinstall() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var text = new TextExportStatePublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, text);
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await vm.DiscoverHollowKnightAsync();
        await InstallFixtureTrackingAsync(vm);
        var observation = fixture.Read(repository.State, 12);
        vm.ApplyRuntimeReaderResult(observation);
        text.PublishRuntimeObservation(repository.State, observation);

        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var status = (TextBlock)window.FindName("RuntimeReaderStatusTextBlock");
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            var historical = (TextBlock)window.FindName("HollowKnightHistoricalTotalLabel");
            Assert.True(vm.IsHollowKnightInstalled);
            Assert.Equal("Synced", status.Text);
            Assert.Equal("12", total.Text);
            Assert.False(historical.IsVisible);

            await vm.IncrementHollowKnightDeathsAsync();
            vm.ApplyRuntimeReaderResult(observation);
            text.PublishRuntimeObservation(repository.State, observation);
            Assert.Equal("13", total.Text);
            Assert.Equal("13", new HostedOverlayProjection().FromAcceptedPublication(repository.State, observation).Death!.Value);

            await vm.UninstallHollowKnightAsync();
            await Idle();
            Assert.False(vm.IsHollowKnightInstalled);
            Assert.Equal("Death tracking is not installed.", status.Text);
            Assert.Equal("13", total.Text);
            Assert.True(historical.IsVisible);
            Assert.Equal("Last recorded total", historical.Text);
            Assert.Equal("Total Deaths: 13", await File.ReadAllTextAsync(repository.State.TextExports.DeathsPath!));
            Assert.Equal("13", new HostedOverlayProjection().FromAcceptedPublication(repository.State, observation).Death!.Value);
            Assert.DoesNotContain(Tree(window).OfType<Button>(), button => button.IsVisible && AutomationProperties.GetName(button) == "Add Hollow Knight death");

            vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 99));
            await Idle();
            Assert.Equal("Death tracking is not installed.", status.Text);
            Assert.Equal("13", total.Text);
            Assert.True(historical.IsVisible);

            await vm.DiscoverHollowKnightAsync();
            await Idle();
            Assert.Equal("Death tracking is not installed.", status.Text);
            Assert.Equal("13", total.Text);
            Assert.True(historical.IsVisible);

            HollowKnightInstallPlan? reinstall = await vm.PrepareHollowKnightInstallAsync();
            Assert.NotNull(reinstall);
            await vm.ApplyHollowKnightInstallAsync(reinstall);
            await Idle();
            Assert.True(vm.IsHollowKnightInstalled);
            Assert.False(historical.IsVisible);
            Assert.NotEqual("Synced", status.Text);
            Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, total.Text);

            vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 12));
            await Idle();
            Assert.Equal("Synced", status.Text);
            Assert.Equal("13", total.Text);
            Assert.False(historical.IsVisible);
            Assert.Equal("Total Deaths: 13", await File.ReadAllTextAsync(repository.State.TextExports.DeathsPath!));
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task RestartAfterUninstallRestoresHistoricalPresentationFromReaderResult()
    {
        using var fixture = new Fixture(false);
        await using (var initialRepository = new SqliteTrackerStateRepository(fixture.Root, "tracker.db"))
            await initialRepository.SaveAsync(fixture.State);

        await using (var coordinator = new SerializedTrackerCoordinator(
            new SqliteTrackerStateRepository(fixture.Root, "tracker.db"), new NullPublisher()))
        {
            await using var first = CreateViewModel(coordinator);
            await first.InitializeAsync();
            await first.DiscoverHollowKnightAsync();
            await InstallFixtureTrackingAsync(first);
            first.ApplyRuntimeReaderResult(fixture.Read(first.CurrentState!, 27));
            await first.UninstallHollowKnightAsync();
            Assert.Equal("Death tracking is not installed.", first.RuntimeReaderStatusText);
            Assert.Equal("27", first.TotalDeathsText);
        }

        var restartedRepository = new SqliteTrackerStateRepository(fixture.Root, "tracker.db");
        await using var restartedCoordinator = new SerializedTrackerCoordinator(restartedRepository, new NullPublisher());
        await using var restarted = CreateViewModel(restartedCoordinator);
        await restarted.InitializeAsync();
        await restarted.DiscoverHollowKnightAsync();
        var persisted = (await restartedRepository.LoadAsync()).State!;
        restarted.ApplyRuntimeReaderResult(fixture.Read(persisted, 27));
        Assert.False(restarted.IsHollowKnightInstalled);
        Assert.Equal("Death tracking is not installed.", restarted.RuntimeReaderStatusText);
        Assert.Equal("27", restarted.TotalDeathsText);
        Assert.True(restarted.IsHollowKnightHistoricalTotalVisible);
    }

    [Fact]
    public async Task FailedUninstallDoesNotReportTrackingOffOrDiscardFailureStatus()
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await vm.DiscoverHollowKnightAsync();
        await InstallFixtureTrackingAsync(vm);
        vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 8));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Plugin)!, "ownership.json"), "{}");

        await vm.UninstallHollowKnightAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("couldn't be completed", vm.RuntimeReaderStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Death tracking is not installed.", vm.RuntimeReaderStatusText);
        Assert.NotEqual("Synced", vm.RuntimeReaderStatusText);
        Assert.False(vm.IsHollowKnightHistoricalTotalVisible);
        Assert.True(File.Exists(fixture.Plugin));
    }

    [Fact]
    public async Task UninstallWithoutReliableRecordShowsUnavailableTotalWithoutHistoricalLabel()
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await vm.DiscoverHollowKnightAsync();
        await InstallFixtureTrackingAsync(vm);
        await vm.UninstallHollowKnightAsync();
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.FromHollowKnight(null, "Tracking unavailable"));

        Assert.Equal("Death tracking is not installed.", vm.RuntimeReaderStatusText);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
        Assert.False(vm.IsHollowKnightHistoricalTotalVisible);
    }

    [Fact]
    public Task InstallConfirmationUsesPreparedFileCountAndNoPreservesGameFiles() => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = fixture.State };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var files = Enumerable.Range(0, 7).Select(index => $"synthetic-file-{index}.dll").ToArray();
        var plan = new HollowKnightInstallPlan(fixture.Game, fixture.Installation, false, files,
            new Dictionary<string, byte[]>(), new Dictionary<string, string?>());
        var confirmation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        window.HollowKnightInstallPlanProvider = _ => Task.FromResult<HollowKnightInstallPlan?>(plan);
        window.HollowKnightInstallConfirmation = message =>
        {
            confirmation.TrySetResult(message);
            return MessageBoxResult.No;
        };
        string[] before = Directory.GetFiles(fixture.Game, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixture.Game, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal).ToArray();
        try
        {
            window.Show(); await Idle();
            Tree(window).OfType<Button>().Single(x => Equals(x.Content, "Install Death Tracking")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string message = await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains($"This will write {plan.Files.Count} files to Hollow Knight.", message);
            Assert.Contains("Other installed mods will remain.", message);
            Assert.Contains("Close Hollow Knight before continuing.", message);
            Assert.DoesNotContain("API", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("revision", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gameplay validation", message, StringComparison.OrdinalIgnoreCase);
            string[] after = Directory.GetFiles(fixture.Game, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(fixture.Game, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(before, after);
            Assert.False(Directory.Exists(Path.Combine(fixture.Game, ".soulstracker-death-tracking")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Game, "hollow_knight_Data", "Managed", "Mods")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task ManualSelectionWinsWhenAutomaticDiscoveryIsPending()
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = new(1, GameId.HollowKnight, OverlayConfiguration.Default) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var releaseDiscovery = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.HollowKnightDirectoryDiscovery = () => releaseDiscovery.Task;

        Task automatic = vm.DiscoverHollowKnightAsync();
        Assert.True(await vm.SetHollowKnightGameDirectoryAsync(fixture.Game));
        releaseDiscovery.SetResult(Path.Combine(fixture.Root, "stale-automatic-result"));
        await automatic;

        Assert.Equal(fixture.Game, repository.State.HollowKnight.GameDirectory);
        Assert.Equal(fixture.Game, vm.HollowKnightGameDirectory);
        Assert.False(vm.HollowKnightDirectoryWasAutomatic);
        Assert.Null(vm.HollowKnightDirectoryFeedback);
    }

    [Fact]
    public async Task FreshAutomaticDiscoveryCommitsDirectoryAndSurvivesViewModelRestart()
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = new(1, GameId.HollowKnight, OverlayConfiguration.Default) };
        {
            await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            await using var vm = CreateViewModel(coordinator);
            await vm.InitializeAsync();
            vm.HollowKnightDirectoryDiscovery = () => Task.FromResult<string?>(fixture.Game);
            await vm.DiscoverHollowKnightAsync();
            Assert.Equal(fixture.Game, vm.HollowKnightGameDirectory);
            Assert.True(vm.HollowKnightDirectoryWasAutomatic);
            Assert.Null(vm.HollowKnightDirectoryFeedback);
        }
        {
            await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            await using var restarted = CreateViewModel(coordinator);
            await restarted.InitializeAsync();
            Assert.Equal(fixture.Game, restarted.HollowKnightGameDirectory);
            await restarted.DiscoverHollowKnightAsync();
            Assert.Equal(fixture.Game, restarted.HollowKnightGameDirectory);
            Assert.False(restarted.HollowKnightDirectoryWasAutomatic);
        }
    }

    [Fact]
    public async Task FailedAutomaticDiscoveryDoesNotSetSuccessIndicators()
    {
        var repository = new MemoryRepository(GameId.HollowKnight) { State = new(1, GameId.HollowKnight, OverlayConfiguration.Default) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        vm.HollowKnightDirectoryDiscovery = () => Task.FromResult<string?>(null);

        await vm.DiscoverHollowKnightAsync();

        Assert.Null(vm.HollowKnightGameDirectory);
        Assert.False(vm.HollowKnightDirectoryWasAutomatic);
        Assert.Equal("Hollow Knight wasn't found automatically. Click Choose Directory to select the game folder.", vm.HollowKnightDirectoryFeedback);
        Assert.False(vm.IsHollowKnightInstalled);
    }

    [Fact]
    public async Task RefreshCannotInvalidateAnInFlightManualSelection()
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = new(1, GameId.HollowKnight, OverlayConfiguration.Default) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var inspectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInspection = new TaskCompletionSource<(HollowKnightInstallation? Game, Guid? Id)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int inspectionCount = 0;
        vm.HollowKnightDirectoryInspector = _ =>
        {
            Interlocked.Increment(ref inspectionCount);
            inspectionEntered.TrySetResult();
            return releaseInspection.Task;
        };

        Task<bool> manual = vm.SetHollowKnightGameDirectoryAsync(fixture.Game);
        await inspectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.DiscoverHollowKnightAsync();
        releaseInspection.SetResult((HollowKnightInstallDiscovery.Inspect(fixture.Game), null));

        Assert.True(await manual);
        Assert.Equal(1, inspectionCount);
        Assert.Equal(fixture.Game, vm.HollowKnightGameDirectory);
        Assert.False(vm.HollowKnightDirectoryWasAutomatic);
    }

    [Fact]
    public async Task ConfigurationSaveGateBlocksConflictingSelectionUntilCommit()
    {
        using var fixture = new Fixture(false);
        var repository = new MemoryRepository(GameId.HollowKnight) { State = new(1, GameId.HollowKnight, OverlayConfiguration.Default) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        repository.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.ReleaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> selection = vm.SetHollowKnightGameDirectoryAsync(fixture.Game);
        await repository.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        Assert.False(await vm.SetHollowKnightGameDirectoryAsync(Path.Combine(fixture.Root, "other")));
        await vm.DiscoverHollowKnightAsync();
        Assert.Null(vm.HollowKnightGameDirectory);
        repository.ReleaseSave.TrySetResult();

        Assert.True(await selection);
        Assert.Equal(fixture.Game, vm.HollowKnightGameDirectory);
        Assert.False(vm.HollowKnightDirectoryWasAutomatic);
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
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.IsVisible && x.Text == "HOLLOW KNIGHT GAME");
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.Text == "DEATH TRACKER");
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.Text == "Choose the game currently being tracked.");
            var help = buttons.Single(x => AutomationProperties.GetName(x) == "Game session help");
            Assert.Equal("Choose the game you'd like to track.", Assert.IsType<ToolTip>(help.ToolTip).Content);
            Assert.Equal("Death tracking starts after installation. Earlier deaths aren't counted automatically.",
                AutomationProperties.GetHelpText(buttons.Single(x => AutomationProperties.GetName(x) == "Hollow Knight death tracker help")));
            Assert.Equal("In case you've already started a playthrough you can manually adjust your deaths here and it will count towards your total.",
                AutomationProperties.GetHelpText(buttons.Single(x => AutomationProperties.GetName(x) == "Hollow Knight manual adjustment help")));
            var path = Tree(window).OfType<TextBlock>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight chosen directory");
            Assert.Equal(fixture.Game, path.Text);
            var copy = buttons.Single(x => x.IsVisible && Equals(x.Tag, fixture.Game) && AutomationProperties.GetName(x) == "Copy directory path");
            copy.BringIntoView(); await Idle(); copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(fixture.Game, copied);
            var increment = buttons.Single(x => AutomationProperties.GetName(x) == "Add Hollow Knight death");
            Assert.False(increment.IsEnabled);
            vm.ApplyRuntimeReaderResult(fixture.Read(repository.State, 0)); await Idle();
            Assert.Equal("Synced", vm.RuntimeReaderStatusText);
            Assert.Equal("0", vm.TotalDeathsText);
            Assert.True(increment.IsEnabled);
            int savesBeforeIncrement = repository.Saves.Count;
            repository.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            repository.ReleaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            increment.BringIntoView(); await Idle(); increment.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            await repository.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            // The counter save is pending, but it must not disable unrelated controls.
            Assert.False(vm.IsBusy);
            Assert.True(increment.IsEnabled);
            Assert.True(((ComboBox)window.FindName("GameSelector")).IsEnabled);
            Assert.Equal("0", total.Text);
            Assert.Equal(savesBeforeIncrement, repository.Saves.Count);
            repository.ReleaseSave.TrySetResult();
            await WaitForTextAsync(total, "1");
            await Idle();
            Assert.Equal("1", vm.TotalDeathsText);
            Assert.Equal("1", total.Text);
            Assert.False(vm.IsBusy);
            Assert.True(increment.IsEnabled);
            Assert.Null(vm.ErrorMessage);
            Assert.Equal(1, Assert.Single(repository.Saves.Skip(savesBeforeIncrement)).HollowKnight.DeathAdjustments.Get(fixture.Installation, fixture.Run));
            Assert.Equal(1, vm.HollowKnightAddedDeaths);
            Assert.Equal("0", vm.HollowKnightSavedDeathsText);
            Assert.Equal("0", Tree(window).OfType<TextBlock>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight saved deaths").Text);
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
            Assert.True(((Border)window.FindName("DeathCounterPanel")).IsVisible);
            Render(window, "hollow-knight-unavailable-" + width + "-" + height);
            ((ScrollViewer)window.FindName("MainContentScrollViewer")).ScrollToTop(); await Idle();
            Render(window, "hollow-knight-unavailable-top-" + width + "-" + height);
        }
        finally { repository.ReleaseSave?.TrySetResult(); window.Close(); }
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
    public async Task CommittedHollowKnightAdjustmentPersistsAcrossDesktopRestart()
    {
        using var fixture = new Fixture(true);
        var seed = fixture.State;
        await using (var repository = new SqliteTrackerStateRepository(fixture.Root, "tracker.db"))
            await repository.SaveAsync(seed);

        await using (var coordinator = new SerializedTrackerCoordinator(
            new SqliteTrackerStateRepository(fixture.Root, "tracker.db"), new NullPublisher()))
        {
            await using var first = CreateViewModel(coordinator);
            await first.InitializeAsync();
            await first.DiscoverHollowKnightAsync();
            first.ApplyRuntimeReaderResult(fixture.Read(seed, 6));
            await first.IncrementHollowKnightDeathsAsync();
            Assert.Equal("7", first.TotalDeathsText);
            Assert.Equal(1, first.HollowKnightAddedDeaths);
        }

        var restartedRepository = new SqliteTrackerStateRepository(fixture.Root, "tracker.db");
        await using (var coordinator = new SerializedTrackerCoordinator(restartedRepository, new NullPublisher()))
        {
            await using var restarted = CreateViewModel(coordinator);
            await restarted.InitializeAsync();
            await restarted.DiscoverHollowKnightAsync();
            PersistentTrackerState loaded = (await restartedRepository.LoadAsync()).State!;
            restarted.ApplyRuntimeReaderResult(fixture.Read(loaded, 6));
            Assert.Equal(1, restarted.HollowKnightAddedDeaths);
            Assert.Equal("7", restarted.TotalDeathsText);
            Assert.Equal("6", restarted.HollowKnightSavedDeathsText);
        }
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
            Tree(window).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Hollow Knight game directory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
    public async Task FirstCachedReadKeepsLoadingWithoutAcceptingOrPublishingTheCachedValue()
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
        Assert.Equal("Loading.", vm.TotalDeathsText);
        Assert.Equal(0, outputs);
    }

    [Fact]
    public async Task LiesOfPRealReaderPipelineCompletesUnconfiguredLoadingWithoutAcceptingStaleNumbers()
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string path = CreateSave("lp", fixture.Root);
        byte[] original = await File.ReadAllBytesAsync(path);
        var configured = new PersistentTrackerState(1, GameId.LiesOfP, OverlayConfiguration.Default, liesOfPSave: new(path));
        var unconfigured = new PersistentTrackerState(1, GameId.LiesOfP, OverlayConfiguration.Default);
        var reader = new LiesOfPSaveDeathReader();
        reader.Configure(configured.LiesOfPSave);
        RuntimeGameReadResult numerical = Assert.IsType<RuntimeGameReadResult>(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(7, numerical.Observation!.TotalDeaths.Value);

        foreach (var state in new[] { unconfigured, configured })
        {
            var repository = new MemoryRepository(GameId.LiesOfP) { State = state };
            await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            await using var vm = CreateViewModel(coordinator);
            await vm.InitializeAsync();
            Assert.Equal("Loading.", vm.TotalDeathsText);
            var session = new RuntimePublicationSession();
            var reads = new RuntimeGameReaderCoordinator([reader]);
            reader.Configure(state.LiesOfPSave);
            int desktop = 0, outputs = 0;
            void Desktop(RuntimeGameReadResult? result) { desktop++; vm.ApplyRuntimeReaderResult(result); }
            void Output(RuntimeGameReadResult? result)
            {
                outputs++;
                Assert.Equal(state.LiesOfPSave.LocalPath is null ? null : 7L,
                    EffectiveDeathTotalResult.Resolve(state, result?.Observation).EffectiveDisplayedTotal);
            }
            for (int index = 0; index < 3; index++)
            {
                var ticket = session.BeginRead(state);
                var result = await reads.PollAsync(GameId.LiesOfP, CancellationToken.None);
                session.CompleteRead(ticket, state, result, Desktop, Output);
            }
            Assert.Equal(3, desktop);
            Assert.Equal(3, outputs);
            Assert.Equal(state.LiesOfPSave.LocalPath is null ? DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage : "7", vm.TotalDeathsText);
            Assert.NotEqual("Loading.", vm.RuntimeReaderStatusText);
            if (state.LiesOfPSave.LocalPath is not null) continue;
            // A current ticket cannot authorize a numerical observation from a discarded configuration.
            session.CompleteRead(session.BeginRead(state), state, numerical, Desktop, Output);
            Assert.Equal(3, desktop);
            // Nor can a completion started before configuration removal cross its generation boundary.
            var stale = session.BeginRead(configured);
            session.SelectState(unconfigured);
            session.CompleteRead(stale, configured, numerical, Desktop, Output);
            Assert.Equal(3, outputs);
            session.CompleteRead(session.BeginRead(state), state, RuntimeGameReadResult.Unavailable(GameId.LiesOfP), Desktop, Output);
            Assert.Equal(4, desktop);
            Assert.Equal(4, outputs);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    private static async Task WaitForTextAsync(TextBlock control, string expected)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var property = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        void Changed(object? sender, EventArgs args)
        {
            if (control.Text == expected) completion.TrySetResult();
        }
        property.AddValueChanged(control, Changed);
        try
        {
            Changed(null, EventArgs.Empty);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { property.RemoveValueChanged(control, Changed); }
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task InstallFixtureTrackingAsync(DesktopTrackerViewModel vm)
    {
        HollowKnightInstallPlan? plan = await vm.PrepareHollowKnightInstallAsync();
        Assert.NotNull(plan);
        await vm.ApplyHollowKnightInstallAsync(plan);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsHollowKnightInstalled);
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject node) => DirectoryPresentationControlTests.Tree(node);
    private sealed record MainLayoutBounds(Rect Session, Rect Status, Rect Total);
    private static MainLayoutBounds MainBounds(MainWindow window) => new(
        Bounds((FrameworkElement)window.FindName("GameSessionPanel"), window),
        Bounds((FrameworkElement)window.FindName("ReaderStatusPanel"), window),
        Bounds((FrameworkElement)window.FindName("DeathCounterPanel"), window));
    private static Rect Bounds(FrameworkElement element, Window window) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static void AssertClose(double expected, double actual, double tolerance, string label) =>
        Assert.InRange(Math.Abs(expected - actual), 0, tolerance);
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
