using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class SettingsHotkeyControlTests
{
    [Fact]
    public Task SettingsEntryAndReturnKeepHelpClosedForEveryGame() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var settings = (TabItem)window.FindName("SettingsWorkspaceTab");
            var main = (TabItem)window.FindName("MainWorkspaceTab");
            var help = (Button)window.FindName("GlobalHotkeysHelpButton");
            var tooltip = (ToolTip)help.ToolTip;
            Assert.Equal(9, vm.GameChoices.Count);
            foreach (var choice in vm.GameChoices)
            {
                await vm.SelectGameAsync(choice); await Idle();
                settings.Focus(); settings.IsSelected = true; await Idle();
                Assert.False(tooltip.IsOpen);
                Assert.True(help.IsVisible);
                var field = (TextBox)window.FindName("IncrementHotkeyTextBox");
                Assert.True(field.IsVisible);
                Assert.Equal(vm.IsGlobalHotkeyConfigurationAvailable, field.IsEnabled);
                if (!vm.IsGlobalHotkeyConfigurationAvailable)
                    Assert.Contains("not available for this game", vm.GlobalHotkeyUsageDescription);
                var panels = ((StackPanel)window.FindName("SettingsContentStack")).Children.OfType<Border>().ToArray();
                Assert.Equal("Manual update check settings", System.Windows.Automation.AutomationProperties.GetName(panels[0]));
                Assert.Equal("Global hotkeys settings", System.Windows.Automation.AutomationProperties.GetName(panels[1]));
                RecordSettings(window, choice.GameId.Value);
                help.BringIntoView(); await Idle();
                Assert.True(help.Focus()); await Idle();
                Assert.True(tooltip.IsOpen);
                Assert.True(ToolTipService.GetIsEnabled(help));
                main.Focus(); main.IsSelected = true; await Idle();
                Assert.False(help.IsVisible);
                settings.Focus(); settings.IsSelected = true; await Idle();
                Assert.False(tooltip.IsOpen);
                main.Focus(); main.IsSelected = true; await Idle();
            }
            // Exercise WPF TabItem mouse-selection routing separately from
            // Focus(), then intentional keyboard traversal within Settings.
            settings.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });
            await Idle();
            Assert.True(settings.IsSelected);
            Assert.False(tooltip.IsOpen);
            var content = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            Assert.True(content.Focus()); await Idle();
            for (int i = 0; i < 8 && !help.IsKeyboardFocused; i++)
            {
                Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                await Idle();
            }
            Assert.True(help.IsKeyboardFocused);
            Assert.True(tooltip.IsOpen);
            content.Focus(); await Idle();
            Assert.False(tooltip.IsOpen);
            Assert.NotNull(help.ToolTip);
            Assert.True(ToolTipService.GetIsEnabled(help));
            Assert.False(vm.IsHotkeyRecording);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SettingsCaptureKeepsSaveCancelAndMainReturnContract(bool save) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var native = new Native();
        using var service = new DesktopGlobalHotkeyService(new Sink(), native, vm);
        Assert.True(service.Start().IsRegistered);
        vm.ConfigureGlobalHotkeys(service.ActiveSettings, async candidate =>
        {
            var result = service.Replace(candidate);
            if (result.IsRegistered) await coordinator.SetGlobalHotkeysAsync(new(candidate.Increment.Modifiers, candidate.Increment.VirtualKey,
                candidate.Decrement.Modifiers, candidate.Decrement.VirtualKey));
            return result;
        });
        string original = vm.PendingIncrementHotkey;
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var settings = (TabItem)window.FindName("SettingsWorkspaceTab");
            settings.IsSelected = true; await Idle();
            var field = (TextBox)window.FindName("IncrementHotkeyTextBox");
            field.BringIntoView(); await Idle();
            field.Focus(); await Idle();
            Assert.True(vm.IsHotkeyRecording);
            Assert.Same(window.FindName("HotkeyRecordingOverlay"), Keyboard.FocusedElement);
            vm.CaptureRecordedHotkey(Key.F7, ModifierKeys.Control);
            Assert.Equal("Ctrl+F7", vm.PendingIncrementHotkey);
            Assert.Empty(repository.Saves);
            // A tab transition does not discard the in-progress capture.
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
            await Idle();
            Assert.True(vm.IsHotkeyRecording);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, save ? Key.Enter : Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(key);
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsHotkeyRecording);
            await Idle();
            Assert.True(key.Handled);
            Assert.True(((TabItem)window.FindName("MainWorkspaceTab")).IsSelected);
            Assert.Same(window.FindName("WorkspaceTabs"), Keyboard.FocusedElement);
            Assert.Equal(save ? "Ctrl+F7" : original, vm.PendingIncrementHotkey);
            Assert.Equal(save ? 1 : 0, repository.Saves.Count);
            Assert.Equal(save ? 118u : GlobalHotkeySettings.Default.Increment.VirtualKey, service.ActiveSettings.Increment.VirtualKey);
            Assert.Equal(2, native.Registered.Count);
            if (save) Assert.Equal(118u, repository.State.GlobalHotkeys.IncrementVirtualKey);
            Assert.True(await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.IncrementHotkeyId));
            Assert.Equal(1, vm.ManualDeaths);
            Assert.True(await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.DecrementHotkeyId));
            Assert.Equal(0, vm.ManualDeaths);
            await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == GameId.Bloodborne));
            settings.IsSelected = true; await Idle();
            Assert.True(field.IsVisible);
            Assert.False(field.IsEnabled);
            Assert.False(service.IsRegistered);
            Assert.Empty(native.Registered);
        }
        finally { window.Close(); }
    });

    private static void RecordSettings(MainWindow window, string game)
    {
        string? directory = Environment.GetEnvironmentVariable("SOULSTRACKER_SETTINGS_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, game + ".png")); encoder.Save(stream);
    }

    private sealed class Native : IWindowsGlobalHotkeyNative
    {
        internal Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = [];
        public bool RegisterHotKey(nint handle, int id, uint modifiers, uint key) => Registered.TryAdd(id, (modifiers, key));
        public bool UnregisterHotKey(nint handle, int id) => Registered.Remove(id);
    }
    private sealed class Sink : IGlobalHotkeyMessageSink
    {
        public nint WindowHandle => 1;
        public void SetMessageHandler(Func<int, nint, bool> messageHandler) { }
        public void Dispose() { }
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
