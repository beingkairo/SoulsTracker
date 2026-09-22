using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearancePopupFocusTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ManualHelpUsesExistingFocusTooltip(bool manual) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(manual ? GameId.DemonsSouls : GameId.Ds3);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var help = Assert.IsType<Button>(window.FindName("ManualCounterHelpButton"));
            Assert.Equal(manual, help.IsVisible);
            Assert.Same(window.FindResource("ContextHelpButton"), help.Style);
            const string text = "Customizable hotkeys for increasing and decreasing the count can be found in the Settings tab.";
            Assert.Equal(text, System.Windows.Automation.AutomationProperties.GetHelpText(help));
            if (manual)
            {
                help.BringIntoView(); help.Focus(); await Idle();
                Assert.True(help.IsKeyboardFocused);
                Assert.True(Assert.IsType<ToolTip>(help.ToolTip).IsOpen);
                Assert.Empty(repository.Saves);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Escape)]
    public Task EldenRingOpensNeutralAndKeepsKeyboardSemantics(Key dismissal) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls) { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, eldenRingNoticeAcknowledged: false) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show();
            var selector = (ComboBox)window.FindName("GameSelector");
            selector.SelectedItem = vm.GameChoices.Single(x => x.GameId == GameId.EldenRing);
            await Idle();
            Assert.True(vm.IsEldenRingNoticeVisible);
            var dialog = (Border)window.FindName("EldenRingNoticeDialog");
            Assert.True(dialog.IsKeyboardFocused);
            Assert.DoesNotContain(Tree(dialog).OfType<Button>(), x => x.IsKeyboardFocused || x.IsDefault);
            AppearanceGeometryTests.Capture(window, $"elden-neutral-{dismissal}");
            dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert.True(((Button)window.FindName("EldenRingNoticeCancelButton")).IsKeyboardFocused);
            selector.Focus(); await Idle();
            Assert.True(dialog.IsKeyboardFocused);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, dismissal) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsEldenRingNoticeVisible); await Idle();
            Assert.Equal(dismissal == Key.Enter, repository.State.EldenRingNoticeAcknowledged);
            if (dismissal == Key.Escape) Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
