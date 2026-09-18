using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class OverlayIconModeTests
{
    [Theory]
    [InlineData(OverlayTitleIconMode.Off)]
    [InlineData(OverlayTitleIconMode.PrefixSkull)]
    [InlineData(OverlayTitleIconMode.SkullOnly)]
    public async Task SelectedModeSurvivesApplyAndSqliteReconstruction(OverlayTitleIconMode mode)
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-icon-{Guid.NewGuid():N}");
        try
        {
            await using (var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), new NullPublisher()))
            {
                var vm = new DesktopTrackerViewModel(coordinator);
                await vm.InitializeAsync();
                vm.DraftTitleIconModeChoice = vm.TitleIconModes.Single(choice => choice.Value == mode);
                await vm.ApplyOverlayAppearanceAsync(true);
                Assert.Null(vm.ErrorMessage);
                Assert.Equal(mode, vm.CurrentState!.OverlayConfiguration.TotalDeaths.TitleIconMode);
                Assert.Equal(mode, vm.DraftTitleIconModeChoice.Value);
            }
            await using var reloaded = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), new NullPublisher());
            var restored = new DesktopTrackerViewModel(reloaded);
            await restored.InitializeAsync();
            Assert.Equal(mode, restored.CurrentState!.OverlayConfiguration.TotalDeaths.TitleIconMode);
            Assert.Equal(mode, restored.DraftTitleIconModeChoice.Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeTransitionsAndReapplyPreserveStateAndAcceptedDeathProjection(bool automatic)
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-icon-{Guid.NewGuid():N}");
        try
        {
            var appearance = new OverlayAppearance("Synthetic total", "Arial", 32, "#ABCDEF", "#A78BFA", "#123456",
                45, 6, 4, OverlayTextAlignment.Left, true, "#010203", 2, true, "#040506", 3, -2, 5, 80, "#FEDCBA");
            var seed = new PersistentTrackerState(1, automatic ? GameId.EldenRing : GameId.DemonsSouls,
                new OverlayConfiguration(1, new TotalDeathsOverlayOptions(true, false, true, appearance, OverlayTitleIconMode.Off)),
                new GlobalHotkeyConfiguration(3, 0x71, 3, 0x72), new TextExportConfiguration(Path.Combine(root, "unused.txt"), true),
                ManualDeathCounter.CreateFor(GameId.DemonsSouls, 7), true,
                new EldenRingSaveConfiguration(Path.Combine(root, "ER0000.sl2"), 1),
                new BlackMythWukongSaveConfiguration(Path.Combine(root, "ArchiveSaveFile.1.sav")),
                new EldenRingMissedDeathAdjustments([new EldenRingMissedDeathAdjustment(Path.Combine(root, "ER0000.sl2"), 1, 3)]),
                new LiesOfPSaveConfiguration(Path.Combine(root, "SaveData-1_Character_1.sav")));
            await using (var repository = new SqliteTrackerStateRepository(root, "tracker.db")) await repository.SaveAsync(seed);
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), new NullPublisher());
            var vm = new DesktopTrackerViewModel(coordinator);
            await vm.InitializeAsync();
            var session = new RuntimePublicationSession();
            var projection = new HostedOverlayProjection();
            session.SelectState(vm.CurrentState!);
            var before = projection.Initialize(vm.CurrentState!);
            RuntimeGameReadResult? accepted = null;
            if (automatic)
            {
                Assert.False(before.HasDeath);
                var read = RuntimeGameReadResult.Synced(new RuntimeGameObservation(seed.SelectedGameId, 12,
                    DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(seed)));
                session.CompleteRead(session.BeginRead(vm.CurrentState!), vm.CurrentState!, read, vm.ApplyRuntimeReaderResult,
                    result => { accepted = result; before = projection.FromAcceptedPublication(vm.CurrentState!, result); });
                Assert.Same(read, accepted);
            }
            Assert.Equal(automatic ? "15" : "7", vm.TotalDeathsText);
            string desktopTotal = vm.TotalDeathsText;
            string? readerStatus = vm.RuntimeReaderStatusText;
            var effective = EffectiveDeathTotalResult.Resolve(vm.CurrentState!, accepted?.Observation);
            long generation = session.BeginRead(vm.CurrentState!).Generation;
            foreach (var (mode, wire) in new[] { (OverlayTitleIconMode.Off, "off"), (OverlayTitleIconMode.PrefixSkull, "prefixSkull"),
                (OverlayTitleIconMode.SkullOnly, "skullOnly"), (OverlayTitleIconMode.Off, "off"), (OverlayTitleIconMode.Off, "off") })
            {
                vm.DraftTitleIconModeChoice = vm.TitleIconModes.Single(choice => choice.Value == mode);
                await vm.ApplyOverlayAppearanceAsync(true);
                Assert.Null(vm.ErrorMessage);
                Assert.Equal(mode, vm.CurrentState!.OverlayConfiguration.TotalDeaths.TitleIconMode);
                Assert.Equal(mode, vm.DraftTitleIconModeChoice.Value);
                Assert.Equal(UnrelatedState(seed), UnrelatedState(vm.CurrentState!));
                Assert.Equal(desktopTotal, vm.TotalDeathsText);
                Assert.Equal(readerStatus, vm.RuntimeReaderStatusText);
                var afterEffective = EffectiveDeathTotalResult.Resolve(vm.CurrentState!, accepted?.Observation);
                Assert.Equal(effective.EffectiveDisplayedTotal, afterEffective.EffectiveDisplayedTotal);
                Assert.Equal(effective.Status, afterEffective.Status);
                session.SelectState(vm.CurrentState!);
                Assert.Equal(generation, session.BeginRead(vm.CurrentState!).Generation);
                var after = projection.FromAcceptedPublication(session.CurrentState!, accepted);
                Assert.Equal(wire, after.Appearance!.TitleIconMode);
                Assert.Equal(before.HasDeath, after.HasDeath);
                Assert.Equal(before.Death, after.Death);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BoundSelectorAndApplyButtonRetainModeAfterWindowAndDatabaseReload() => await HostedConnectionTests.OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), $"souls-icon-{Guid.NewGuid():N}");
        try
        {
            foreach (var mode in new[] { OverlayTitleIconMode.Off, OverlayTitleIconMode.PrefixSkull, OverlayTitleIconMode.SkullOnly })
            {
                await using (var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), new NullPublisher()))
                {
                    var vm = new DesktopTrackerViewModel(coordinator);
                    await vm.InitializeAsync();
                    var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
                    try
                    {
                        window.Show();
                        ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                        await Idle();
                        var selector = Tree(window).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "Total Deaths title icon");
                        var apply = Tree(window).OfType<Button>().Single(control => Equals(control.Content, "Apply Total Deaths appearance"));
                        Assert.True(selector.IsEnabled && selector.Focusable && selector.IsTabStop);
                        Assert.True(apply.IsEnabled && apply.Focusable && apply.IsTabStop);
                        selector.SelectedItem = vm.TitleIconModes.Single(choice => choice.Value == mode);
                        await Idle();
                        Assert.Equal(mode, vm.DraftTitleIconModeChoice.Value);
                        apply.BringIntoView();
                        await Idle();
                        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsBusy && vm.TotalDeathsAppearanceStatus == "Total Deaths appearance applied.");
                        await Idle();
                        Assert.Null(vm.ErrorMessage);
                        Assert.Equal(mode, vm.CurrentState!.OverlayConfiguration.TotalDeaths.TitleIconMode);
                        Assert.Same(vm.DraftTitleIconModeChoice, selector.SelectedItem);
                    }
                    finally { window.Close(); }
                }
                await using var reloaded = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), new NullPublisher());
                var restored = new DesktopTrackerViewModel(reloaded);
                await restored.InitializeAsync();
                Assert.Equal(mode, restored.CurrentState!.OverlayConfiguration.TotalDeaths.TitleIconMode);
                Assert.Equal(mode, restored.DraftTitleIconModeChoice.Value);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    // Compare every serialized state property except the deliberately changed mode,
    // including appearance, source selections, counter, adjustments, hotkeys and exports.
    private static string UnrelatedState(PersistentTrackerState state)
    {
        JsonNode node = JsonSerializer.SerializeToNode(state)!;
        node["OverlayConfiguration"]!["TotalDeaths"]!["TitleIconMode"] = 0;
        return node.ToJsonString();
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
