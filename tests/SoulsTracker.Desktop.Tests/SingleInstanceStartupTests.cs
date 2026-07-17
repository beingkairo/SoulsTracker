using SoulsTracker.Desktop;

namespace SoulsTracker.Desktop.Tests;

public sealed class SingleInstanceStartupTests
{
    [Fact]
    public void FirstNamedMutexAcquisitionSucceedsSecondReportsAlreadyRunningAndReleasePermitsLaterAcquisition()
    {
        var factory = new WindowsSingleInstanceLeaseFactory(CreateTestMutexName(), new WindowsNamedMutexFactory());
        DesktopSingleInstanceLeaseAcquisition first = factory.Acquire();

        Assert.Equal(DesktopSingleInstanceLeaseStatus.Acquired, first.Status);
        Assert.NotNull(first.Lease);
        IDesktopSingleInstanceLease firstLease = first.Lease!;

        try
        {
            DesktopSingleInstanceLeaseAcquisition second = factory.Acquire();

            Assert.Equal(DesktopSingleInstanceLeaseStatus.AlreadyRunning, second.Status);
            Assert.Null(second.Lease);
        }
        finally
        {
            firstLease.Dispose();
        }

        DesktopSingleInstanceLeaseAcquisition later = factory.Acquire();
        try
        {
            Assert.Equal(DesktopSingleInstanceLeaseStatus.Acquired, later.Status);
            Assert.NotNull(later.Lease);
        }
        finally
        {
            later.Lease?.Dispose();
        }
    }

    [Fact]
    public void AcquisitionErrorFromNativeMutexFactoryFailsClosed()
    {
        var factory = new WindowsSingleInstanceLeaseFactory("test-mutex", new ThrowingNamedMutexFactory());

        DesktopSingleInstanceLeaseAcquisition result = factory.Acquire();

        Assert.Equal(DesktopSingleInstanceLeaseStatus.Failed, result.Status);
        Assert.Null(result.Lease);
    }

    [Fact]
    public void UnownedMutexHandleIsDisposedWithoutBeingReleased()
    {
        var handle = new RecordingNamedMutexHandle();
        var factory = new WindowsSingleInstanceLeaseFactory(
            "test-mutex",
            new StubNamedMutexFactory(handle, createdNew: false));

        DesktopSingleInstanceLeaseAcquisition result = factory.Acquire();

        Assert.Equal(DesktopSingleInstanceLeaseStatus.AlreadyRunning, result.Status);
        Assert.Null(result.Lease);
        Assert.Equal(0, handle.ReleaseCount);
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public void AcquiredMutexLeaseReleasesAndDisposesExactlyOnce()
    {
        var handle = new RecordingNamedMutexHandle();
        var factory = new WindowsSingleInstanceLeaseFactory(
            "test-mutex",
            new StubNamedMutexFactory(handle, createdNew: true));
        DesktopSingleInstanceLeaseAcquisition result = factory.Acquire();

        Assert.Equal(DesktopSingleInstanceLeaseStatus.Acquired, result.Status);
        Assert.NotNull(result.Lease);

        result.Lease!.Dispose();
        result.Lease.Dispose();

        Assert.Equal(1, handle.ReleaseCount);
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task AlreadyRunningDecisionPreventsEveryDesktopCompositionSideEffectAndUsesExactMessage()
    {
        var composition = new DesktopCompositionProbe();
        using var controller = new DesktopStartupController(
            new StubLeaseFactory(DesktopSingleInstanceLeaseAcquisition.AlreadyRunning()));

        DesktopStartupDecision decision = await controller.StartAsync(composition.ComposeAndStartAsync);

        Assert.False(decision.CanStart);
        Assert.Equal(DesktopStartupStatus.AlreadyRunning, decision.Status);
        Assert.Equal("Unable to open SoulsTracker: another instance is already running.", decision.UserMessage);
        composition.AssertNotComposed();
    }

    [Fact]
    public async Task ProtectionFailurePreventsEveryDesktopCompositionSideEffectAndUsesDistinctSanitizedMessage()
    {
        var composition = new DesktopCompositionProbe();
        using var controller = new DesktopStartupController(
            new StubLeaseFactory(DesktopSingleInstanceLeaseAcquisition.Failed()));

        DesktopStartupDecision decision = await controller.StartAsync(composition.ComposeAndStartAsync);

        Assert.False(decision.CanStart);
        Assert.Equal(DesktopStartupStatus.ProtectionUnavailable, decision.Status);
        Assert.NotNull(decision.UserMessage);
        Assert.NotEqual(DesktopStartupDecision.AlreadyRunning.UserMessage, decision.UserMessage);
        Assert.DoesNotContain("Global", decision.UserMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mutex", decision.UserMessage!, StringComparison.OrdinalIgnoreCase);
        composition.AssertNotComposed();
    }

    [Fact]
    public async Task AcquiredLeaseIsHeldUntilDesktopShutdownAndReleasedOnlyOnce()
    {
        var lease = new RecordingLease();
        var composition = new DesktopCompositionProbe();
        var controller = new DesktopStartupController(
            new StubLeaseFactory(DesktopSingleInstanceLeaseAcquisition.Acquired(lease)));

        DesktopStartupDecision decision = await controller.StartAsync(composition.ComposeAndStartAsync);

        Assert.True(decision.CanStart);
        composition.AssertComposed();
        Assert.Equal(0, lease.DisposeCount);

        controller.Dispose();
        controller.Dispose();

        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task StartupExceptionKeepsLeaseUntilControllerDisposalThenReleasesItOnce()
    {
        var lease = new RecordingLease();
        var controller = new DesktopStartupController(
            new StubLeaseFactory(DesktopSingleInstanceLeaseAcquisition.Acquired(lease)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartAsync(() => Task.FromException(new InvalidOperationException("test startup failure"))));

        Assert.Equal(0, lease.DisposeCount);

        controller.Dispose();
        controller.Dispose();

        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task AsyncShutdownCompletesComponentsBeforeLeaseReleaseAndFinalApplicationShutdown()
    {
        var events = new List<string>();
        var overlayCanComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new RecordingLease(() => events.Add("lease-released"));
        var shutdown = new DesktopShutdownCoordinator(
            () => ValueTask.CompletedTask,
            DisposeOverlayAsync,
            DisposeCoordinatorAsync,
            lease);

        Task closeTask = shutdown.RequestApplicationShutdownAsync(() => events.Add("application-shutdown"));

        Assert.Equal(["overlay-dispose-started"], events);
        Assert.Equal(0, lease.DisposeCount);

        overlayCanComplete.SetResult(true);
        await closeTask;

        Assert.Equal(
            ["overlay-dispose-started", "overlay-dispose-completed", "coordinator-disposed", "lease-released", "application-shutdown"],
            events);

        async ValueTask DisposeOverlayAsync()
        {
            events.Add("overlay-dispose-started");
            await overlayCanComplete.Task;
            events.Add("overlay-dispose-completed");
        }

        ValueTask DisposeCoordinatorAsync()
        {
            events.Add("coordinator-disposed");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task TimedOutComponentShutdownStillReleasesLeaseAndRequestsApplicationShutdown()
    {
        var events = new List<string>();
        var lease = new RecordingLease(() => events.Add("lease-released"));
        var shutdown = new DesktopShutdownCoordinator(
            () => ValueTask.CompletedTask,
            () => new ValueTask(new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task),
            () => { events.Add("coordinator-disposed"); return ValueTask.CompletedTask; },
            lease,
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TimeoutException>(
            () => shutdown.RequestApplicationShutdownAsync(() => events.Add("application-shutdown")));

        Assert.Equal(["coordinator-disposed", "lease-released", "application-shutdown"], events);
    }

    [Fact]
    public async Task HotkeyDisposalCompletesBeforeOverlayCoordinatorLeaseReleaseAndFinalApplicationShutdown()
    {
        var events = new List<string>();
        var lease = new RecordingLease(() => events.Add("lease-released"));
        var shutdown = new DesktopShutdownCoordinator(
            DisposeHotkeysAsync,
            DisposeOverlayAsync,
            DisposeCoordinatorAsync,
            lease);

        await shutdown.RequestApplicationShutdownAsync(() => events.Add("application-shutdown"));

        Assert.Equal(
            ["hotkeys-disposed", "overlay-disposed", "coordinator-disposed", "lease-released", "application-shutdown"],
            events);

        ValueTask DisposeHotkeysAsync()
        {
            events.Add("hotkeys-disposed");
            return ValueTask.CompletedTask;
        }

        ValueTask DisposeOverlayAsync()
        {
            events.Add("overlay-disposed");
            return ValueTask.CompletedTask;
        }

        ValueTask DisposeCoordinatorAsync()
        {
            events.Add("coordinator-disposed");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task HotkeyDisposalFailureStillDisposesLaterComponentsReleasesLeaseAndRequestsFinalShutdown()
    {
        var events = new List<string>();
        var lease = new RecordingLease(() => events.Add("lease-released"));
        var shutdown = new DesktopShutdownCoordinator(
            DisposeFailingHotkeysAsync,
            DisposeOverlayAsync,
            DisposeCoordinatorAsync,
            lease);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => shutdown.RequestApplicationShutdownAsync(() => events.Add("application-shutdown")));

        Assert.Equal(
            ["hotkeys-dispose-failed", "overlay-disposed", "coordinator-disposed", "lease-released", "application-shutdown"],
            events);

        ValueTask DisposeFailingHotkeysAsync()
        {
            events.Add("hotkeys-dispose-failed");
            return ValueTask.FromException(new InvalidOperationException("test hotkey disposal failure"));
        }

        ValueTask DisposeOverlayAsync()
        {
            events.Add("overlay-disposed");
            return ValueTask.CompletedTask;
        }

        ValueTask DisposeCoordinatorAsync()
        {
            events.Add("coordinator-disposed");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DuplicateCloseAndDisposeDoNotRepeatComponentDisposalLeaseReleaseOrFinalShutdown()
    {
        var overlayDisposeCount = 0;
        var coordinatorDisposeCount = 0;
        var applicationShutdownCount = 0;
        var lease = new RecordingLease();
        var shutdown = new DesktopShutdownCoordinator(
            () => ValueTask.CompletedTask,
            () =>
            {
                overlayDisposeCount++;
                return ValueTask.CompletedTask;
            },
            () =>
            {
                coordinatorDisposeCount++;
                return ValueTask.CompletedTask;
            },
            lease);

        Task firstClose = shutdown.RequestApplicationShutdownAsync(() => applicationShutdownCount++);
        Task duplicateClose = shutdown.RequestApplicationShutdownAsync(() => applicationShutdownCount++);

        Assert.Same(firstClose, duplicateClose);
        await firstClose;
        shutdown.Dispose();
        shutdown.Dispose();

        Assert.Equal(1, overlayDisposeCount);
        Assert.Equal(1, coordinatorDisposeCount);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(1, applicationShutdownCount);
    }

    [Fact]
    public async Task ShutdownFailureStillDisposesCoordinatorReleasesLeaseAndRequestsApplicationShutdown()
    {
        var events = new List<string>();
        var lease = new RecordingLease(() => events.Add("lease-released"));
        var shutdown = new DesktopShutdownCoordinator(
            () => ValueTask.CompletedTask,
            DisposeFailingOverlayAsync,
            DisposeCoordinatorAsync,
            lease);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => shutdown.RequestApplicationShutdownAsync(() => events.Add("application-shutdown")));

        Assert.Equal(["overlay-dispose-failed", "coordinator-disposed", "lease-released", "application-shutdown"], events);

        ValueTask DisposeFailingOverlayAsync()
        {
            events.Add("overlay-dispose-failed");
            return ValueTask.FromException(new InvalidOperationException("test overlay shutdown failure"));
        }

        ValueTask DisposeCoordinatorAsync()
        {
            events.Add("coordinator-disposed");
            return ValueTask.CompletedTask;
        }
    }

    private static string CreateTestMutexName() => $"Global\\SoulsTracker.Desktop.Tests.{Guid.NewGuid():N}";

    private sealed class DesktopCompositionProbe
    {
        public int OverlayPublisherCreated { get; private set; }
        public int RepositoryCreated { get; private set; }
        public int CoordinatorCreated { get; private set; }
        public int OverlayServiceCreated { get; private set; }
        public int ViewModelCreated { get; private set; }
        public int MainWindowCreated { get; private set; }

        public Task ComposeAndStartAsync()
        {
            OverlayPublisherCreated++;
            RepositoryCreated++;
            CoordinatorCreated++;
            OverlayServiceCreated++;
            ViewModelCreated++;
            MainWindowCreated++;
            return Task.CompletedTask;
        }

        public void AssertNotComposed()
        {
            Assert.Equal(0, OverlayPublisherCreated);
            Assert.Equal(0, RepositoryCreated);
            Assert.Equal(0, CoordinatorCreated);
            Assert.Equal(0, OverlayServiceCreated);
            Assert.Equal(0, ViewModelCreated);
            Assert.Equal(0, MainWindowCreated);
        }

        public void AssertComposed()
        {
            Assert.Equal(1, OverlayPublisherCreated);
            Assert.Equal(1, RepositoryCreated);
            Assert.Equal(1, CoordinatorCreated);
            Assert.Equal(1, OverlayServiceCreated);
            Assert.Equal(1, ViewModelCreated);
            Assert.Equal(1, MainWindowCreated);
        }
    }

    private sealed class StubLeaseFactory(DesktopSingleInstanceLeaseAcquisition acquisition) : IDesktopSingleInstanceLeaseFactory
    {
        public DesktopSingleInstanceLeaseAcquisition Acquire() => acquisition;
    }

    private sealed class ThrowingNamedMutexFactory : INamedMutexFactory
    {
        public INamedMutexHandle CreateInitiallyOwned(string mutexName, out bool createdNew)
        {
            createdNew = false;
            throw new InvalidOperationException("test mutex factory failure");
        }
    }

    private sealed class StubNamedMutexFactory(INamedMutexHandle mutex, bool createdNew) : INamedMutexFactory
    {
        public INamedMutexHandle CreateInitiallyOwned(string mutexName, out bool returnedCreatedNew)
        {
            returnedCreatedNew = createdNew;
            return mutex;
        }
    }

    private sealed class RecordingNamedMutexHandle : INamedMutexHandle
    {
        public int ReleaseCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void ReleaseMutex() => ReleaseCount++;

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingLease(Action? onDispose = null) : IDesktopSingleInstanceLease
    {
        private readonly Action? onDispose = onDispose;

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            onDispose?.Invoke();
        }
    }
}
