using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class HotkeyFeedbackControlTests
{
    [Theory]
    [InlineData("validation")]
    [InlineData("registration")]
    [InlineData("persistence")]
    public Task FailuresRemainDistinctAndNeverShowSuccess(string failure) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        int calls = 0;
        vm.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ =>
        {
            calls++;
            return Task.FromResult(failure == "persistence" ? GlobalHotkeyRegistrationResult.SaveFailed : GlobalHotkeyRegistrationResult.Unavailable);
        });
        if (failure == "validation")
        {
            vm.CapturePendingHotkey(true, Key.F7, ModifierKeys.Control);
            vm.CapturePendingHotkey(false, Key.F7, ModifierKeys.Control);
        }
        int timers = 0;
        var window = new MainWindow((_, _) => null, _ => { }, (_, _) => { timers++; return () => { }; })
        { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Settings(window);
            ((Button)window.FindName("ApplyHotkeysButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            string status = Assert.IsType<string>(vm.GlobalHotkeyStatus);
            Assert.NotEmpty(status);
            Assert.Empty(((TextBlock)window.FindName("HotkeySuccessStatus")).Text);
            Assert.Equal(failure == "validation" ? 0 : 1, calls);
            Assert.Equal(0, timers);
            Assert.Equal(status, ((TextBlock)window.FindName("GlobalHotkeyStatusTextBlock")).Text);
            Assert.True(((TextBlock)window.FindName("GlobalHotkeyStatusTextBlock")).IsVisible);
            Assert.Empty(repository.Saves);
            await Idle();
            Assert.Equal(status, vm.GlobalHotkeyStatus);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("replace")]
    [InlineData("close")]
    [InlineData("newer")]
    [InlineData("tab")]
    [InlineData("game")]
    public Task ObsoleteApplyCannotReviveConfirmation(string transition) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var pending = new TaskCompletionSource<GlobalHotkeyRegistrationResult>();
        int calls = 0;
        vm.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ => ++calls == 1 ? pending.Task : Task.FromResult(GlobalHotkeyRegistrationResult.Unavailable));
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Settings(window);
            var apply = (Button)window.FindName("ApplyHotkeysButton");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal(1, calls);
            switch (transition)
            {
                case "replace": window.DataContext = null; window.DataContext = vm; break;
                case "close": window.Close(); break;
                case "newer": apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                case "tab": ((TabItem)window.FindName("MainWorkspaceTab")).IsSelected = true; break;
                case "game": await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == GameId.Bloodborne)); break;
            }
            pending.SetResult(GlobalHotkeyRegistrationResult.Registered); await Idle();
            Assert.Empty(((TextBlock)window.FindName("HotkeySuccessStatus")).Text);
            Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("HotkeySuccessOverlay")).Visibility);
            if (transition == "newer") Assert.Equal(GlobalHotkeyRegistrationResult.Unavailable.StatusMessage, vm.GlobalHotkeyStatus);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RecordingRestoresCapturedOriginEvenDuringPendingSave(bool alternateOrigin) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var pending = new TaskCompletionSource<GlobalHotkeyRegistrationResult>();
        vm.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ => pending.Task);
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Settings(window);
            var origin = (TabItem)window.FindName(alternateOrigin ? "OverlayWorkspaceTab" : "SettingsWorkspaceTab");
            origin.IsSelected = true; await Idle();
            typeof(MainWindow).GetMethod("BeginHotkeyRecording", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]);
            await Idle(); Assert.True(vm.IsHotkeyRecording);
            SendKey(window, Key.Enter); await Idle();
            ((TabItem)window.FindName("MainWorkspaceTab")).IsSelected = true; await Idle();
            pending.SetResult(GlobalHotkeyRegistrationResult.Registered); await Idle();
            Assert.True(origin.IsSelected);
            Assert.Same(window.FindName(alternateOrigin ? "OverlayConfigurationScrollViewer" : "SettingsContentScrollViewer"), Keyboard.FocusedElement);
            Assert.False(vm.IsHotkeyRecording);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EveryGameKeepsHotkeyContextAndEligibility() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        vm.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ => Task.FromResult(GlobalHotkeyRegistrationResult.Registered));
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Settings(window);
            var settings = (TabItem)window.FindName("SettingsWorkspaceTab");
            var field = (TextBox)window.FindName("IncrementHotkeyTextBox");
            Assert.Equal(9, vm.GameChoices.Count);
            foreach (var game in vm.GameChoices)
            {
                await vm.SelectGameAsync(game); await Settings(window);
                Assert.True(field.IsVisible);
                Assert.Equal(vm.IsGlobalHotkeyConfigurationAvailable, field.IsEnabled);
                if (!field.IsEnabled)
                {
                    Assert.False(field.Focus());
                    Assert.False(vm.IsHotkeyRecording);
                    continue;
                }
                foreach (var key in new[] { Key.Escape, Key.Enter })
                {
                    field.BringIntoView(); await Idle(); Assert.True(field.Focus()); await Idle();
                    Assert.True(vm.IsHotkeyRecording);
                    ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
                    SendKey(window, key); await Idle();
                    Assert.True(settings.IsSelected);
                    Assert.False(vm.IsHotkeyRecording);
                    Assert.Equal(key == Key.Enter ? "Hotkeys applied successfully" : string.Empty,
                        ((TextBlock)window.FindName("HotkeySuccessStatus")).Text);
                }
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SuccessExpiresOnDispatcherWithoutMovingSettings() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        vm.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ => Task.FromResult(GlobalHotkeyRegistrationResult.Registered));
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Settings(window);
            var apply = (Button)window.FindName("ApplyHotkeysButton");
            apply.BringIntoView(); await Idle();
            var stack = (StackPanel)window.FindName("SettingsContentStack");
            var before = stack.RenderSize;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var status = (TextBlock)window.FindName("HotkeySuccessStatus");
            Assert.True(status.IsVisible);
            await Task.Delay(TimeSpan.FromSeconds(3));
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            await Task.Delay(TimeSpan.FromSeconds(2.5)); await Idle();
            Assert.Equal("Hotkeys applied successfully", status.Text);
            await Task.Delay(TimeSpan.FromSeconds(3)); await Idle();
            Assert.Empty(status.Text);
            Assert.Equal(before, stack.RenderSize);
            Assert.True(((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DepartingContextCancelsRecordingWithoutLosingOrigin(bool close) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Settings(window);
            typeof(MainWindow).GetMethod("BeginHotkeyRecording", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]);
            await Idle(); Assert.True(vm.IsHotkeyRecording);
            string original = vm.PendingIncrementHotkey;
            vm.CaptureRecordedHotkey(Key.F7, ModifierKeys.Control);
            if (close) window.Close();
            else { window.DataContext = null; window.DataContext = vm; }
            await Idle();
            Assert.False(vm.IsHotkeyRecording);
            Assert.Equal(original, vm.PendingIncrementHotkey);
        }
        finally { window.Close(); }
    });

    private static async Task Settings(MainWindow window)
    {
        ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
        await Idle();
    }
    private static void SendKey(MainWindow window, Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(window), Environment.TickCount, key)
    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
