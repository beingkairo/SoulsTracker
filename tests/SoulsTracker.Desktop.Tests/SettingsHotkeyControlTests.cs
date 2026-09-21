using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class SettingsHotkeyControlTests
{
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
            Assert.False(field.IsVisible);
            Assert.False(service.IsRegistered);
            Assert.Empty(native.Registered);
        }
        finally { window.Close(); }
    });

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
