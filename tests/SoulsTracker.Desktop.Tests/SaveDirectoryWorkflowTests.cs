using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class SaveDirectoryWorkflowTests : IDisposable
{
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "SoulsTracker.DirectoryWorkflow", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task ManualDirectoryCommitsResolvedSourceAndRetainsChosenRoot(string game)
    {
        string path = CreateSave(game, Path.Combine(Root, "selected", "account"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        byte[] before = File.ReadAllBytes(path);

        await Choose(vm, game, Path.Combine(Root, "selected"));

        Assert.Equal(path, ConfiguredPath(repository.State, game));
        Assert.Equal(Path.Combine(Root, "selected"), DirectoryPath(vm, game));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(repository.Saves);
        Assert.Equal(Path.Combine(Root, "selected"), ConfiguredDirectory(repository.State, game));
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task AmbiguousReplacementRequiresChoiceAndCancelRestoresCommittedDirectory(string game)
    {
        string oldDirectory = Path.Combine(Root, "old");
        string old = CreateSave(game, oldDirectory);
        string replacement = Path.Combine(Root, "replacement");
        string first = CreateSave(game, Path.Combine(replacement, "a"));
        string second = CreateSave(game, Path.Combine(replacement, "b"), 2);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, game, oldDirectory);
        await Choose(vm, game, replacement);
        Assert.Equal(old, ConfiguredPath(repository.State, game));
        Assert.Equal(replacement, DirectoryPath(vm, game));
        Assert.True(SelectorVisible(vm, game));
        Assert.Null(SelectedChoice(vm, game));
        Cancel(vm, game);
        Assert.Equal(oldDirectory, DirectoryPath(vm, game));
        Assert.Equal(old, ConfiguredPath(repository.State, game));
        Assert.DoesNotContain(Choices(vm, game), x => x.LocalPath == first || x.LocalPath == second);
        await Choose(vm, game, replacement);
        await Select(vm, game, Choices(vm, game).Single(x => x.LocalPath == second));
        Assert.Equal(second, ConfiguredPath(repository.State, game));
        Assert.Equal(replacement, ConfiguredDirectory(repository.State, game));
        await Rescan(vm, game);
        Assert.Equal(2, Choices(vm, game).Count);
        Assert.Equal(second, ConfiguredPath(repository.State, game));
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task InvalidEmptyUnusableAndFailedReplacementKeepCommittedConfiguration(string game)
    {
        string old = CreateSave(game, Path.Combine(Root, "old"));
        string replacement = CreateSave(game, Path.Combine(Root, "replacement"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, game, Path.GetDirectoryName(old)!);
        string empty = Path.Combine(Root, "empty"); Directory.CreateDirectory(empty);
        string invalid = CreateSave(game, Path.Combine(Root, "invalid")); File.WriteAllBytes(invalid, [1, 2]);
        foreach (string directory in new[] { Path.Combine(Root, "missing"), empty, old, Path.GetDirectoryName(invalid)!, "\0" })
        {
            await Choose(vm, game, directory);
            Assert.Equal(old, ConfiguredPath(repository.State, game));
            Assert.Equal(Path.GetDirectoryName(old), DirectoryPath(vm, game));
            Assert.Contains("No usable saves", Status(vm, game), StringComparison.Ordinal);
        }
        using (var locked = new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
            await Choose(vm, game, Path.GetDirectoryName(replacement)!);
        Assert.Equal(old, ConfiguredPath(repository.State, game));
        repository.FailSave = true;
        await Choose(vm, game, Path.GetDirectoryName(replacement)!);
        Assert.Equal(old, ConfiguredPath(repository.State, game));
        Assert.Contains("could not be saved", Status(vm, game), StringComparison.Ordinal);
        Cancel(vm, game);
        Assert.Equal(Path.GetDirectoryName(old), DirectoryPath(vm, game));
        Assert.Single(repository.Saves);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task CandidateRemovedBeforeSelectionCannotReplaceAcceptedSource(string game)
    {
        string old = CreateSave(game, Path.Combine(Root, "old"));
        string replacement = Path.Combine(Root, "replacement");
        string removed = CreateSave(game, Path.Combine(replacement, "a"));
        CreateSave(game, Path.Combine(replacement, "b"), 2);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, game, Path.GetDirectoryName(old)!);
        await Choose(vm, game, replacement);
        File.Delete(removed);
        await Select(vm, game, Choices(vm, game).Single(x => x.LocalPath == removed));
        Assert.Equal(old, ConfiguredPath(repository.State, game));
        Assert.Single(repository.Saves);
    }

    [Fact]
    public async Task LiesPairedMemberUpdateDoesNotChangeAcceptedFileIdentityOrWriteOnRestart()
    {
        string selected = CreateSave("lp", Path.Combine(Root, "selected"));
        var repository = new MemoryRepository(GameId.LiesOfP);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, "lp", Path.GetDirectoryName(selected)!);
        string sibling = Path.Combine(Path.GetDirectoryName(selected)!, "SaveData-1_Character_2.sav");
        File.Copy(selected, sibling);
        File.SetLastWriteTimeUtc(sibling, DateTime.UtcNow.AddMinutes(1));
        await Rescan(vm, "lp");
        Assert.Equal(selected, repository.State.LiesOfPSave.LocalPath);
        Assert.Single(repository.Saves);
        Assert.Equal(selected, vm.SelectedLiesOfPSaveChoice!.LocalPath);
        var restarted = CreateViewModel(coordinator);
        await restarted.InitializeAsync();
        Assert.Equal(selected, repository.State.LiesOfPSave.LocalPath);
        Assert.Single(repository.Saves);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task DelayedAutomaticDiscoveryCannotOverwriteNewDirectory(string game)
    {
        string old = CreateSave(game, Path.Combine(Root, "automatic"));
        string selected = CreateSave(game, Path.Combine(Root, "selected"));
        var discovery = new DelayedDiscovery(new(old, "Automatic source"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, blackMythWukongSaveDiscovery: discovery, eldenRingSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        await vm.InitializeAsync();
        discovery.Delay = true;
        Task rescan = Rescan(vm, game);
        await discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Choose(vm, game, Path.GetDirectoryName(selected)!);
        discovery.Release.SetResult();
        await rescan;
        Assert.Equal(selected, ConfiguredPath(repository.State, game));
        Assert.Equal(selected, Assert.Single(Choices(vm, game)).LocalPath);
        Assert.Single(repository.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedEldenProfileCannotCommitAfterCancelOrGameRoundTrip(bool gameSwitch)
    {
        string selected = CreateSave("er", Path.Combine(Root, "selected"));
        var reader = new DelayedProfileReader(selected);
        var repository = new MemoryRepository(GameId.EldenRing);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var empty = new EmptyDiscovery();
        var vm = new DesktopTrackerViewModel(coordinator, reader, empty, empty, liesOfPSaveDiscovery: empty);
        await vm.InitializeAsync();
        Task choose = Choose(vm, "er", Path.GetDirectoryName(selected)!);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task returnToGame = Task.CompletedTask;
        if (gameSwitch)
        {
            await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == GameId.DemonsSouls));
            returnToGame = vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == GameId.EldenRing));
        }
        else Cancel(vm, "er");
        reader.Release.SetResult();
        await choose;
        await returnToGame;
        Assert.Null(repository.State.EldenRingSave.LocalPath);
        Assert.Null(vm.EldenRingDirectoryPath);
        Assert.Empty(vm.EldenRingProfileSlots);
    }

    [Theory]
    [InlineData("er", false)]
    [InlineData("wk", false)]
    [InlineData("lp", false)]
    [InlineData("er", true)]
    [InlineData("wk", true)]
    [InlineData("lp", true)]
    public async Task RestartLoadsDirectoryOrLegacyFileWithoutDiscardingSourceOrWriting(string game, bool legacy)
    {
        string directory = Path.Combine(Root, "selected");
        string path = CreateSave(game, Path.Combine(directory, "account"));
        var repository = new MemoryRepository(Game(game));
        repository.State = new(1, Game(game), OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true,
            eldenRingSave: game == "er" ? new(path, 0, legacy ? null : directory) : null,
            blackMythWukongSave: game == "wk" ? new(path, legacy ? null : directory) : null,
            liesOfPSave: game == "lp" ? new(path, legacy ? null : directory) : null);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        Assert.Equal(path, ConfiguredPath(repository.State, game));
        Assert.Equal(legacy ? Path.GetDirectoryName(path) : directory, DirectoryPath(vm, game));
        if (!legacy) Assert.Equal(path, Assert.Single(Choices(vm, game)).LocalPath);
        if (game == "er") Assert.Equal(0, vm.SelectedEldenRingProfileSlot!.Index);
        Assert.Empty(repository.Saves);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task AutomaticDefaultSingleSourceUsesItsDirectoryWithoutPrompt(string game)
    {
        string path = CreateSave(game, Path.Combine(Root, "automatic"));
        var discovery = new FixedDiscovery(new(path, "Automatic source"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, blackMythWukongSaveDiscovery: discovery, eldenRingSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        await vm.InitializeAsync();
        Assert.Equal(path, ConfiguredPath(repository.State, game));
        Assert.Equal(Path.GetDirectoryName(path), DirectoryPath(vm, game));
        Assert.Single(repository.Saves);
        Assert.NotNull(SelectedChoice(vm, game));
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task DirectorySwitchFencesDelayedRuntimeReadAndPreservesBytes(string game)
    {
        string old = CreateSave(game, Path.Combine(Root, "old"));
        string next = CreateSave(game, Path.Combine(Root, "next"));
        byte[] before = File.ReadAllBytes(old);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        await Choose(vm, game, Path.GetDirectoryName(old)!);
        var session = new RuntimePublicationSession();
        var previous = repository.State;
        var read = session.BeginRead(previous);
        await Choose(vm, game, Path.GetDirectoryName(next)!);
        session.SelectState(repository.State);
        int published = 0;
        var observation = new RuntimeGameObservation(Game(game), 22, DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(previous));
        session.CompleteRead(read, previous, RuntimeGameReadResult.Synced(observation), _ => published++, _ => published++);
        Assert.Equal(0, published);
        Assert.Equal(before, File.ReadAllBytes(old));
        Assert.Equal(next, ConfiguredPath(repository.State, game));
    }

    [Fact]
    public async Task WukongDelayedMetadataCannotReplaceNewDirectoryMetadata()
    {
        string old = CreateSave("wk", Path.Combine(Root, "old"));
        string next = CreateSave("wk", Path.Combine(Root, "next"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WukongSaveMetadataReadResult> Metadata(string path, CancellationToken cancellationToken)
        {
            if (path == old) { entered.TrySetResult(); await release.Task; }
            return new(true, new(path == old ? 11 : 22, null, null));
        }
        var repository = new MemoryRepository(GameId.BlackMythWukong);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var empty = new EmptyDiscovery();
        var vm = new DesktopTrackerViewModel(coordinator, null, empty, empty, null, Metadata, empty);
        await vm.InitializeAsync();
        Task first = Choose(vm, "wk", Path.GetDirectoryName(old)!);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Choose(vm, "wk", Path.GetDirectoryName(next)!);
        release.SetResult(); await first;
        Assert.Equal("Level 22", vm.BlackMythWukongSaveMetadataText);
        Assert.Equal(next, repository.State.BlackMythWukongSave.LocalPath);
    }

    [Fact]
    public async Task EldenRestartDoesNotInventACharacterSelection()
    {
        string directory = Path.Combine(Root, "selected");
        string path = CreateSave("er", directory);
        var repository = new MemoryRepository(GameId.EldenRing) { State = new(1, GameId.EldenRing, OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true, eldenRingSave: new(path, -1, directory)) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        Assert.Equal(-1, repository.State.EldenRingSave.SlotIndex);
        Assert.Null(vm.SelectedEldenRingProfileSlot);
        Assert.Single(vm.EldenRingProfileSlots);
        Assert.Empty(repository.Saves);
    }

    [Fact]
    public async Task DelayedDefaultEldenProfileCannotOverwriteManualDirectory()
    {
        string old = CreateSave("er", Path.Combine(Root, "automatic"));
        string next = CreateSave("er", Path.Combine(Root, "selected"));
        var discovery = new DelayedDiscovery(new(old, "Automatic source"));
        var reader = new DelayedProfileReader(old);
        var repository = new MemoryRepository(GameId.EldenRing);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var empty = new EmptyDiscovery();
        var vm = new DesktopTrackerViewModel(coordinator, reader, empty, discovery, liesOfPSaveDiscovery: empty);
        await vm.InitializeAsync();
        discovery.Delay = true; discovery.Release.SetResult();
        Task first = vm.RescanEldenRingSavesAsync();
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Choose(vm, "er", Path.GetDirectoryName(next)!);
        reader.Release.SetResult(); await first;
        Assert.Equal(next, repository.State.EldenRingSave.LocalPath);
        Assert.Single(repository.Saves);
    }

    private sealed class FixedDiscovery(DiscoveredLocalSave choice) : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([choice]);
    }

    private sealed class DelayedDiscovery(DiscoveredLocalSave choice) : ILocalSaveDiscovery
    {
        internal bool Delay { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken)
        {
            if (!Delay) return [];
            Entered.TrySetResult(); await Release.Task; return [choice];
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableEldenProfilesCannotReplaceSourceOrWriteOnRestart(bool throws)
    {
        string old = CreateSave("er", Path.Combine(Root, "old"));
        string next = CreateSave("er", Path.Combine(Root, "next"));
        var repository = new MemoryRepository(GameId.EldenRing) { State = new(1, GameId.EldenRing, OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true, eldenRingSave: new(old, 0, Path.GetDirectoryName(old))) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var empty = new EmptyDiscovery();
        var vm = new DesktopTrackerViewModel(coordinator, new UnavailableProfileReader(throws), empty, empty, liesOfPSaveDiscovery: empty);
        await vm.InitializeAsync();
        Assert.Empty(repository.Saves);
        Assert.Equal(0, repository.State.EldenRingSave.SlotIndex);
        await Choose(vm, "er", Path.GetDirectoryName(next)!);
        Assert.Empty(repository.Saves);
        Assert.Equal(old, repository.State.EldenRingSave.LocalPath);
        Assert.Equal(0, repository.State.EldenRingSave.SlotIndex);
        Assert.Contains("unavailable", vm.EldenRingSaveDiscoveryStatus, StringComparison.Ordinal);
        Cancel(vm, "er");
        Assert.Equal(Path.GetDirectoryName(old), vm.EldenRingDirectoryPath);
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task PersistenceOwnsSelectionUntilCommitCompletes(string game)
    {
        string first = CreateSave(game, Path.Combine(Root, "first"));
        string second = CreateSave(game, Path.Combine(Root, "second"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        repository.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.ReleaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task choose = Choose(vm, game, Path.GetDirectoryName(first)!);
        await repository.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(vm.ControlsEnabled);
            Cancel(vm, game);
            Assert.Equal(Path.GetDirectoryName(first), DirectoryPath(vm, game));
            await Choose(vm, game, Path.GetDirectoryName(second)!);
            await Rescan(vm, game);
        }
        finally { repository.ReleaseSave.TrySetResult(); }
        await choose;
        Assert.Single(repository.Saves);
        Assert.Equal(first, ConfiguredPath(repository.State, game));
        Assert.Equal(first, SelectedChoice(vm, game)!.LocalPath);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task LegacyEldenTransientProfilesPreserveCommittedSourceWithoutWrites(bool rescan, bool throws, bool discovered)
    {
        string path = CreateSave("er", Path.Combine(Root, "legacy"));
        byte[] before = File.ReadAllBytes(path);
        var repository = new MemoryRepository(GameId.EldenRing) { State = new(1, GameId.EldenRing, OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true, eldenRingSave: new(path, 0)) };
        var configuration = repository.State.EldenRingSave;
        var source = EffectiveDeathTotalResult.SourceIdentityFor(repository.State);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var empty = new EmptyDiscovery();
        ILocalSaveDiscovery discovery = discovered ? new FixedDiscovery(new(path, "Automatic source")) : empty;
        var reader = new UnavailableProfileReader(throws) { ReadAvailableProfiles = rescan };
        var vm = new DesktopTrackerViewModel(coordinator, reader, empty, discovery, liesOfPSaveDiscovery: empty);
        await vm.InitializeAsync();
        if (rescan)
        {
            Assert.Equal(0, vm.SelectedEldenRingProfileSlot!.Index);
            reader.ReadAvailableProfiles = false;
            await vm.RescanEldenRingSavesAsync();
        }
        Assert.Equal(configuration, repository.State.EldenRingSave);
        Assert.Equal(source, EffectiveDeathTotalResult.SourceIdentityFor(repository.State));
        Assert.Null(repository.State.EldenRingSave.SelectedDirectory);
        Assert.Equal(Path.GetDirectoryName(path), vm.EldenRingDirectoryPath);
        Assert.Empty(repository.Saves);
        Assert.Empty(vm.EldenRingProfileSlots);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, -1, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, -1, false)]
    [InlineData(true, 0, true)]
    public async Task LegacyEldenValidProfilesRetainSelectionAndStaleCleanup(bool rescan, int slot, bool noCharacters)
    {
        string path = CreateSave("er", Path.Combine(Root, "legacy"));
        if (noCharacters)
        {
            byte[] contents = File.ReadAllBytes(path);
            contents[0x1000 + 0x1964] = 0;
            File.WriteAllBytes(path, contents);
        }
        byte[] before = File.ReadAllBytes(path);
        var repository = new MemoryRepository(GameId.EldenRing) { State = new(1, GameId.EldenRing, OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true, eldenRingSave: new(path, slot)) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var empty = new EmptyDiscovery();
        var reader = new UnavailableProfileReader(false) { ReadAvailableProfiles = !rescan };
        var vm = new DesktopTrackerViewModel(coordinator, reader, empty, empty, liesOfPSaveDiscovery: empty);
        await vm.InitializeAsync();
        if (rescan)
        {
            Assert.Equal(slot, repository.State.EldenRingSave.SlotIndex);
            Assert.Empty(repository.Saves);
            reader.ReadAvailableProfiles = true;
            await vm.RescanEldenRingSavesAsync();
        }
        bool stale = slot != EldenRingSaveConfiguration.NoSlotIndex && (slot != 0 || noCharacters);
        Assert.Equal(stale ? EldenRingSaveConfiguration.NoSlotIndex : slot, repository.State.EldenRingSave.SlotIndex);
        Assert.Equal(stale ? 1 : 0, repository.Saves.Count);
        Assert.Equal(path, repository.State.EldenRingSave.LocalPath);
        Assert.Null(repository.State.EldenRingSave.SelectedDirectory);
        if (noCharacters) Assert.Empty(vm.EldenRingProfileSlots);
        else
        {
            var choice = Assert.Single(vm.EldenRingProfileSlots);
            Assert.Equal(0, choice.Index);
            if (slot == 0) Assert.Equal(choice, vm.SelectedEldenRingProfileSlot);
            else
            {
                Assert.Null(vm.SelectedEldenRingProfileSlot);
                await vm.SetEldenRingProfileSlotAsync(choice);
                Assert.Equal(0, repository.State.EldenRingSave.SlotIndex);
                Assert.Equal(choice, vm.SelectedEldenRingProfileSlot);
                Assert.Equal(stale ? 2 : 1, repository.Saves.Count);
            }
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private sealed class UnavailableProfileReader(bool throws) : IEldenRingSaveProfileReader
    {
        internal bool ReadAvailableProfiles { get; set; }
        public ValueTask<IReadOnlyList<EldenRingCharacterSlotMetadata>> ReadAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) =>
            ReadAvailableProfiles ? new EldenRingSaveProfileReader().ReadAsync(configuration, cancellationToken)
                : throws ? throw new IOException("Synthetic profile failure") : ValueTask.FromResult(EldenRingCharacterSlotMetadata.UnavailableSlots);
    }

    private sealed class DelayedProfileReader(string delayedPath) : IEldenRingSaveProfileReader
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IReadOnlyList<EldenRingCharacterSlotMetadata>> ReadAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken)
        {
            if (configuration.LocalPath == delayedPath) { Entered.TrySetResult(); await Release.Task; }
            return await new EldenRingSaveProfileReader().ReadAsync(configuration, cancellationToken);
        }
    }

    internal static string? Status(DesktopTrackerViewModel vm, string game) => game == "er" ? vm.EldenRingSaveDiscoveryStatus : game == "wk" ? vm.BlackMythWukongSaveDiscoveryStatus : vm.LiesOfPSaveDiscoveryStatus;
    internal static IReadOnlyList<DiscoveredLocalSave> Choices(DesktopTrackerViewModel vm, string game) => game == "er" ? vm.EldenRingSaveChoices : game == "wk" ? vm.BlackMythWukongSaveChoices : vm.LiesOfPSaveChoices;
    internal static DiscoveredLocalSave? SelectedChoice(DesktopTrackerViewModel vm, string game) => game == "er" ? vm.SelectedEldenRingSaveChoice : game == "wk" ? vm.SelectedBlackMythWukongSaveChoice : vm.SelectedLiesOfPSaveChoice;
    internal static bool SelectorVisible(DesktopTrackerViewModel vm, string game) => game == "er" ? vm.IsEldenRingSaveSelectorVisible : game == "wk" ? vm.IsWukongSaveSelectorVisible : vm.IsLiesOfPSaveSelectorVisible;
    internal static Task Select(DesktopTrackerViewModel vm, string game, DiscoveredLocalSave choice) => game == "er" ? vm.SelectEldenRingSaveChoiceAsync(choice) : game == "wk" ? vm.SelectBlackMythWukongSaveChoiceAsync(choice) : vm.SelectLiesOfPSaveChoiceAsync(choice);
    internal static Task Rescan(DesktopTrackerViewModel vm, string game) => game == "er" ? vm.RescanEldenRingSavesAsync() : game == "wk" ? vm.RescanBlackMythWukongSavesAsync() : vm.RescanLiesOfPSavesAsync();
    internal static void Cancel(DesktopTrackerViewModel vm, string game)
    {
        if (game == "er") vm.CancelEldenRingChange();
        else if (game == "wk") vm.CancelBlackMythWukongChange();
        else vm.CancelLiesOfPChange();
    }

    internal static DesktopTrackerViewModel CreateViewModel(SerializedTrackerCoordinator coordinator) =>
        new(coordinator, blackMythWukongSaveDiscovery: new EmptyDiscovery(), eldenRingSaveDiscovery: new EmptyDiscovery(), liesOfPSaveDiscovery: new EmptyDiscovery());

    internal static async Task Choose(DesktopTrackerViewModel vm, string game, string directory)
    {
        var method = typeof(DesktopTrackerViewModel).GetMethod("Set" + Prefix(game) + "SaveDirectoryAsync");
        Assert.NotNull(method);
        await (Task)method.Invoke(vm, [directory, CancellationToken.None])!;
    }

    internal static string? DirectoryPath(DesktopTrackerViewModel vm, string game) =>
        (string?)typeof(DesktopTrackerViewModel).GetProperty(Prefix(game) + "DirectoryPath")!.GetValue(vm);
    internal static string Prefix(string game) => game == "er" ? "EldenRing" : game == "wk" ? "BlackMythWukong" : "LiesOfP";
    internal static GameId Game(string game) => game == "er" ? GameId.EldenRing : game == "wk" ? GameId.BlackMythWukong : GameId.LiesOfP;
    internal static string? ConfiguredPath(PersistentTrackerState state, string game) => game == "er" ? state.EldenRingSave.LocalPath : game == "wk" ? state.BlackMythWukongSave.LocalPath : state.LiesOfPSave.LocalPath;
    internal static string? ConfiguredDirectory(PersistentTrackerState state, string game) => game == "er" ? state.EldenRingSave.SelectedDirectory : game == "wk" ? state.BlackMythWukongSave.SelectedDirectory : state.LiesOfPSave.SelectedDirectory;

    internal static string CreateSave(string game, string directory, int slot = 1)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, game == "er" ? "ER0000.sl2" : game == "wk" ? $"ArchiveSaveFile.{slot}.sav" : $"SaveData-{slot}_Character_1.sav");
        byte[] bytes;
        if (game == "er")
        {
            bytes = new byte[0x6000];
            "BND4"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x0C), 11);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x40 + 10 * 0x20 + 8), 0x5000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x40 + 10 * 0x20 + 0x10), 0x1000);
            bytes[0x1000 + 0x1964] = 1;
            Encoding.Unicode.GetBytes("Synthetic").CopyTo(bytes, 0x1000 + 0x1964 + 10);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1000 + 0x1964 + 10 + 0x22), 12);
        }
        else if (game == "lp")
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            writer.Write("GVAS"u8); writer.Write(new byte[60]);
            foreach (string name in new[] { "TotalReceiveDamage", "YouDieCount" })
            {
                writer.Write(name.Length + 1); writer.Write(Encoding.ASCII.GetBytes(name)); writer.Write((byte)0);
                writer.Write(12); writer.Write("IntProperty\0"u8); writer.Write(4); writer.Write(0); writer.Write((byte)0); writer.Write(7);
            }
            bytes = stream.ToArray();
        }
        else
        {
            byte[] encrypted = Field(6, Field(1, Field(5, [8, 7])));
            byte[] key = [0x7B, 0x5C, 0xDA, 0x91, 0x3E, 0xFC, 0xDA, 0x37];
            for (int i = 0; i < encrypted.Length; i++) encrypted[i] ^= key[i % key.Length];
#pragma warning disable CA5351
            byte[] checksum = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(MD5.HashData([.. encrypted, .. "lhx2tkh6lj1wj8jmrgs3k1xb2brusehx"u8])));
#pragma warning restore CA5351
            byte[] metadata = [.. Field(1, checksum), 56, 14, 64, 1, 80, 151, 186, 1, 88, 151, 186, 1];
            bytes = [.. Field(1, metadata), .. Field(2, encrypted)];
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Field(int field, byte[] value) => [(byte)(field << 3 | 2), (byte)value.Length, .. value];
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    internal sealed class EmptyDiscovery : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([]);
    }
    internal sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    internal sealed class MemoryRepository(GameId game) : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; set; } = new(1, game, OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true);
        public List<PersistentTrackerState> Saves { get; } = [];
        public bool FailSave { get; set; }
        public TaskCompletionSource? SaveEntered { get; set; }
        public TaskCompletionSource? ReleaseSave { get; set; }
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public async Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Synthetic failure");
            SaveEntered?.TrySetResult();
            if (ReleaseSave is not null) await ReleaseSave.Task;
            State = state; Saves.Add(state);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
