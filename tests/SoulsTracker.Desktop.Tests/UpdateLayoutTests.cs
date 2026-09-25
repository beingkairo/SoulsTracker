using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class UpdateLayoutTests
{
    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public Task StatusAndActionsKeepTheirLayoutDuringDelayedCheck(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var checker = new UpdateSessionTests.ControlledChecker();
        await using var coordinator = new SerializedTrackerCoordinator(new UpdateSessionTests.Repository(), new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "1.0.0");
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
            await Idle();
            var summary = (FrameworkElement)window.FindName("UpdateVersionSummary");
            var status = (TextBlock)window.FindName("UpdateCheckStatusTextBlock");
            var check = (Button)window.FindName("CheckForUpdatesButton");
            var retry = (Button)window.FindName("RetryUpdateCheckButton");
            var release = (Button)window.FindName("OpenUpdateReleasePageButton");
            Assert.True(summary.IsVisible);
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-idle");
            double initialHeight = summary.ActualHeight;
            double actionY = check.TranslatePoint(new Point(), summary).Y;
            ((IInvokeProvider)new ButtonAutomationPeer(check).GetPattern(PatternInterface.Invoke)).Invoke();
            await Idle();
            Task pending = vm.CheckForUpdatesAsync();
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-checking");
            Assert.Equal(initialHeight, summary.ActualHeight);
            Assert.Equal(actionY, check.TranslatePoint(new Point(), summary).Y);
            Assert.True(retry.IsVisible);
            Assert.True(release.IsVisible);
            Assert.False(check.IsEnabled);
            var label = (TextBlock)window.FindName("UpdateStatusLabel");
            Assert.InRange(Math.Abs(label.TranslatePoint(new Point(), summary).Y - status.TranslatePoint(new Point(), summary).Y), 0, 1);
            Assert.True(status.TranslatePoint(new Point(), summary).X > label.TranslatePoint(new Point(), summary).X);
            checker.Result.SetResult(new(ManualReleaseUpdateStatus.InvalidResponse));
            await pending;
            await Idle();
            Assert.Equal(initialHeight, summary.ActualHeight);
            Assert.Equal(actionY, check.TranslatePoint(new Point(), summary).Y);
            Assert.True(retry.IsEnabled);
            retry.BringIntoView();
            await Idle();
            Assert.True(retry.Focus());
            Assert.True(retry.IsKeyboardFocused);
            Assert.True(retry.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.True(release.IsKeyboardFocused);
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-error-focus");
            var measured = new TextBlock { Text = status.Text, FontFamily = status.FontFamily, FontSize = status.FontSize, TextWrapping = TextWrapping.Wrap };
            measured.Measure(new Size(status.ActualWidth, double.PositiveInfinity));
            Assert.True(status.ActualHeight >= measured.DesiredSize.Height - 1);
        }
        finally { await vm.StopUpdateChecksAsync(); window.Close(); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public Task NewerNoticeAndPreferenceAreReachableAndBoundToCommittedState(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new UpdateSessionTests.Repository();
        var checker = new UpdateSessionTests.ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
        var launcher = new RecordingLauncher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, updateReleasePageLauncher: launcher);
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
            await vm.CheckForUpdatesAsync();
            await Idle();
            var view = (Button)window.FindName("OpenUpdateProductPageButton");
            Assert.True(view.IsVisible);
            Assert.True(view.Focus());
            ((IInvokeProvider)new ButtonAutomationPeer(view).GetPattern(PatternInterface.Invoke)).Invoke();
            await Idle();
            Assert.Equal(new Uri("https://beingkairo.com/tools/souls-tracker/"), launcher.Opened);
            Assert.True(view.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            var dismiss = (Button)window.FindName("DismissUpdateNoticeButton");
            Assert.True(dismiss.IsKeyboardFocused);
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-newer");
            ((IInvokeProvider)new ButtonAutomationPeer(dismiss).GetPattern(PatternInterface.Invoke)).Invoke();
            await Idle();
            Assert.False(view.IsVisible);
            var toggle = (CheckBox)window.FindName("CheckForUpdatesOnStartupCheckBox");
            toggle.BringIntoView();
            await Idle();
            Assert.True(toggle.Focus());
            toggle.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Idle();
            Assert.True(repository.State.CheckForUpdatesOnStartup);
            Assert.True(vm.CheckForUpdatesOnStartup);
            repository.FailSave = true;
            toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Idle();
            Assert.True(toggle.IsChecked);
            Assert.NotNull(vm.ErrorMessage);
            var feedback = (TextBlock)window.FindName("UpdatePreferenceStatusTextBlock");
            Assert.Contains("could not be saved", feedback.Text, StringComparison.Ordinal);
            feedback.BringIntoView();
            await Idle();
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-save-failure");
        }
        finally { window.Close(); }
    });

    private sealed class RecordingLauncher : IUpdateReleasePageLauncher
    {
        public Uri? Opened { get; private set; }
        public bool TryOpen(Uri releasePage) { Opened = releasePage; return true; }
    }

    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
