using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class DesktopTrackerViewModelCharacterizationTests
{
    [Fact]
    public async Task UpdateCheckProjectsNormalizedVersionAndAvailableRelease()
    {
        var checker = new FakeChecker(new(ManualReleaseUpdateStatus.UpdateAvailable, "v2.0.0", new Uri("https://github.com/beingkairo/SoulsTracker/releases/tag/v2.0.0")));
        await using var coordinator = NewCoordinator();
        var tracker = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "v1.2.3+local");
        await tracker.InitializeAsync();

        await tracker.CheckForUpdatesAsync();

        Assert.Equal("1.2.3", tracker.UpdateCurrentVersion);
        Assert.Equal("v2.0.0", tracker.UpdateLatestVersion);
        Assert.Equal("New version out!", tracker.UpdateCheckStatus);
        Assert.True(tracker.CanOpenAvailableUpdateReleasePage);
        Assert.True(tracker.CanCheckForUpdates);
    }

    [Fact]
    public async Task RetryableFailureExposesRetryAndOfficialReleasePage()
    {
        await using var coordinator = NewCoordinator();
        var tracker = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: new FakeChecker(new(ManualReleaseUpdateStatus.Unavailable)), installedVersionProvider: () => "1.0.0");
        await tracker.InitializeAsync();

        await tracker.CheckForUpdatesAsync();

        Assert.True(tracker.CanCheckForUpdates);
        Assert.Equal("Unavailable", tracker.UpdateLatestVersion);
        Assert.Equal(new Uri("https://beingkairo.com/tools/souls-tracker/"), tracker.AvailableUpdateReleasePage);
    }

    [Fact]
    public async Task CancelledUpdateCheckLeavesRetryAvailable()
    {
        await using var coordinator = NewCoordinator();
        var tracker = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: new CancelledChecker(), installedVersionProvider: () => "1.0.0");
        await tracker.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await tracker.CheckForUpdatesAsync(cancellation.Token);

        Assert.True(tracker.CanCheckForUpdates);
        Assert.Equal("Update check cancelled. Try again when you’re ready.", tracker.UpdateCheckStatus);
        Assert.Equal("Unavailable", tracker.UpdateLatestVersion);
    }

    private static SerializedTrackerCoordinator NewCoordinator() => new(new MemoryRepository(), new NullPublisher());

    private sealed class FakeChecker(ManualReleaseUpdateResult result) : IManualReleaseUpdateChecker
    {
        public ValueTask<ManualReleaseUpdateResult> CheckAsync(string installedVersion, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    }
    private sealed class CancelledChecker : IManualReleaseUpdateChecker
    {
        public ValueTask<ManualReleaseUpdateResult> CheckAsync(string installedVersion, CancellationToken cancellationToken = default) => ValueTask.FromException<ManualReleaseUpdateResult>(new OperationCanceledException(cancellationToken));
    }
    private sealed class NullPublisher : ITrackerStateChangePublisher { public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class MemoryRepository : ITrackerStateRepository
    {
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(PersistentTrackerState.Default));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
