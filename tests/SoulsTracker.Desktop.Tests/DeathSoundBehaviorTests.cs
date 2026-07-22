using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using System.IO;

namespace SoulsTracker.Desktop.Tests;

public sealed class DeathSoundBehaviorTests
{
    [Fact]
    public async Task ManualSoundPlaysOnlyAfterAPersistedIncrement()
    {
        string sound = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        await File.WriteAllBytesAsync(sound, []);
        try
        {
            var repository = new MemoryRepository(new PersistentTrackerState(1, GameId.Bloodborne, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default, deathSound: new DeathSoundConfiguration(sound, true, 100)));
            await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            var viewModel = new DesktopTrackerViewModel(coordinator);
            var player = new RecordingPlayer();
            viewModel.ConfigureDeathSoundPlayback(player);
            await viewModel.InitializeAsync();
            await viewModel.DecrementManualDeathsAsync();
            Assert.Equal(0, player.Count);
            repository.FailSaves = true;
            await viewModel.IncrementManualDeathsAsync();
            Assert.Equal(0, player.Count);
            repository.FailSaves = false;
            await viewModel.IncrementManualDeathsAsync();
            Assert.Equal(1, player.Count);
        }
        finally { File.Delete(sound); }
    }

    [Fact]
    public void AutomatedSoundResetsForInterruptedOrChangedReaderContextsAndPlaysOnlyForSameContinuityIncrease()
    {
        var player = new RecordingPlayer();
        var notifier = new AutomatedDeathSoundNotifier(player);
        DeathSoundConfiguration config = new(null, true, 100);
        DateTimeOffset t = DateTimeOffset.UtcNow;
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 4, t)), config);
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.WaitingForActiveCharacter(GameId.Ds1), config);
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 9, t.AddSeconds(1))), config);
        notifier.Observe(GameId.Ds2, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 10, t.AddSeconds(2))), config);
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 10, t.AddSeconds(3))), config);
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 7, t.AddSeconds(4))), config);
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 11, t.AddSeconds(5))), config);
        notifier.Observe(GameId.Ds1, null, config);
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 11, t.AddSeconds(6))), config);
        notifier.Observe(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 12, t.AddSeconds(7))), config);
        Assert.Equal(1, player.Count);
    }

    [Fact]
    public async Task WpfPlayerRetainsOneActiveMediaInstanceUntilCleanup()
    {
        string sound = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        await File.WriteAllBytesAsync(sound, []);
        try
        {
            var factory = new RecordingMediaFactory();
            var player = new WpfDeathSoundPlayer(factory);
            var config = new DeathSoundConfiguration(sound, true, 60);
            player.Play(config); player.Play(config);
            RecordingMedia media = Assert.Single(factory.Created);
            Assert.True(player.IsPlaying); Assert.Equal(1, media.PlayCount);
            media.RaiseEnded();
            Assert.False(player.IsPlaying); Assert.Equal(1, media.CloseCount);
            player.Play(config);
            Assert.Equal(2, factory.Created.Count);
            factory.Created[1].RaiseFailed();
            Assert.False(player.IsPlaying);
        }
        finally { File.Delete(sound); }
    }

    [Fact]
    public async Task MissingSelectedFileUsesGenericUnavailableStatus()
    {
        const string missing = "C:\\private\\missing-death.wav";
        var repository = new MemoryRepository(new PersistentTrackerState(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default, deathSound: new DeathSoundConfiguration(missing, true, 100)));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var viewModel = new DesktopTrackerViewModel(coordinator);
        await viewModel.InitializeAsync();
        Assert.Equal("Death sound is unavailable.", viewModel.DeathSoundStatus);
        Assert.DoesNotContain("private", viewModel.DeathSoundStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing-death", viewModel.DeathSoundStatus!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewUsesSavedConfigurationWithoutPersistingAndReportsPlaybackFailure()
    {
        string sound = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        await File.WriteAllBytesAsync(sound, []);
        try
        {
            var repository = new MemoryRepository(new PersistentTrackerState(1, null, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty, OverlayConfiguration.Default, deathSound: new DeathSoundConfiguration(sound, true, 37)));
            await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            var viewModel = new DesktopTrackerViewModel(coordinator);
            var player = new RecordingPlayer();
            viewModel.ConfigureDeathSoundPlayback(player);
            await viewModel.InitializeAsync();

            viewModel.PreviewDeathSound();

            Assert.Equal(1, player.Count);
            Assert.Equal(37, player.LastConfiguration!.Volume);
            Assert.Equal("Playing death sound.", viewModel.DeathSoundStatus);
            Assert.Equal(0, repository.SaveCount);
            player.RaiseEnded();
            Assert.Equal("Death sound ready.", viewModel.DeathSoundStatus);
            viewModel.PreviewDeathSound();
            player.RaiseFailed();
            Assert.Equal("Unable to play death sound.", viewModel.DeathSoundStatus);
        }
        finally { File.Delete(sound); }
    }

    private sealed class RecordingPlayer : IDeathSoundPlayer
    {
        public event EventHandler? PlaybackEnded;
        public event EventHandler? PlaybackFailed;
        public int Count { get; private set; }
        public DeathSoundConfiguration? LastConfiguration { get; private set; }
        public void Play(DeathSoundConfiguration configuration) { Count++; LastConfiguration = configuration; }
        public void RaiseEnded() => PlaybackEnded?.Invoke(this, EventArgs.Empty);
        public void RaiseFailed() => PlaybackFailed?.Invoke(this, EventArgs.Empty);
    }
    private sealed class RecordingMediaFactory : ILocalDeathSoundMediaFactory { public List<RecordingMedia> Created { get; } = []; public ILocalDeathSoundMedia Create() { var media = new RecordingMedia(); Created.Add(media); return media; } }
    private sealed class RecordingMedia : ILocalDeathSoundMedia
    {
        public event EventHandler? Ended; public event EventHandler? Failed;
        public int PlayCount { get; private set; }
        public int CloseCount { get; private set; }
        public void Open(Uri source) { }
        public void SetVolume(double volume) { }
        public void Play() => PlayCount++; public void Close() => CloseCount++;
        public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);
        public void RaiseFailed() => Failed?.Invoke(this, EventArgs.Empty);
    }
    private sealed class NullPublisher : ITrackerStateChangePublisher { public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class MemoryRepository(PersistentTrackerState state) : ITrackerStateRepository
    {
        public bool FailSaves { get; set; }
        public int SaveCount { get; private set; }
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
        public Task SaveAsync(PersistentTrackerState value, CancellationToken cancellationToken = default) { if (FailSaves) throw new IOException(); SaveCount++; state = value; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
