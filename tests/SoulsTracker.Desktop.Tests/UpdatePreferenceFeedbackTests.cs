using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class UpdatePreferenceFeedbackTests
{
    [Theory]
    [InlineData(560d, false, true)]
    [InlineData(560d, true, true)]
    [InlineData(1060d, false, true)]
    [InlineData(1060d, true, true)]
    [InlineData(560d, false, false)]
    [InlineData(560d, true, false)]
    [InlineData(1060d, false, false)]
    [InlineData(1060d, true, false)]
    public Task PreferenceSaveFeedbackIsVisibleOnFirstIdleFrame(double width, bool committed, bool failSave) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new RecordingRepository(committed, failSave);
        var checker = new UpdateSessionTests.ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "1.4.0"));
        var launcher = new RecordingLauncher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, updateReleasePageLauncher: launcher);
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
            await vm.CheckForUpdatesAsync();
            await Idle();
            var check = (Button)window.FindName("CheckForUpdatesButton");
            var release = (Button)window.FindName("OpenUpdateReleasePageButton");
            var toggle = (CheckBox)window.FindName("CheckForUpdatesOnStartupCheckBox");
            var feedback = (TextBlock)window.FindName("UpdatePreferenceStatusTextBlock");
            var status = (TextBlock)window.FindName("UpdateCheckStatusTextBlock");
            var row = (Grid)status.Parent;
            var scroll = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            Assert.True(check.Focus());
            Assert.True(check.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.True(release.IsKeyboardFocused);
            Assert.True(release.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            await Idle();
            Assert.True(toggle.IsKeyboardFocused);
            AssertFullyVisible(toggle, viewport);
            Assert.Equal(committed, toggle.IsChecked);
            Assert.Equal(Visibility.Collapsed, feedback.Visibility);
            Assert.Equal(6, toggle.TranslatePoint(new Point(), row).Y - row.ActualHeight, precision: 6);
            double normalOffset = scroll.VerticalOffset;
            int initialSaves = repository.Saves;
            var current = vm.UpdateCurrentVersion;
            var latest = vm.UpdateLatestVersion;
            var result = vm.UpdateCheckStatus;
            string capture = $"preference-{width}-{committed}-{failSave}";
            AppearanceGeometryTests.Capture(window, capture + "-before");

            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, !committed);
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle();
            // No test-side scrolling, layout update or BringIntoView after activation.
            AppearanceGeometryTests.Capture(window, capture + "-first-idle");
            Assert.Equal(initialSaves + 1, repository.Saves);
            Assert.Equal(1, checker.Calls);
            Assert.Equal(0, launcher.Calls);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            Assert.Equal(current, vm.UpdateCurrentVersion);
            Assert.Equal(latest, vm.UpdateLatestVersion);
            Assert.Equal(result, vm.UpdateCheckStatus);
            Assert.True(toggle.IsKeyboardFocused);
            AssertFullyVisible(toggle, viewport);
            bool expected = failSave ? committed : !committed;
            Assert.Equal(expected, toggle.IsChecked);
            Assert.Equal(expected, vm.CheckForUpdatesOnStartup);
            Assert.Equal(expected, repository.State.CheckForUpdatesOnStartup);
            if (failSave)
            {
                Assert.Equal("The update setting could not be saved. Your previous choice is still active.", feedback.Text);
                AssertFullyVisible(feedback, viewport);
                var measured = new TextBlock { Text = feedback.Text, FontFamily = feedback.FontFamily, FontSize = feedback.FontSize, TextWrapping = TextWrapping.Wrap };
                measured.Measure(new Size(feedback.ActualWidth, double.PositiveInfinity));
                Assert.True(feedback.ActualHeight >= measured.DesiredSize.Height - 1);
            }
            else
            {
                Assert.Equal(Visibility.Collapsed, feedback.Visibility);
                Assert.Empty(feedback.Text);
                Assert.Equal(normalOffset, scroll.VerticalOffset);
                Assert.Equal(6, toggle.TranslatePoint(new Point(), row).Y - row.ActualHeight, precision: 6);
            }
        }
        finally { await vm.StopUpdateChecksAsync(); window.Close(); }
    });

    private static void AssertFullyVisible(FrameworkElement element, FrameworkElement viewport)
    {
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        Assert.True(element.IsVisible);
        Assert.InRange(bounds.Left, -0.5, viewport.ActualWidth);
        Assert.InRange(bounds.Right, 0, viewport.ActualWidth + 0.5);
        Assert.InRange(bounds.Top, -0.5, viewport.ActualHeight);
        Assert.InRange(bounds.Bottom, 0, viewport.ActualHeight + 0.5);
    }

    private sealed class RecordingRepository(bool committed, bool failSave) : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: committed);
        public int Saves { get; private set; }
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (failSave) throw new System.IO.IOException("Synthetic save failure");
            State = state;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLauncher : IUpdateReleasePageLauncher
    {
        public int Calls { get; private set; }
        public bool TryOpen(Uri releasePage) { Calls++; return true; }
    }

    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
