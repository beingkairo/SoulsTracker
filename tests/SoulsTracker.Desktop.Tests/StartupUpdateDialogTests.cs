using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class StartupUpdateDialogTests
{
    [Theory]
    [InlineData("escape")]
    [InlineData("close")]
    [InlineData("shutdown")]
    [InlineData("before-presentation")]
    public Task ModalDismissalAndShutdownLeaveNoPendingPresentation(string action) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var checker = new UpdateSessionTests.ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "1.3.2"));
        await using var coordinator = new SerializedTrackerCoordinator(new UpdateSessionTests.Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true) }, new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "1.3.1");
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show();
            await Idle();
            vm.StartStartupUpdateCheck();
            if (action == "before-presentation") await vm.StopUpdateChecksAsync();
            else
            {
                var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = window.Dispatcher.BeginInvoke(async () =>
                {
                    Window? dialog = window.OwnedWindows.Cast<Window>().SingleOrDefault();
                    try
                    {
                        Assert.NotNull(dialog);
                        if (action == "escape")
                        {
                            var scope = PresentationSource.FromVisual(dialog);
                            Assert.True(AccessKeyManager.IsKeyRegistered(scope, "\x1b"));
                            AccessKeyManager.ProcessKey(scope, "\x1b", false);
                        }
                        else if (action == "close") dialog.Close();
                        else await vm.StopUpdateChecksAsync();
                        await Idle();
                        Assert.False(dialog.IsVisible);
                        dismissed.TrySetResult();
                    }
                    catch (Exception error) { dismissed.TrySetException(error); }
                    finally { dialog?.Close(); }
                }, DispatcherPriority.ApplicationIdle);
                await dismissed.Task;
            }
            await Idle();
            Assert.False(vm.IsUpdateNoticeVisible);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            vm.StartStartupUpdateCheck();
            await vm.CheckForUpdatesAsync();
            await Idle();
            Assert.False(vm.IsUpdateNoticeVisible);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
        }
        finally { await vm.StopUpdateChecksAsync(); window.Close(); }
    });

    [Fact]
    public Task SettingsLaunchFailureIsSeparateReadableFeedback() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var checker = new UpdateSessionTests.ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "1.3.2"));
        await using var coordinator = new SerializedTrackerCoordinator(new UpdateSessionTests.Repository(), new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, updateReleasePageLauncher: new Launcher());
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
            await vm.CheckForUpdatesAsync();
            await Idle();
            var before = (vm.UpdateCurrentVersion, vm.UpdateLatestVersion, vm.UpdateCheckStatus);
            var open = (Button)window.FindName("OpenUpdateReleasePageButton");
            ((IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
            await Idle();
            Assert.Equal(before, (vm.UpdateCurrentVersion, vm.UpdateLatestVersion, vm.UpdateCheckStatus));
            var error = Assert.IsType<TextBlock>(window.FindName("UpdatePageActionErrorTextBlock"));
            error.BringIntoView();
            await Idle();
            Assert.True(error.IsVisible);
            Assert.Contains("could not be opened", error.Text, StringComparison.Ordinal);
            Assert.Same(window.FindResource("DangerBrush"), error.Foreground);
            var measure = new TextBlock { Text = error.Text, FontFamily = error.FontFamily, FontSize = error.FontSize, TextWrapping = TextWrapping.Wrap };
            measure.Measure(new Size(error.ActualWidth, double.PositiveInfinity));
            Assert.True(error.ActualHeight >= measure.DesiredSize.Height - 1);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            AppearanceGeometryTests.Capture(window, "settings-update-page-failure");
        }
        finally { await vm.StopUpdateChecksAsync(); window.Close(); }
    });

    [Theory]
    [InlineData(560d, 400d, false)]
    [InlineData(560d, 760d, true)]
    [InlineData(1060d, 760d, false)]
    public Task StartupShowsOwnedModalWithExplicitActionAndSessionDismissal(double width, double height, bool launchFails) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new UpdateSessionTests.Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true) };
        var checker = new UpdateSessionTests.ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "1.3.2"));
        var launcher = new Launcher { Succeeds = !launchFails };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "1.3.1", updateReleasePageLauncher: launcher);
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
            await Idle();
            var check = (Button)window.FindName("CheckForUpdatesButton");
            check.BringIntoView();
            await Idle();
            Assert.True(check.Focus());
            vm.StartStartupUpdateCheck();
            Assert.Empty(window.OwnedWindows.Cast<Window>()); // ShowDialog must be deferred outside the check/lock.
            var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = window.Dispatcher.BeginInvoke(() =>
            {
                Window? dialog = window.OwnedWindows.Cast<Window>().SingleOrDefault();
                try
                {
                    Assert.NotNull(dialog);
                    Assert.Equal("Update available", dialog.Title);
                    Assert.Same(window, dialog.Owner);
                    Assert.Same(window.Resources, dialog.Resources);
                    Assert.False(dialog.ShowInTaskbar);
                    Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
                    Assert.True(dialog.IsVisible);
                    Assert.False(vm.IsCheckingForUpdates);
                    Assert.Null(window.FindName("UpdateNotice"));
                    Assert.Null(launcher.Opened);
                    var body = (TextBlock)dialog.FindName("UpdateNoticeBody");
                    Assert.Contains("1.3.2", body.Text, StringComparison.Ordinal);
                    Assert.Contains("1.3.1", body.Text, StringComparison.Ordinal);
                    var content = (StackPanel)dialog.Content;
                    Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(content));
                    Assert.True(content.IsKeyboardFocused);
                    Assert.True(content.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                    var open = (Button)dialog.FindName("OpenUpdatePageButton");
                    Assert.True(open.IsKeyboardFocused);
                    var before = (vm.UpdateCurrentVersion, vm.UpdateLatestVersion, vm.UpdateCheckStatus);
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(new Uri("https://beingkairo.com/tools/souls-tracker/"), launcher.Opened);
                    Assert.Equal(before, (vm.UpdateCurrentVersion, vm.UpdateLatestVersion, vm.UpdateCheckStatus));
                    Assert.True(dialog.IsVisible);
                    var error = (TextBlock)dialog.FindName("UpdatePageActionError");
                    Assert.Equal(launchFails, error.Visibility == Visibility.Visible);
                    Assert.Empty(vm.UpdatePageActionError);
                    Assert.True(open.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                    var dismiss = (Button)dialog.FindName("DismissUpdateButton");
                    Assert.True(dismiss.IsKeyboardFocused);
                    Assert.True(dismiss.IsCancel);
                    Assert.Equal("Dismiss", new ButtonAutomationPeer(dismiss).GetName());
                    AppearanceGeometryTests.Capture(dialog, $"startup-modal-{width}-{height}-{launchFails}");
                    dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    inspected.TrySetResult();
                }
                catch (Exception error) { inspected.TrySetException(error); }
                finally { dialog?.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            await inspected.Task;
            await Idle();
            Assert.False(vm.IsUpdateNoticeVisible);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            Assert.True(check.IsKeyboardFocused);
            vm.StartStartupUpdateCheck();
            await vm.CheckForUpdatesAsync();
            await Idle();
            Assert.False(vm.IsUpdateNoticeVisible);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
        }
        finally { await vm.StopUpdateChecksAsync(); window.Close(); }
    });

    private sealed class Launcher : IUpdateReleasePageLauncher
    {
        public bool Succeeds { get; init; }
        public Uri? Opened { get; private set; }
        public bool TryOpen(Uri releasePage) { Opened = releasePage; return Succeeds; }
    }

    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
