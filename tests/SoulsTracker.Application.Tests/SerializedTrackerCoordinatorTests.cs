using SoulsTracker.Domain;

namespace SoulsTracker.Application.Tests;

public sealed class SerializedTrackerCoordinatorTests
{
    [Fact]
    public async Task ConcurrentInitializationLoadsRepositoryOnceAndReturnsCommittedState()
    {
        var repository = new Repository();
        var publisher = new Publisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);

        TrackerStateLoadResult[] results = await Task.WhenAll(
            coordinator.InitializeAsync(), coordinator.InitializeAsync(), coordinator.InitializeAsync());

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, repository.LoadCount);
    }

    [Fact]
    public async Task EndpointAndHotkeyChangesPersistWithoutPublishing()
    {
        var repository = new Repository();
        var publisher = new Publisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        await coordinator.InitializeAsync();
        var endpoint = new OverlayEndpointConfiguration(4321, OverlayAccessToken.Parse(new string('A', 43)));
        var hotkeys = new ManualBloodborneHotkeyConfiguration(2, 118, 2, 119);

        PersistentTrackerState endpointState = await coordinator.SetOverlayEndpointAsync(endpoint);
        PersistentTrackerState hotkeyState = await coordinator.SetManualBloodborneHotkeysAsync(hotkeys);

        Assert.Equal(endpoint, endpointState.OverlayConfiguration.Endpoint);
        Assert.Equal(hotkeys, hotkeyState.ManualBloodborneHotkeys);
        Assert.Equal(0, publisher.PublishCount);
        Assert.Equal(hotkeys, repository.State.ManualBloodborneHotkeys);
    }

    private sealed class Repository : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = PersistentTrackerState.Default;
        public int LoadCount { get; private set; }
        public object Publisher { get; } = new Publisher();
        public async Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) { LoadCount++; await Task.Delay(1, cancellationToken); return TrackerStateLoadResult.Loaded(State); }
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); State = state; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Publisher : ITrackerStateChangePublisher
    {
        public int PublishCount { get; private set; }
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) { PublishCount++; return Task.CompletedTask; }
    }
}
