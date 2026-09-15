using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class GlobalHotkeyTests
{
    [Fact]
    public async Task DemonsSoulsCustomBindingsSurviveSqliteReload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"SoulsTracker-hotkeys-{Guid.NewGuid():N}");
        var bindings = new GlobalHotkeyConfiguration(2, 118, 2, 119);
        try
        {
            await using (var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "test.db"), new NullPublisher()))
            {
                Assert.True((await coordinator.InitializeAsync()).IsSuccess);
                await coordinator.SetGlobalHotkeysAsync(bindings);
            }
            await using var repository = new SqliteTrackerStateRepository(root, "test.db");
            TrackerStateLoadResult load = await repository.LoadAsync();
            Assert.True(load.IsSuccess);
            Assert.Equal(GameId.DemonsSouls, load.State!.SelectedGameId);
            Assert.Equal(bindings, load.State.GlobalHotkeys);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BloodborneHasNoNativeRegistrationOrExposedConfiguration()
    {
        await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(), new NullPublisher());
        var tracker = new DesktopTrackerViewModel(coordinator);
        await tracker.InitializeAsync();
        await tracker.SelectGameAsync(tracker.GameChoices.Single(choice => choice.GameId == GameId.Bloodborne));
        var native = new RecordingNative();
        using var service = new DesktopGlobalHotkeyService(new MessageSink(), native, tracker);

        var settings = new GlobalHotkeySettings(new GlobalHotkeyBinding(2, 118, "Ctrl+F7"), new GlobalHotkeyBinding(2, 119, "Ctrl+F8"));
        Assert.False(service.Start(settings).IsRegistered);
        Assert.Empty(native.Registered);
        Assert.False(tracker.IsGlobalHotkeyConfigurationAvailable);
        tracker.BeginHotkeyRecording(true);
        Assert.False(tracker.IsHotkeyRecording);
        Assert.False(await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.IncrementHotkeyId));
        await tracker.SelectGameAsync(tracker.GameChoices.Single(choice => choice.GameId == GameId.DemonsSouls));
        Assert.True(service.IsRegistered);
        Assert.Equal((0x4002u, 118u), native.Registered[GlobalHotkeyController.IncrementHotkeyId]);
        Assert.Equal((0x4002u, 119u), native.Registered[GlobalHotkeyController.DecrementHotkeyId]);
    }

    [Fact]
    public async Task SwitchingToBloodborneReleasesKeysAndReturningRestoresDemonsSoulsBindings()
    {
        await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(), new NullPublisher());
        var tracker = new DesktopTrackerViewModel(coordinator);
        await tracker.InitializeAsync();
        var native = new RecordingNative();
        using var service = new DesktopGlobalHotkeyService(new MessageSink(), native, tracker);
        Assert.True(service.Start().IsRegistered);
        var settings = new GlobalHotkeySettings(new GlobalHotkeyBinding(2, 118, "Ctrl+F7"), new GlobalHotkeyBinding(2, 119, "Ctrl+F8"));
        Assert.True(service.Replace(settings).IsRegistered);
        Assert.Equal(2, native.Registered.Count);
        Assert.True(tracker.IsGlobalHotkeyConfigurationAvailable);
        await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.IncrementHotkeyId);
        Assert.Equal(1, tracker.ManualDeaths);

        await tracker.SelectGameAsync(tracker.GameChoices.Single(choice => choice.GameId == GameId.Bloodborne));
        Assert.False(service.IsRegistered);
        Assert.Empty(native.Registered);
        Assert.False(await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.DecrementHotkeyId));

        await tracker.SelectGameAsync(tracker.GameChoices.Single(choice => choice.GameId == GameId.DemonsSouls));
        Assert.True(service.IsRegistered);
        Assert.Equal(2, native.Registered.Count);
        Assert.Equal(settings, service.ActiveSettings);
        Assert.Equal((0x4002u, 118u), native.Registered[GlobalHotkeyController.IncrementHotkeyId]);
        await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.DecrementHotkeyId);
        await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.DecrementHotkeyId);
        Assert.Equal(0, tracker.ManualDeaths);
        service.Dispose();
        Assert.Empty(native.Registered);
        await tracker.SelectGameAsync(tracker.GameChoices.Single(choice => choice.GameId == GameId.Bloodborne));
        await tracker.SelectGameAsync(tracker.GameChoices.Single(choice => choice.GameId == GameId.DemonsSouls));
        Assert.Empty(native.Registered);
    }

    [Fact]
    public async Task EldenRingRetainsSharedHotkeyRegistrationAndConfiguration()
    {
        await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(), new NullPublisher());
        await coordinator.InitializeAsync();
        await coordinator.SubmitAsync(new AcknowledgeEldenRingNoticeCommand());
        await coordinator.SubmitAsync(new SelectGameCommand(GameId.EldenRing));
        var tracker = new DesktopTrackerViewModel(coordinator);
        await tracker.InitializeAsync();
        var native = new RecordingNative();
        using var service = new DesktopGlobalHotkeyService(new MessageSink(), native, tracker);
        Assert.True(service.Start().IsRegistered);
        Assert.True(tracker.IsGlobalHotkeyConfigurationAvailable);
        Assert.Equal(2, native.Registered.Count);
    }

    private sealed class RecordingNative : IWindowsGlobalHotkeyNative
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = new();
        public bool RegisterHotKey(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey) => Registered.TryAdd(hotkeyId, (modifiers, virtualKey));
        public bool UnregisterHotKey(nint windowHandle, int hotkeyId) => Registered.Remove(hotkeyId);
    }

    private sealed class MessageSink : IGlobalHotkeyMessageSink
    {
        public nint WindowHandle => 1;
        public void SetMessageHandler(Func<int, nint, bool> messageHandler) { }
        public void Dispose() { }
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryRepository : ITrackerStateRepository
    {
        private PersistentTrackerState state = PersistentTrackerState.Default;
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
        public Task SaveAsync(PersistentTrackerState updated, CancellationToken cancellationToken = default) { state = updated; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
