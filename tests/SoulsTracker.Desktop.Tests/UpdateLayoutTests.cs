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
    [Fact]
    public Task InlineUpdateBannerIsRemoved() => HostedConnectionTests.OnDispatcher(() =>
    {
        var window = new MainWindow();
        try { Assert.Null(window.FindName("UpdateNotice")); }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static readonly string[] SupportedResults =
    [
        "All up to date.", "New version out!", "Checking for updates…",
        "GitHub asked you to try again later.",
        "Update information could not be verified. Try again or open the official Releases page.",
        "Couldn’t reach GitHub right now. Check your connection and try again.",
        "Update check cancelled. Try again when you’re ready."
    ];
    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    [InlineData(1060d, 400d)]
    public Task StatusAndActionsKeepTheirLayoutDuringDelayedCheck(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var checker = new UpdateSessionTests.ControlledChecker();
        var clock = new UpdatePresentationClock();
        await using var coordinator = new SerializedTrackerCoordinator(new UpdateSessionTests.Repository(), new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "1.0.0", timeProvider: clock);
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
            Assert.Null(window.FindName("RetryUpdateCheckButton"));
            var feedback = (TextBlock)window.FindName("UpdatePreferenceStatusTextBlock");
            Assert.False(feedback.IsVisible);
            var release = (Button)window.FindName("OpenUpdateReleasePageButton");
            Assert.True(summary.IsVisible);
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-idle");
            double initialHeight = summary.ActualHeight;
            double actionY = check.TranslatePoint(new Point(), summary).Y;
            var toggle = (CheckBox)window.FindName("CheckForUpdatesOnStartupCheckBox");
            double toggleY = toggle.TranslatePoint(new Point(), summary).Y;
            var foreground = status.Foreground;
            var buttonBackground = check.Background;
            var buttonForeground = check.Foreground;
            string initialStatus = status.Text;
            ((IInvokeProvider)new ButtonAutomationPeer(check).GetPattern(PatternInterface.Invoke)).Invoke();
            await Idle();
            Task pending = vm.CheckForUpdatesAsync();
            Assert.Equal(initialStatus, status.Text);
            Assert.Same(foreground, status.Foreground);
            Assert.Same(buttonBackground, check.Background);
            Assert.Same(buttonForeground, check.Foreground);
            Assert.Equal("Check for updates", check.Content);
            Assert.Equal(1, checker.Calls);
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.UpdateCheckStatus) && vm.IsUpdateProgressVisible) shown.TrySetResult(); };
            clock.Advance(300);
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Idle();
            Assert.Equal("Checking for updates…", status.Text);
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-checking");
            Assert.Equal(toggleY, toggle.TranslatePoint(new Point(), summary).Y);
            Assert.Equal(initialHeight, summary.ActualHeight);
            Assert.Equal(actionY, check.TranslatePoint(new Point(), summary).Y);
            Assert.True(release.IsVisible);
            Assert.True(check.IsEnabled);
            var label = (TextBlock)window.FindName("UpdateStatusLabel");
            Assert.InRange(Math.Abs(label.TranslatePoint(new Point(), summary).Y - status.TranslatePoint(new Point(), summary).Y), 0, 1);
            Assert.True(status.TranslatePoint(new Point(), summary).X > label.TranslatePoint(new Point(), summary).X);
            checker.Result.SetResult(new(ManualReleaseUpdateStatus.InvalidResponse));
            await pending;
            await Idle();
            Assert.Equal(toggleY, toggle.TranslatePoint(new Point(), summary).Y);
            Assert.Equal(initialHeight, summary.ActualHeight);
            Assert.Equal(actionY, check.TranslatePoint(new Point(), summary).Y);
            Assert.True(check.IsEnabled);
            check.BringIntoView();
            await Idle();
            Assert.True(check.Focus());
            Assert.True(check.IsKeyboardFocused);
            Assert.True(check.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.True(release.IsKeyboardFocused);
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-error-focus");
            var measured = new TextBlock { Text = status.Text, FontFamily = status.FontFamily, FontSize = status.FontSize, TextWrapping = TextWrapping.Wrap };
            measured.Measure(new Size(status.ActualWidth, double.PositiveInfinity));
            Assert.True(status.ActualHeight >= measured.DesiredSize.Height - 1);
            var row = (Grid)status.Parent;
            foreach (string result in SupportedResults)
            {
                measured.Text = result;
                measured.Measure(new Size(status.ActualWidth, double.PositiveInfinity));
                Assert.True(row.ActualHeight >= measured.DesiredSize.Height - 1);
            }
            Assert.InRange(row.ActualHeight, 14, 32);
            Assert.Equal(6, toggleY - row.TranslatePoint(new Point(0, row.ActualHeight), summary).Y, precision: 6);
            Assert.Equal("Check for updates", new ButtonAutomationPeer(check).GetName());
            Assert.True(release.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
            Assert.True(check.IsKeyboardFocused);
        }
        finally { await vm.StopUpdateChecksAsync(); window.Close(); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public Task ManualNewerResultAndPreferenceAreReachableWithoutModal(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new UpdateSessionTests.Repository();
        var checker = new UpdateSessionTests.ControlledChecker();
        var clock = new UpdatePresentationClock();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
        var launcher = new RecordingLauncher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, updateReleasePageLauncher: launcher, timeProvider: clock);
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
            await vm.CheckForUpdatesAsync();
            await Idle();
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            Assert.False(vm.IsUpdateNoticeVisible);
            var view = (Button)window.FindName("OpenUpdateReleasePageButton");
            Assert.True(view.IsVisible);
            var noticePosition = view.TranslatePoint(new Point(), window);
            checker.Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task recheck = vm.CheckForUpdatesAsync();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.UpdateCheckStatus) && vm.IsUpdateProgressVisible) shown.TrySetResult(); };
            Assert.True(view.IsVisible);
            clock.Advance(300);
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Idle();
            Assert.True(view.IsVisible);
            Assert.Equal(noticePosition, view.TranslatePoint(new Point(), window));
            checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
            await recheck;
            await Idle();
            view.BringIntoView();
            await Idle();
            Assert.True(view.Focus());
            ((IInvokeProvider)new ButtonAutomationPeer(view).GetPattern(PatternInterface.Invoke)).Invoke();
            await Idle();
            Assert.Equal(new Uri("https://beingkairo.com/tools/souls-tracker/"), launcher.Opened);
            Assert.Equal("New version out!", vm.UpdateCheckStatus);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-newer");
            var toggle = (CheckBox)window.FindName("CheckForUpdatesOnStartupCheckBox");
            Assert.True(view.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.True(toggle.IsKeyboardFocused);
            toggle.BringIntoView();
            await Idle();
            Assert.True(toggle.Focus());
            toggle.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Idle();
            Assert.True(repository.State.CheckForUpdatesOnStartup);
            Assert.True(vm.CheckForUpdatesOnStartup);
            Assert.False(((TextBlock)window.FindName("UpdatePreferenceStatusTextBlock")).IsVisible);
            repository.FailSave = true;
            toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Idle();
            Assert.True(toggle.IsChecked);
            Assert.NotNull(vm.ErrorMessage);
            var feedback = (TextBlock)window.FindName("UpdatePreferenceStatusTextBlock");
            Assert.Contains("could not be saved", feedback.Text, StringComparison.Ordinal);
            Assert.True(feedback.IsVisible);
            var measuredFeedback = new TextBlock { Text = feedback.Text, FontFamily = feedback.FontFamily, FontSize = feedback.FontSize, TextWrapping = TextWrapping.Wrap };
            measuredFeedback.Measure(new Size(feedback.ActualWidth, double.PositiveInfinity));
            Assert.True(feedback.ActualHeight >= measuredFeedback.DesiredSize.Height - 1);
            feedback.BringIntoView();
            await Idle();
            AppearanceGeometryTests.Capture(window, $"updates-{width}-{height}-save-failure");
        }
        finally { await vm.StopUpdateChecksAsync(); window.Close(); }
    });

    private sealed class RecordingLauncher : IUpdateReleasePageLauncher
    {
        public Uri? Opened { get; private set; }
        public bool TryOpen(Uri releasePage) { Opened = releasePage; return true; }
    }

    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
