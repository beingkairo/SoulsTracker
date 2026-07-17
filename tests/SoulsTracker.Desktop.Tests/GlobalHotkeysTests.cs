using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop.Tests;

public sealed class GlobalHotkeysTests
{
    [Theory]
    [InlineData(0x0003u, 0x41u, "Ctrl+Alt+A")]
    [InlineData(0x0007u, 0x70u, "Ctrl+Alt+Shift+F1")]
    [InlineData(0x0007u, 0x26u, "Ctrl+Alt+Shift+Up Arrow")]
    public void PersistedBindingsUseCanonicalWpfLabels(uint modifiers, uint virtualKey, string expected)
    {
        Assert.True(GlobalHotkeyBinding.TryFromPersisted(modifiers, virtualKey, out GlobalHotkeyBinding? binding));
        Assert.Equal(expected, binding!.DisplayText);
    }

    [Theory]
    [InlineData(0x01u)]
    [InlineData(0x5Bu)]
    public void PersistedUnsupportedKeysAreRejected(uint virtualKey) =>
        Assert.False(GlobalHotkeyBinding.TryFromPersisted(0x0003, virtualKey, out _));

    [Fact]
    public async Task RegistersExactlyTheApprovedPairsWithNoRepeatAndShowsTheirGlobalManualPurpose()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        var native = new RecordingGlobalHotkeyNative(true, true);
        var messageSink = new RecordingMessageSink();
        using var service = new DesktopGlobalHotkeyService(messageSink, native, harness.ViewModel);

        GlobalHotkeyRegistrationResult result = service.Start();

        Assert.True(result.IsRegistered);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Registered, result.Status);
        Assert.Equal(
            "Manual hotkeys are active.",
            harness.ViewModel.GlobalHotkeyStatus);
        Assert.Collection(
            native.Registrations,
            registration => Assert.Equal(
                new GlobalHotkeyRegistration(
                    0x5001,
                    0x4007,
                    0x26),
                registration),
            registration => Assert.Equal(
                new GlobalHotkeyRegistration(
                    0x5002,
                    0x4007,
                    0x28),
                registration));
    }

    [Fact]
    public async Task SecondRegistrationFailureRollsBackTheFirstAndReportsOnlyTheExactUnavailableStatus()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        var native = new RecordingGlobalHotkeyNative(true, false);
        var messageSink = new RecordingMessageSink();
        var service = new DesktopGlobalHotkeyService(messageSink, native, harness.ViewModel);

        GlobalHotkeyRegistrationResult result = service.Start();

        Assert.False(result.IsRegistered);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Unavailable, result.Status);
        Assert.Equal(
            "Global hotkeys are unavailable because another application is using one of the required key combinations. The desktop controls remain available.",
            harness.ViewModel.GlobalHotkeyStatus);
        Assert.Equal([GlobalHotkeyController.IncrementHotkeyId], native.UnregisteredIds);
        Assert.Equal(0, messageSink.DisposeCount);
        Assert.False(await service.HandleMessageAsync(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.IncrementHotkeyId));

        service.Dispose();
        service.Dispose();

        Assert.Equal([GlobalHotkeyController.IncrementHotkeyId], native.UnregisteredIds);
        Assert.Equal(1, messageSink.DisposeCount);
    }

    [Fact]
    public async Task InitialConflictKeepsMessageSinkAliveForRecoveryAndDispatch()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Bloodborne));
        var sink = new RecordingMessageSink();
        var native = new RecordingGlobalHotkeyNative(true, false, true, true);
        using var service = new DesktopGlobalHotkeyService(sink, native, harness.ViewModel);
        Assert.False(service.Start().IsRegistered);
        Assert.Equal(0, sink.DisposeCount);

        Assert.True(service.Replace(new GlobalHotkeySettings(
            new GlobalHotkeyBinding(0x0003, 0x41, "Ctrl+Alt+A"),
            new GlobalHotkeyBinding(0x0003, 0x42, "Ctrl+Alt+B"))).IsRegistered);
        Assert.True(service.IsRegistered);
        Assert.True(await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.IncrementHotkeyId));
    }

    [Fact]
    public async Task DisposalRemovesTheMessageHookBeforeUnregisteringOnlyTheAcquiredIdsOnce()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        var events = new List<string>();
        var native = new RecordingGlobalHotkeyNative(events, true, true)
        {
            UnregisterResult = false,
        };
        var messageSink = new RecordingMessageSink(events);
        var service = new DesktopGlobalHotkeyService(messageSink, native, harness.ViewModel);
        Assert.True(service.Start().IsRegistered);

        service.Dispose();
        service.Dispose();

        Assert.Equal(
            [
                "message-hook-attached",
                "message-hook-removed",
                $"unregister:{GlobalHotkeyController.DecrementHotkeyId}",
                $"unregister:{GlobalHotkeyController.IncrementHotkeyId}",
            ],
            events);
        Assert.Equal(
            [GlobalHotkeyController.DecrementHotkeyId, GlobalHotkeyController.IncrementHotkeyId],
            native.UnregisteredIds);
        Assert.Equal(1, messageSink.DisposeCount);
    }

    [Fact]
    public async Task HotkeyMessagesUseOnlyTheViewModelCommandsAndPreserveManualGuards()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Bloodborne));
        using var service = CreateStartedService(harness.ViewModel);

        Assert.True(await service.HandleMessageAsync(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.DecrementHotkeyId));
        Assert.Equal(0, harness.ViewModel.ManualDeaths);
        Assert.Equal(1, harness.Repository.SaveCount); // Game selection only; zero decrement is a no-op.

        Assert.True(await service.HandleMessageAsync(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.IncrementHotkeyId));
        Assert.Equal(1, harness.ViewModel.ManualDeaths);
        Assert.Equal(2, harness.Repository.SaveCount);

        Assert.True(await service.HandleMessageAsync(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.DecrementHotkeyId));
        Assert.Equal(0, harness.ViewModel.ManualDeaths);
        Assert.Equal(3, harness.Repository.SaveCount);

        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds1));
        int automaticGameSaveCount = harness.Repository.SaveCount;

        Assert.True(await service.HandleMessageAsync(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.IncrementHotkeyId));
        Assert.True(await service.HandleMessageAsync(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.DecrementHotkeyId));
        Assert.Equal(0, harness.ViewModel.ManualDeaths);
        Assert.Equal(automaticGameSaveCount, harness.Repository.SaveCount);
    }

    [Fact]
    public async Task ControlsDisabledHotkeyMessageIsAViewModelGuardedNoOp()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        using var service = CreateStartedService(harness.ViewModel);

        Assert.False(harness.ViewModel.ControlsEnabled);
        Assert.True(await service.HandleMessageAsync(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.IncrementHotkeyId));
        Assert.Equal(0, harness.Repository.SaveCount);
        Assert.Equal(0, harness.ViewModel.ManualDeaths);
    }

    [Fact]
    public async Task NativeMessageSinkDispatchesTheApprovedHotkeyWithoutBlockingItsWindowProcedure()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Bloodborne));
        var messageSink = new RecordingMessageSink();
        using var service = new DesktopGlobalHotkeyService(
            messageSink,
            new RecordingGlobalHotkeyNative(true, true),
            harness.ViewModel);
        Assert.True(service.Start().IsRegistered);
        Task<int> nextSave = harness.Repository.WaitForNextSaveAsync();

        Assert.True(messageSink.Dispatch(
            GlobalHotkeyController.WindowsHotkeyMessage,
            GlobalHotkeyController.IncrementHotkeyId));
        Assert.Equal(2, await nextSave.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, harness.ViewModel.ManualDeaths);
        Assert.False(messageSink.Dispatch(GlobalHotkeyController.WindowsHotkeyMessage, hotkeyId: 0x5003));
    }

    [Fact]
    public async Task ReplaceRejectsDuplicateBindingsBeforeTouchingNativeRegistration()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        var native = new RecordingGlobalHotkeyNative(true, true);
        using var service = new DesktopGlobalHotkeyService(new RecordingMessageSink(), native, harness.ViewModel);
        Assert.True(service.Start().IsRegistered);

        GlobalHotkeyBinding binding = new(0x0007, 0x26, "Ctrl+Alt+Shift+Up Arrow");
        GlobalHotkeyRegistrationResult result = service.Replace(new GlobalHotkeySettings(binding, binding));

        Assert.False(result.IsRegistered);
        Assert.Equal(2, native.Registrations.Count);
        Assert.True(service.ActiveSettings == GlobalHotkeySettings.Default);
    }

    [Fact]
    public async Task ReplaceRestoresOldPairWhenCandidateCannotRegister()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        var native = new RecordingGlobalHotkeyNative(true, true, true, false, true, true);
        using var service = new DesktopGlobalHotkeyService(new RecordingMessageSink(), native, harness.ViewModel);
        Assert.True(service.Start().IsRegistered);

        GlobalHotkeyRegistrationResult result = service.Replace(new GlobalHotkeySettings(
            new GlobalHotkeyBinding(0x0003, 0x41, "Ctrl+Alt+A"),
            new GlobalHotkeyBinding(0x0003, 0x42, "Ctrl+Alt+B")));

        Assert.False(result.IsRegistered);
        Assert.Equal("The hotkey change was not applied. The previous hotkeys remain active.", result.StatusMessage);
        Assert.True(service.IsRegistered);
        Assert.Equal(GlobalHotkeySettings.Default, service.ActiveSettings);
    }

    [Fact]
    public async Task FailedApplyRestoresPendingLabelsToTheActualActivePair()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.ConfigureGlobalHotkeys(
            GlobalHotkeySettings.Default,
            _ => Task.FromResult(GlobalHotkeyRegistrationResult.ChangeNotAppliedPreviousActive));
        harness.ViewModel.CapturePendingHotkey(true, System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt);

        await harness.ViewModel.ApplyGlobalHotkeysAsync();

        Assert.Equal("Ctrl+Alt+Shift+Up Arrow", harness.ViewModel.PendingIncrementHotkey);
        Assert.Equal("The hotkey change was not applied. The previous hotkeys remain active.", harness.ViewModel.GlobalHotkeyStatus);
    }

    private static DesktopGlobalHotkeyService CreateStartedService(DesktopTrackerViewModel viewModel)
    {
        var service = new DesktopGlobalHotkeyService(
            new RecordingMessageSink(),
            new RecordingGlobalHotkeyNative(true, true),
            viewModel);
        Assert.True(service.Start().IsRegistered);
        return service;
    }

    private sealed record GlobalHotkeyRegistration(int HotkeyId, uint Modifiers, uint VirtualKey);

    private sealed class RecordingGlobalHotkeyNative : IWindowsGlobalHotkeyNative
    {
        private readonly Queue<bool> registrationResults;
        private readonly List<string>? events;

        public RecordingGlobalHotkeyNative(params bool[] registrationResults)
        {
            this.registrationResults = new Queue<bool>(registrationResults);
        }

        public RecordingGlobalHotkeyNative(List<string> events, params bool[] registrationResults)
        {
            this.events = events ?? throw new ArgumentNullException(nameof(events));
            this.registrationResults = new Queue<bool>(registrationResults);
        }

        public List<GlobalHotkeyRegistration> Registrations { get; } = [];

        public List<int> UnregisteredIds { get; } = [];

        public bool UnregisterResult { get; init; } = true;

        public bool RegisterHotKey(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey)
        {
            Registrations.Add(new GlobalHotkeyRegistration(hotkeyId, modifiers, virtualKey));
            return registrationResults.Dequeue();
        }

        public bool UnregisterHotKey(nint windowHandle, int hotkeyId)
        {
            UnregisteredIds.Add(hotkeyId);
            events?.Add($"unregister:{hotkeyId}");
            return UnregisterResult;
        }
    }

    private sealed class RecordingMessageSink(List<string>? events = null) : IGlobalHotkeyMessageSink
    {
        private readonly List<string>? events = events;
        private Func<int, nint, bool>? messageHandler;

        public int DisposeCount { get; private set; }

        public nint WindowHandle => (nint)123;

        public void SetMessageHandler(Func<int, nint, bool> messageHandler)
        {
            ArgumentNullException.ThrowIfNull(messageHandler);
            this.messageHandler = messageHandler;
            events?.Add("message-hook-attached");
        }

        public bool Dispatch(int message, nint hotkeyId) => messageHandler?.Invoke(message, hotkeyId) ?? false;

        public void Dispose()
        {
            DisposeCount++;
            messageHandler = null;
            events?.Add("message-hook-removed");
        }
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly SerializedTrackerCoordinator coordinator;

        public TestHarness(PersistentTrackerState state)
        {
            Repository = new FakeRepository(state);
            coordinator = new SerializedTrackerCoordinator(Repository, new NullPublisher());
            ViewModel = new DesktopTrackerViewModel(coordinator);
        }

        public FakeRepository Repository { get; }

        public DesktopTrackerViewModel ViewModel { get; }

        public GameChoice Game(GameId gameId) => ViewModel.GameChoices.Single(choice => choice.GameId == gameId);

        public ValueTask DisposeAsync() => coordinator.DisposeAsync();
    }

    private sealed class FakeRepository(PersistentTrackerState state) : ITrackerStateRepository
    {
        private TaskCompletionSource<int> nextSave = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PersistentTrackerState State { get; private set; } = state;

        public int SaveCount { get; private set; }

        public Task<int> WaitForNextSaveAsync() => nextSave.Task;

        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TrackerStateLoadResult.Loaded(State));

        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            State = state;
            SaveCount++;
            nextSave.TrySetResult(SaveCount);
            nextSave = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
