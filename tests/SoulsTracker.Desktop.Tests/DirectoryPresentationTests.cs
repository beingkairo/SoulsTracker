using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

public sealed class DirectoryPresentationTests
{
    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task CommonAutomaticDirectoryPrecedesSelectionWithoutWriting(string game)
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string first = Path.Combine(fixture.Root, "first.sav");
        string second = Path.Combine(fixture.Root, "second.sav");
        var discovery = new Discovery(new(first, "Character 1"), new(second, "Character 2"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, eldenRingSaveDiscovery: discovery,
            blackMythWukongSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        await vm.InitializeAsync();
        Assert.Equal(fixture.Root, DirectoryPath(vm, game));
        Assert.True(Automatic(vm, game));
        Assert.Null(SelectedChoice(vm, game));
        Assert.Null(ConfiguredPath(repository.State, game));
        Assert.Empty(repository.Saves);
        Assert.Equal("Choose a character to continue.", vm.RuntimeReaderStatusText);
        await Rescan(vm, game);
        Assert.Equal(fixture.Root, DirectoryPath(vm, game));
        Assert.True(Automatic(vm, game));
        Assert.Empty(repository.Saves);
    }

    [Fact]
    public async Task LegacyLiesPairedDiscoveryRetainsAllAlreadyDiscoveredCandidates()
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string selected = CreateSave("lp", Path.Combine(fixture.Root, "a"));
        string pair = Path.Combine(Path.GetDirectoryName(selected)!, "SaveData-1_Character_2.sav");
        File.Copy(selected, pair);
        File.SetLastWriteTimeUtc(pair, DateTime.UtcNow.AddMinutes(1));
        string other = CreateSave("lp", Path.Combine(fixture.Root, "b"), 2);
        var repository = new MemoryRepository(GameId.LiesOfP) { State = new(1, GameId.LiesOfP, OverlayConfiguration.Default, liesOfPSave: new(selected)) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, liesOfPSaveDiscovery: new Discovery(new DiscoveredLocalSave(pair, "Character 1"), new DiscoveredLocalSave(other, "Character 2")));
        await vm.InitializeAsync();
        Assert.Equal(2, vm.LiesOfPSaveChoices.Count);
        Assert.Equal(selected, vm.SelectedLiesOfPSaveChoice?.LocalPath);
        Assert.Equal(selected, repository.State.LiesOfPSave.LocalPath);
        Assert.Empty(repository.Saves);
        Assert.False(vm.LiesOfPDirectoryWasAutomatic);
    }

    [Fact]
    public async Task LiesBoundedAutomaticResultsExposeDirectoryBeforeCharacterAndCancelRestoresIt()
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = Path.Combine(fixture.Root, "automatic");
        string first = CreateSave("lp", directory);
        string pair = Path.Combine(directory, "SaveData-1_Character_2.sav");
        File.Copy(first, pair);
        File.SetLastWriteTimeUtc(pair, DateTime.UtcNow.AddMinutes(1));
        CreateSave("lp", directory, 2);
        var before = Directory.GetFiles(directory).ToDictionary(x => x, File.ReadAllBytes);
        var discovery = new Discovery([.. LiesOfPSaveDiscovery.DiscoverInDirectory(directory)]);
        var repository = new MemoryRepository(GameId.LiesOfP);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, liesOfPSaveDiscovery: discovery);
        await vm.InitializeAsync();
        Assert.Equal(2, vm.LiesOfPSaveChoices.Count);
        Assert.Equal(directory, vm.LiesOfPDirectoryPath);
        Assert.True(vm.LiesOfPDirectoryWasAutomatic);
        Assert.Null(vm.SelectedLiesOfPSaveChoice);
        Assert.Empty(repository.Saves);
        string pending = Path.Combine(fixture.Root, "manual");
        CreateSave("lp", pending); CreateSave("lp", pending, 2);
        await vm.SetLiesOfPSaveDirectoryAsync(pending);
        Assert.False(vm.LiesOfPDirectoryWasAutomatic);
        Assert.Equal(pending, vm.LiesOfPDirectoryPath);
        vm.CancelLiesOfPChange();
        Assert.Equal(directory, vm.LiesOfPDirectoryPath);
        Assert.True(vm.LiesOfPDirectoryWasAutomatic);
        Assert.Null(vm.SelectedLiesOfPSaveChoice);
        Assert.Empty(repository.Saves);
        var choice = vm.LiesOfPSaveChoices.Single(x => LiesOfPSaveDiscovery.IsSameCharacter(x.LocalPath, first));
        await vm.SelectLiesOfPSaveChoiceAsync(choice);
        Assert.Equal(choice.LocalPath, repository.State.LiesOfPSave.LocalPath);
        Assert.Single(repository.Saves);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task AmbiguousAutomaticCandidatesRequireExplicitChoiceWithoutAutomaticClaim(string game)
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string first = CreateSave(game, Path.Combine(fixture.Root, "a"));
        string second = CreateSave(game, Path.Combine(fixture.Root, "b"), 2);
        var discovery = new Discovery(new DiscoveredLocalSave(first, "Character 1"), new DiscoveredLocalSave(second, "Character 2"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, eldenRingSaveDiscovery: discovery,
            blackMythWukongSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        await vm.InitializeAsync();
        Assert.Equal(2, Choices(vm, game).Count);
        Assert.False(Automatic(vm, game));
        Assert.Null(ConfiguredPath(repository.State, game));
        Assert.Empty(repository.Saves);
        Assert.DoesNotContain("not found automatically", Feedback(vm, game));
        await Select(vm, game, Choices(vm, game)[1]);
        Assert.Equal(second, ConfiguredPath(repository.State, game));
        Assert.False(Automatic(vm, game));
        Assert.Null(Feedback(vm, game));
        await Rescan(vm, game);
        Assert.False(Automatic(vm, game));
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task AutomaticHelpTracksActualSelectionAndCancelWithoutPersistingProvenance(string game)
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string path = CreateSave(game, Path.Combine(fixture.Root, "automatic"));
        var discovery = new Discovery(new DiscoveredLocalSave(path, "Character 1"));
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator, eldenRingSaveDiscovery: discovery,
            blackMythWukongSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        await vm.InitializeAsync();
        Assert.True(Automatic(vm, game));
        Assert.Null(Feedback(vm, game));
        Assert.Equal("Chosen Directory", Heading(vm, game));
        string multiple = Path.Combine(fixture.Root, "replacement");
        CreateSave(game, Path.Combine(multiple, "a")); CreateSave(game, Path.Combine(multiple, "b"), 2);
        await Choose(vm, game, multiple);
        Assert.False(Automatic(vm, game));
        Assert.Equal("Pending Directory", Heading(vm, game));
        Cancel(vm, game);
        Assert.True(Automatic(vm, game));
        await Rescan(vm, game);
        Assert.True(Automatic(vm, game));
        await Choose(vm, game, multiple);
        await Select(vm, game, Choices(vm, game)[0]);
        Assert.False(Automatic(vm, game));
        var restarted = CreateViewModel(coordinator);
        await restarted.InitializeAsync();
        Assert.False(Automatic(restarted, game));
    }

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task MissingDirectoryInstructionDoesNotReplaceAmbiguityOrErrors(string game)
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        Assert.Equal("Save Directory", Heading(vm, game));
        Assert.Equal("Save directory was not found automatically, please click Choose Directory and select the save folder", Feedback(vm, game));
        Assert.False(Automatic(vm, game));
        string directory = Path.Combine(fixture.Root, "multiple");
        CreateSave(game, Path.Combine(directory, "a")); CreateSave(game, Path.Combine(directory, "b"), 2);
        await Choose(vm, game, directory);
        Assert.DoesNotContain("not found automatically", Feedback(vm, game));
        await Select(vm, game, Choices(vm, game)[0]);
        Assert.Null(Feedback(vm, game));
        await Choose(vm, game, Path.Combine(fixture.Root, "missing"));
        Assert.Contains("attempted directory", Feedback(vm, game));
        Assert.False(Automatic(vm, game));
    }

    internal static bool Automatic(DesktopTrackerViewModel vm, string game) => (bool)Property(vm, game, "DirectoryWasAutomatic")!;
    internal static string? Feedback(DesktopTrackerViewModel vm, string game) => (string?)Property(vm, game, "DirectoryStatus");
    internal static string? Heading(DesktopTrackerViewModel vm, string game) => (string?)Property(vm, game, "DirectoryHeading");
    private static object? Property(DesktopTrackerViewModel vm, string game, string suffix)
    {
        var property = typeof(DesktopTrackerViewModel).GetProperty(Prefix(game) + suffix);
        Assert.NotNull(property);
        return property.GetValue(vm);
    }
    private sealed class Discovery(params DiscoveredLocalSave[] choices) : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>(choices);
    }
}
