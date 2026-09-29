using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class FeedbackReadabilityTests
{
    [Theory]
    [InlineData(560, 400, false)]
    [InlineData(560, 760, false)]
    [InlineData(1060, 760, false)]
    [InlineData(560, 400, true)]
    [InlineData(560, 760, true)]
    [InlineData(1060, 760, true)]
    public Task FailedExplicitHotkeyApplyRevealsCompleteExplanation(int width, int height, bool saveFailure) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        bool success = true;
        var failure = saveFailure ? GlobalHotkeyRegistrationResult.SaveFailed : GlobalHotkeyRegistrationResult.Unavailable;
        vm.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ => Task.FromResult(success ? GlobalHotkeyRegistrationResult.Registered : failure));
        var window = new MainWindow((_, _) => null, _ => { })
        { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
            var scroll = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            var apply = (Button)window.FindName("ApplyHotkeysButton");
            apply.BringIntoView(); await Idle(); Assert.True(apply.Focus()); await Idle();
            double offset = scroll.VerticalOffset;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal(offset, scroll.VerticalOffset);
            Assert.Equal("Hotkeys applied successfully", ((TextBlock)window.FindName("HotkeySuccessStatus")).Text);
            AssertContained((Border)window.FindName("HotkeySuccessOverlay"), viewport);
            AppearanceGeometryTests.Capture(window, $"hotkey-success-{width}-{height}-{saveFailure}");
            success = false;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var status = (TextBlock)window.FindName("GlobalHotkeyStatusTextBlock");
            AppearanceGeometryTests.Capture(window, $"hotkey-failure-{width}-{height}-{saveFailure}");
            Assert.Equal(failure.StatusMessage, status.Text);
            Assert.True(status.IsVisible);
            AssertContained(status, viewport);
            Assert.Same(apply, Keyboard.FocusedElement);
            Assert.Empty(((TextBlock)window.FindName("HotkeySuccessStatus")).Text);
            if (height > 400) Assert.Equal(offset, scroll.VerticalOffset);
            scroll.ScrollToTop(); await Idle();
            Assert.Equal(0, scroll.VerticalOffset);
            await Idle(); Assert.Equal(0, scroll.VerticalOffset);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400, false)]
    [InlineData(560, 760, false)]
    [InlineData(1060, 760, false)]
    [InlineData(560, 400, true)]
    [InlineData(560, 760, true)]
    [InlineData(1060, 760, true)]
    public Task ActualHostedCopyKeepsFeedbackAndActionsReadable(int width, int height, bool fail) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var store = new HostedPublisherConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent"),
            new NoSecrets(), ["https://overlay.beingkairo.com"]);
        await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, _ => throw new InvalidOperationException("No sender allowed."));
        await connection.InitializeAsync(repository.State);
        // Synthetic paired eligibility only. No secret storage or transport is used.
        typeof(HostedOverlayConnection).GetField("configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection,
            HostedPublisherConfiguration.Create("https://overlay.beingkairo.com", new string('a', 32), new string('b', 64), new string('c', 64), ["https://overlay.beingkairo.com"]));
        typeof(HostedOverlayConnection).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(connection, null);
        vm.ConfigureHostedOverlay(connection);
        int copies = 0;
        var window = new MainWindow((_, _) => null, _ => { copies++; if (fail) throw new IOException("Synthetic failure"); })
        { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var apply = (Button)window.FindName("ApplyAppearanceButton");
            var reset = (Button)window.FindName("ResetSelectedOverlayAppearanceButton");
            var box = (Border)window.FindName("HostedCopyFeedbackOverlay");
            var status = (TextBlock)window.FindName("HostedCopyStatus");
            copy.BringIntoView(); await Idle(); Assert.True(copy.Focus());
            Assert.True(copy.IsEnabled); AssertHit(window, copy);
            var before = Bounds(apply, window);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal(1, copies);
            Assert.Equal(fail ? "Could not copy the Overlay URL. Try Copy URL again." : "URL copied", status.Text);
            Assert.Same(copy, Keyboard.FocusedElement);
            var layer = (Canvas)window.FindName("FloatingFeedbackLayer");
            Assert.Same(layer, box.Parent);
            Assert.Equal(new Size(), layer.DesiredSize);
            Assert.True(box.IsVisible);
            Assert.False(Bounds(box, window).IntersectsWith(Bounds(copy, window)));
            scroll.ScrollToEnd(); await Idle();
            Assert.False(box.IsVisible);
            Assert.True(((FrameworkElement)window.FindName("DockedPreviewCard")).IsVisible);
            AppearanceGeometryTests.Capture(window, $"feedback-{width}-{height}-{fail}");
            AssertContained(status, (FrameworkElement)window.Content);
            Assert.True(status.ActualHeight + status.Margin.Top + status.Margin.Bottom >= status.DesiredSize.Height - 0.5);
            foreach (var action in new[] { apply, reset })
            {
                AssertContained(action, (FrameworkElement)window.Content);
                var label = Tree(action).OfType<ContentPresenter>().First();
                Assert.False(Bounds(box, window).IntersectsWith(Bounds(label, window)), $"Copy feedback covers {action.Content}.");
                AssertHit(window, action);
                Assert.True(action.Focus()); await Idle(); Assert.Same(action, Keyboard.FocusedElement);
            }
            Assert.True(apply.Focus());
            Assert.True(apply.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))); await Idle();
            Assert.Same(reset, Keyboard.FocusedElement);
            var presenter = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            Assert.True(presenter.ActualHeight >= 32, $"Editable viewport: {presenter.ActualHeight}");
            copy.BringIntoView(); await Idle();
            Assert.True(box.IsVisible);
            await Task.Delay(TimeSpan.FromSeconds(4.5)); await Idle();
            Assert.True(box.IsVisible);
            await Task.Delay(TimeSpan.FromSeconds(0.7)); await Idle();
            Assert.Equal(fail, box.IsVisible);
            if (!fail)
            {
                Assert.Empty(status.Text);
                Assert.Equal(before, Bounds(apply, window));
            }
            else Assert.NotEmpty(status.Text);
            AppearanceGeometryTests.Capture(window, $"feedback-settled-{width}-{height}-{fail}");
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    private sealed class NoSecrets : IStateSecretProtector
    {
        public byte[] Protect(byte[] value) => throw new InvalidOperationException("No secret writes allowed.");
        public byte[] Unprotect(byte[] value) => throw new InvalidOperationException("No secret reads allowed.");
    }

    private static void AssertHit(MainWindow window, Button button)
    {
        var bounds = Bounds(button, window);
        var hit = window.InputHitTest(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) as DependencyObject;
        Assert.True(ReferenceEquals(hit, button) || hit is not null && button.IsAncestorOf(hit));
    }

    private static void AssertContained(FrameworkElement element, FrameworkElement viewport)
    {
        var visible = new Rect(viewport.RenderSize); visible.Inflate(0.5, 0.5);
        Assert.True(visible.Contains(Bounds(element, viewport)), $"{element.Name} outside viewport: {Bounds(element, viewport)} / {visible}");
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToVisual(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
