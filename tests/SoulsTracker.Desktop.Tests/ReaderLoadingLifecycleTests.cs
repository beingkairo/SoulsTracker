using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

public sealed class ReaderLoadingLifecycleTests
{
    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task PendingDiscoveryCannotTurnInitialLoadingIntoFailure(string game)
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new ControlledDiscovery();
        await using var vm = ViewModel(coordinator, discovery);
        await vm.InitializeAsync();
        var request = discovery.Enqueue();
        Task selection = vm.SelectGameAsync(vm.GameChoices.Single(choice => choice.GameId == Game(game)));
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(vm.IsSaveReaderSourcePending);
            Assert.Equal("Loading.", vm.TotalDeathsText);
            Assert.Equal("Loading.", vm.RuntimeReaderStatusText);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.WaitingForSaveFile(Game(game)));
            vm.ApplyRuntimeReaderResult(null);
            Assert.Equal("Loading.", vm.TotalDeathsText);
        }
        finally { request.Result.TrySetResult([]); }
        await selection;
        Assert.False(vm.IsSaveReaderSourcePending);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.WaitingForSaveFile(Game(game)));
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
        Assert.NotEqual("Loading.", vm.RuntimeReaderStatusText);
    }

    [Theory]
    [InlineData("er", false)]
    [InlineData("wk", false)]
    [InlineData("lp", false)]
    [InlineData("er", true)]
    [InlineData("wk", true)]
    [InlineData("lp", true)]
    public async Task DiscoveryFailureOrCancellationReleasesInitialRead(string game, bool cancel)
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new ControlledDiscovery();
        await using var vm = ViewModel(coordinator, discovery);
        await vm.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var request = discovery.Enqueue();
        Task selection = vm.SelectGameAsync(vm.GameChoices.Single(choice => choice.GameId == Game(game)), cancellation.Token);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) cancellation.Cancel();
        request.Result.SetException(cancel ? new OperationCanceledException(cancellation.Token) : new IOException("Fixture discovery failure"));
        await selection;
        Assert.False(vm.IsSaveReaderSourcePending);
        Assert.True(vm.CanPublishSaveReaderResult(vm.SaveReaderResolutionVersion));
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(Game(game)));
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task RefreshRetainsValidatedTotalAndRejectsReadsStartedBeforeResolution(string game)
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string path = CreateSave(game, fixture.Root);
        var repository = new MemoryRepository(Game(game)) { State = Configured(game, path) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new ControlledDiscovery();
        await using var vm = ViewModel(coordinator, discovery);
        await vm.InitializeAsync();
        vm.ApplyRuntimeReaderResult(Synced(repository.State, 37));
        long beforeRefresh = vm.SaveReaderResolutionVersion;
        var request = discovery.Enqueue();
        Task refresh = Rescan(vm, game);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(vm.CanPublishSaveReaderResult(beforeRefresh));
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.SelectedSaveUnreadable(Game(game)));
            Assert.Equal("37", vm.TotalDeathsText);
        }
        finally { request.Result.TrySetResult([]); }
        await refresh;
        Assert.False(vm.IsSaveReaderSourcePending);
        Assert.False(vm.CanPublishSaveReaderResult(beforeRefresh));
        Assert.Equal("37", vm.TotalDeathsText);
        Assert.True(vm.CanPublishSaveReaderResult(vm.SaveReaderResolutionVersion));
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.SelectedSaveUnreadable(Game(game)));
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
    }

    [Theory]
    [InlineData("er", false)]
    [InlineData("wk", false)]
    [InlineData("lp", false)]
    [InlineData("er", true)]
    [InlineData("wk", true)]
    [InlineData("lp", true)]
    public async Task SupersededRefreshCannotHoldOrClearNewerResolution(string game, bool oldCompletesFirst)
    {
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new ControlledDiscovery();
        await using var vm = ViewModel(coordinator, discovery);
        await vm.InitializeAsync();
        var old = discovery.Enqueue();
        Task oldRefresh = Rescan(vm, game);
        await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = discovery.Enqueue();
        Task currentRefresh = Rescan(vm, game);
        await current.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (oldCompletesFirst)
        {
            old.Result.SetResult([]);
            await oldRefresh;
            Assert.True(vm.IsSaveReaderSourcePending);
        }
        current.Result.SetResult([]);
        await currentRefresh;
        Assert.False(vm.IsSaveReaderSourcePending);
        long readyVersion = vm.SaveReaderResolutionVersion;
        if (!oldCompletesFirst)
        {
            old.Result.SetResult([]);
            await oldRefresh;
            Assert.Equal(readyVersion, vm.SaveReaderResolutionVersion);
        }
        Assert.True(vm.CanPublishSaveReaderResult(readyVersion));
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(Game(game)));
        Assert.NotEqual("Loading.", vm.TotalDeathsText);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task CommittedNewSourceDoesNotWaitForTheOldSourcesDiscovery(string game)
    {
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new ControlledDiscovery();
        await using var vm = ViewModel(coordinator, discovery);
        await vm.InitializeAsync();
        var request = discovery.Enqueue();
        Task refresh = Rescan(vm, game);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsSaveReaderSourcePending);
        var newState = RuntimePublicationSessionTests.Selected(Game(game), "new");
        vm.ApplyImportedCommittedState(newState);
        Assert.False(vm.IsSaveReaderSourcePending);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        vm.ApplyRuntimeReaderResult(Synced(newState, 23));
        Assert.Equal("23", vm.TotalDeathsText);
        request.Result.SetResult([]);
        await refresh;
        Assert.False(vm.IsSaveReaderSourcePending);
        Assert.Equal("23", vm.TotalDeathsText);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task RapidGameReselectionIgnoresOldDiscoveryAndCachedResults(string game)
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new ControlledDiscovery();
        await using var vm = ViewModel(coordinator, discovery);
        await vm.InitializeAsync();
        var old = discovery.Enqueue();
        Task first = vm.SelectGameAsync(vm.GameChoices.Single(choice => choice.GameId == Game(game)));
        await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.SelectGameAsync(vm.GameChoices.Single(choice => choice.GameId == GameId.DemonsSouls));
        var current = discovery.Enqueue();
        Task second = vm.SelectGameAsync(vm.GameChoices.Single(choice => choice.GameId == Game(game)));
        await current.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        old.Result.SetResult([]);
        await first;
        Assert.True(vm.IsSaveReaderSourcePending);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        current.Result.SetResult([]);
        await second;
        Assert.False(vm.IsSaveReaderSourcePending);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(Game(game)));
        Assert.NotEqual("Loading.", vm.TotalDeathsText);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    public async Task RealSaveCacheReparsesOnReselectionWithoutPublishingOldObservations(string game)
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string path = CreateSave(game, fixture.Root);
        if (game == "er")
        {
            byte[] contents = await File.ReadAllBytesAsync(path);
            // Add one synthetic v252 character to the profile-metadata fixture.
            Array.Resize(ref contents, 0x6000 + 400_000 + 0x10);
            BinaryPrimitives.WriteUInt64LittleEndian(contents.AsSpan(0x48), 400_000 + 0x10);
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(0x50), 0x6000);
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(0x6010), 252);
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(0x6010 + 0x34E1F), 7);
            await File.WriteAllBytesAsync(path, contents);
        }
        var repository = new MemoryRepository(Game(game)) { State = Configured(game, path) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var er = new EldenRingSaveDeathReader();
        var wk = new BlackMythWukongSaveDeathReader();
        IRuntimeGameDeathReader reader = game == "er" ? er : wk;
        var readers = new RuntimeGameReaderCoordinator([reader]);
        var session = new RuntimePublicationSession();
        var outputs = new List<RuntimeGameReadResult?>();
        async Task Poll()
        {
            var state = repository.State;
            var ticket = session.BeginRead(state);
            er.Configure(state.EldenRingSave, session.RequiresFreshSaveRead);
            wk.Configure(state.BlackMythWukongSave, session.RequiresFreshSaveRead);
            var result = await readers.PollAsync(state.SelectedGameId, default, EffectiveDeathTotalResult.SourceIdentityFor(state));
            session.CompleteRead(ticket, state, result, vm.ApplyRuntimeReaderResult, outputs.Add);
        }
        await Poll();
        Assert.Equal("7", vm.TotalDeathsText);
        await Poll();
        Assert.Single(outputs);
        var initial = outputs[0]!.Observation;
        await vm.SelectGameAsync(vm.GameChoices.Single(choice => choice.GameId == GameId.DemonsSouls));
        session.SelectState(repository.State);
        await vm.SelectGameAsync(vm.GameChoices.Single(choice => choice.GameId == Game(game)));
        session.SelectState(repository.State);
        Assert.True(session.RequiresFreshSaveRead);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        var cached = await reader.ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.Cached, cached!.Status);
        session.CompleteRead(session.BeginRead(repository.State), repository.State, cached, vm.ApplyRuntimeReaderResult, outputs.Add);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        Assert.Single(outputs);
        await Poll();
        Assert.Equal("7", vm.TotalDeathsText);
        Assert.Equal(2, outputs.Count);
        Assert.NotSame(initial, outputs[1]!.Observation);
        Assert.All(outputs, result => Assert.Equal(RuntimeGameReaderStatus.Synced, result!.Status));
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task DelayedReadAndOldSaveCacheCannotEndNewSourcesLoading(string game)
    {
        string name = game == "er" ? "ER0000.sl2" : game == "wk" ? "ArchiveSaveFile.1.sav" : "SaveData-1_Character_1.sav";
        var oldState = Configured(game, Path.GetFullPath(Path.Combine("fixtures", "old", name)));
        var newState = Configured(game, Path.GetFullPath(Path.Combine("fixtures", "new", name)));
        var repository = new MemoryRepository(Game(game)) { State = oldState };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var session = new RuntimePublicationSession();
        int outputs = 0;
        var ticket = session.BeginRead(oldState);
        var delayed = new TaskCompletionSource<RuntimeGameReadResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ApplyImportedCommittedState(newState);
        session.SelectState(newState);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        delayed.SetResult(Synced(oldState, 91));
        session.CompleteRead(ticket, newState, await delayed.Task, vm.ApplyRuntimeReaderResult, _ => outputs++);
        session.CompleteRead(session.BeginRead(newState), newState, RuntimeGameReadResult.Cached(Synced(oldState, 91)), vm.ApplyRuntimeReaderResult, _ => outputs++);
        session.CompleteRead(session.BeginRead(newState), newState, RuntimeGameReadResult.Unavailable(Game(game), Synced(oldState, 91).Observation), vm.ApplyRuntimeReaderResult, _ => outputs++);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        Assert.Equal(0, outputs);
        session.CompleteRead(session.BeginRead(newState), newState, Synced(newState, 12), vm.ApplyRuntimeReaderResult, _ => outputs++);
        Assert.Equal("12", vm.TotalDeathsText);
        Assert.Equal(1, outputs);
        session.CompleteRead(session.BeginRead(newState), newState, RuntimeGameReadResult.SelectedSaveUnreadable(Game(game)), vm.ApplyRuntimeReaderResult, _ => outputs++);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task RetentionKeepsOnlyTheSelectedSaveAndNewSourceFailureEndsLoading(string game)
    {
        var oldState = RuntimePublicationSessionTests.Selected(Game(game), "old");
        var newState = RuntimePublicationSessionTests.Selected(Game(game), "new");
        var repository = new MemoryRepository(Game(game)) { State = oldState };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        await using var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var reader = new MutableReader(Game(game)) { Result = Synced(oldState, 91) };
        var readers = new RuntimeGameReaderCoordinator([reader]);
        var session = new RuntimePublicationSession();
        var outputs = new List<RuntimeGameReadResult?>();
        async Task Poll(PersistentTrackerState state)
        {
            var ticket = session.BeginRead(state);
            var result = await readers.PollAsync(state.SelectedGameId, default, EffectiveDeathTotalResult.SourceIdentityFor(state));
            session.CompleteRead(ticket, state, result, vm.ApplyRuntimeReaderResult, outputs.Add);
        }
        await Poll(oldState);
        Assert.Equal("91", vm.TotalDeathsText);
        reader.Result = null;
        await Poll(oldState);
        Assert.Equal("91", vm.TotalDeathsText);
        Assert.Equal(RuntimeGameReaderStatus.Unavailable, outputs[^1]!.Status);
        vm.ApplyImportedCommittedState(newState);
        session.SelectState(newState);
        Assert.Equal("Loading.", vm.TotalDeathsText);
        await Poll(newState);
        Assert.Null(outputs[^1]);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, vm.TotalDeathsText);
        Assert.False(vm.IsSaveReaderSourcePending);
    }

    private sealed class MutableReader(GameId game) : IRuntimeGameDeathReader
    {
        public GameId GameId => game;
        internal RuntimeGameReadResult? Result { get; set; }
        public ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result);
    }

    private static DesktopTrackerViewModel ViewModel(SerializedTrackerCoordinator coordinator, ILocalSaveDiscovery discovery) =>
        new(coordinator, blackMythWukongSaveDiscovery: discovery, eldenRingSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);

    private static PersistentTrackerState Configured(string game, string path) =>
        new(1, Game(game), OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true,
            eldenRingSave: game == "er" ? new(path, 0) : null,
            blackMythWukongSave: game == "wk" ? new(path) : null,
            liesOfPSave: game == "lp" ? new(path) : null);

    private static RuntimeGameReadResult Synced(PersistentTrackerState state, long deaths) =>
        RuntimeGameReadResult.Synced(new(state.SelectedGameId, deaths, DateTimeOffset.UtcNow,
            EffectiveDeathTotalResult.SourceIdentityFor(state)));

    private sealed class ControlledDiscovery : ILocalSaveDiscovery
    {
        private readonly ConcurrentQueue<Request> requests = new();
        internal Request Enqueue() { var request = new Request(); requests.Enqueue(request); return request; }
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken)
        {
            if (!requests.TryDequeue(out var request)) return ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([]);
            request.Entered.SetResult();
            return new(request.Result.Task);
        }
    }

    private sealed class Request
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyList<DiscoveredLocalSave>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
