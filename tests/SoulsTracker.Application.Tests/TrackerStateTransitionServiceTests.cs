using System.Reflection;
using System.Runtime.CompilerServices;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Application.Tests;

public sealed class TrackerStateTransitionServiceTests
{
    private static readonly string[] TransitionSourceFiles = ["TrackerCommands.cs", "TrackerTransitionResult.cs", "TrackerStateTransitionService.cs"];
    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    public void SelectGameAcceptsEverySelectableCanonicalGame(string gameIdValue)
    {
        GameId gameId = GameId.Parse(gameIdValue);

        TrackerTransitionResult result = TrackerStateTransitionService.Apply(PersistentTrackerState.Default, new SelectGameCommand(gameId));

        Assert.True(result.StateChanged);
        Assert.Equal(TrackerCommandType.SelectGame, result.CommandType);
        Assert.Same(gameId, result.State.SelectedGameId);
    }

    [Fact]
    public void SelectGamePreservesAllOtherPersistentFieldsAndReselectingIsANoOp()
    {
        BossId ds1Boss = GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id;
        BossProgress progress = BossProgress.Empty.MarkDefeated(GameId.Ds1, ds1Boss);
        OverlayConfiguration configuration = new(
            OverlayConfiguration.CurrentSchemaVersion,
            OverlayEndpointConfiguration.Unassigned,
            new TotalDeathsOverlayOptions(isEnabled: false, showGameName: false),
            new BossListOverlayOptions(isEnabled: false, BossListVisibilityMode.Remaining));
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.Ds1,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 4),
            progress,
            configuration);

        TrackerTransitionResult changed = TrackerStateTransitionService.Apply(state, new SelectGameCommand(GameId.Bloodborne));
        TrackerTransitionResult unchanged = TrackerStateTransitionService.Apply(changed.State, new SelectGameCommand(GameId.Bloodborne));

        Assert.True(changed.StateChanged);
        Assert.Equal(4, changed.State.ManualBloodborneDeathCounter.Value);
        Assert.Same(progress, changed.State.BossProgress);
        Assert.Same(configuration, changed.State.OverlayConfiguration);
        Assert.False(unchanged.StateChanged);
        Assert.Same(changed.State, unchanged.State);
    }

    [Fact]
    public void SelectGameRejectsUnknownDisabledAndNullGameIdsWithoutChangingState()
    {
        PersistentTrackerState state = PersistentTrackerState.Default;
        GameId unknown = CreateUnknownGameId();

        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new SelectGameCommand(GameId.EldenRing)));
        Assert.Equal(GameId.DemonsSouls, TrackerStateTransitionService.Apply(state, new SelectGameCommand(GameId.DemonsSouls)).State.SelectedGameId);
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new SelectGameCommand(unknown)));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new SelectGameCommand(null!)));
        Assert.Same(state, state);
    }

    [Fact]
    public void ManualCounterTransitionsWorkOnlyForBloodborneAndPreserveOtherState()
    {
        PersistentTrackerState state = Select(PersistentTrackerState.Default, GameId.Bloodborne);
        TrackerTransitionResult incremented = TrackerStateTransitionService.Apply(state, new IncrementManualBloodborneDeathsCommand());
        TrackerTransitionResult decremented = TrackerStateTransitionService.Apply(incremented.State, new DecrementManualBloodborneDeathsCommand());
        TrackerTransitionResult atZero = TrackerStateTransitionService.Apply(decremented.State, new DecrementManualBloodborneDeathsCommand());

        Assert.Equal(1, incremented.State.ManualBloodborneDeathCounter.Value);
        Assert.True(incremented.StateChanged);
        Assert.Equal(0, decremented.State.ManualBloodborneDeathCounter.Value);
        Assert.True(decremented.StateChanged);
        Assert.False(atZero.StateChanged);
        Assert.Same(decremented.State, atZero.State);
        Assert.Same(state.BossProgress, incremented.State.BossProgress);
        Assert.Same(state.OverlayConfiguration, incremented.State.OverlayConfiguration);
    }

    [Fact]
    public void ManualCountersAreIndependentForBloodborneAndDemonsSouls()
    {
        PersistentTrackerState bloodborne = Select(PersistentTrackerState.Default, GameId.Bloodborne);
        PersistentTrackerState afterBloodborneDeath = TrackerStateTransitionService.Apply(bloodborne, new IncrementManualBloodborneDeathsCommand()).State;
        PersistentTrackerState demonsSouls = TrackerStateTransitionService.Apply(afterBloodborneDeath, new SelectGameCommand(GameId.DemonsSouls)).State;
        PersistentTrackerState afterDemonsSoulsDeaths = TrackerStateTransitionService.Apply(demonsSouls, new IncrementManualBloodborneDeathsCommand()).State;
        afterDemonsSoulsDeaths = TrackerStateTransitionService.Apply(afterDemonsSoulsDeaths, new IncrementManualBloodborneDeathsCommand()).State;
        PersistentTrackerState afterDemonsSoulsReset = TrackerStateTransitionService.Apply(
            TrackerStateTransitionService.Apply(afterDemonsSoulsDeaths, new DecrementManualBloodborneDeathsCommand()).State,
            new DecrementManualBloodborneDeathsCommand()).State;
        PersistentTrackerState restoredBloodborne = TrackerStateTransitionService.Apply(afterDemonsSoulsReset, new SelectGameCommand(GameId.Bloodborne)).State;

        Assert.Equal(1, restoredBloodborne.ManualBloodborneDeathCounter.Value);
        Assert.Equal(0, restoredBloodborne.ManualDemonsSoulsDeathCounter.Value);
        Assert.Equal(1, restoredBloodborne.GetManualDeathCounter(GameId.Bloodborne).Value);
        Assert.Equal(0, restoredBloodborne.GetManualDeathCounter(GameId.DemonsSouls).Value);
    }

    [Fact]
    public void ManualCounterTransitionsRejectNoSelectionAndAutomaticGamesWithoutChangingState()
    {
        PersistentTrackerState noSelection = PersistentTrackerState.Default;
        PersistentTrackerState automatic = Select(noSelection, GameId.Ds1);

        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(noSelection, new IncrementManualBloodborneDeathsCommand()));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(noSelection, new DecrementManualBloodborneDeathsCommand()));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(automatic, new IncrementManualBloodborneDeathsCommand()));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(automatic, new DecrementManualBloodborneDeathsCommand()));
        Assert.Equal(0, noSelection.ManualBloodborneDeathCounter.Value);
        Assert.Equal(0, automatic.ManualBloodborneDeathCounter.Value);
    }

    [Fact]
    public void BossTransitionsSetUnsetAndRemainIsolatedByGame()
    {
        BossId ds1Boss = GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id;
        BossId ds2Boss = GameCatalog.GetRequired(GameId.Ds2).BossCatalog[0].Id;
        PersistentTrackerState state = PersistentTrackerState.Default;

        TrackerTransitionResult ds1Set = TrackerStateTransitionService.Apply(state, new SetBossDefeatedCommand(GameId.Ds1, ds1Boss, true));
        TrackerTransitionResult ds2Set = TrackerStateTransitionService.Apply(ds1Set.State, new SetBossDefeatedCommand(GameId.Ds2, ds2Boss, true));
        TrackerTransitionResult ds1Unset = TrackerStateTransitionService.Apply(ds2Set.State, new SetBossDefeatedCommand(GameId.Ds1, ds1Boss, false));

        Assert.True(ds1Set.State.BossProgress.IsDefeated(GameId.Ds1, ds1Boss));
        Assert.True(ds2Set.State.BossProgress.IsDefeated(GameId.Ds2, ds2Boss));
        Assert.False(ds1Unset.State.BossProgress.IsDefeated(GameId.Ds1, ds1Boss));
        Assert.True(ds1Unset.State.BossProgress.IsDefeated(GameId.Ds2, ds2Boss));
    }

    [Fact]
    public void MatchingBossStateIsANoOpAndBossUpdatesNeedNotMatchSelectedGame()
    {
        BossId ds1Boss = GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id;
        PersistentTrackerState selectedBloodborne = Select(PersistentTrackerState.Default, GameId.Bloodborne);
        TrackerTransitionResult set = TrackerStateTransitionService.Apply(selectedBloodborne, new SetBossDefeatedCommand(GameId.Ds1, ds1Boss, true));
        TrackerTransitionResult repeated = TrackerStateTransitionService.Apply(set.State, new SetBossDefeatedCommand(GameId.Ds1, ds1Boss, true));

        Assert.True(set.StateChanged);
        Assert.False(repeated.StateChanged);
        Assert.Same(set.State, repeated.State);
    }

    [Fact]
    public void BossTransitionsRejectDisabledUnknownCrossGameAndNullValuesWithoutMutation()
    {
        BossId ds1Boss = GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id;
        BossId ds2Boss = GameCatalog.GetRequired(GameId.Ds2).BossCatalog[0].Id;
        BossId unknownBoss = BossId.Parse("unknown_boss");
        PersistentTrackerState state = PersistentTrackerState.Default;

        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new SetBossDefeatedCommand(GameId.EldenRing, ds1Boss, true)));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new SetBossDefeatedCommand(GameId.Ds1, ds2Boss, true)));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new SetBossDefeatedCommand(GameId.Ds1, unknownBoss, true)));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(state, new SetBossDefeatedCommand(GameId.Ds1, null!, true)));
        Assert.False(state.BossProgress.IsDefeated(GameId.Ds1, ds1Boss));
    }

    [Fact]
    public void PresentationTransitionUpdatesOnlyApprovedOptionsAndPreservesEndpointAndAllOtherState()
    {
        const string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        OverlayEndpointConfiguration endpoint = new(45678, OverlayAccessToken.Parse(token));
        BossId boss = GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id;
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.Bloodborne,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 9),
            BossProgress.Empty.MarkDefeated(GameId.Ds1, boss),
            new OverlayConfiguration(
                OverlayConfiguration.CurrentSchemaVersion,
                endpoint,
                TotalDeathsOverlayOptions.Default,
                BossListOverlayOptions.Default));

        TrackerTransitionResult changed = TrackerStateTransitionService.Apply(
            state,
            new UpdateOverlayPresentationCommand(false, true, false, BossListVisibilityMode.Defeated));
        TrackerTransitionResult unchanged = TrackerStateTransitionService.Apply(
            changed.State,
            new UpdateOverlayPresentationCommand(false, true, false, BossListVisibilityMode.Defeated));

        Assert.True(changed.StateChanged);
        Assert.Equal(TrackerCommandType.UpdateOverlayPresentation, changed.CommandType);
        Assert.False(changed.State.OverlayConfiguration.TotalDeaths.IsEnabled);
        Assert.False(changed.State.OverlayConfiguration.TotalDeaths.ShowGameName);
        Assert.False(changed.State.OverlayConfiguration.BossList.IsEnabled);
        Assert.Equal(BossListVisibilityMode.Defeated, changed.State.OverlayConfiguration.BossList.VisibilityMode);
        Assert.Same(endpoint, changed.State.OverlayConfiguration.Endpoint);
        Assert.Equal(GameId.Bloodborne, changed.State.SelectedGameId);
        Assert.Equal(9, changed.State.ManualBloodborneDeathCounter.Value);
        Assert.Same(state.BossProgress, changed.State.BossProgress);
        Assert.False(unchanged.StateChanged);
        Assert.Same(changed.State, unchanged.State);
    }

    [Fact]
    public void PresentationTransitionPreservesIndependentAppearanceConfiguration()
    {
        OverlayAppearance total = new("Deaths", "Arial", 30, "#010203", "#040506", "#070809", 20, 6, 3, OverlayTextAlignment.Center);
        OverlayAppearance boss = new("Bosses", "Verdana", 33, "#112233", "#445566", "#778899", 44, 12, 5, OverlayTextAlignment.Right);
        PersistentTrackerState configured = new(1, GameId.Ds1, ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), BossProgress.Empty,
            new OverlayConfiguration(1, OverlayEndpointConfiguration.Unassigned,
                new TotalDeathsOverlayOptions(true, true, true, total, OverlayTitleIconMode.PrefixSkull),
                new BossListOverlayOptions(true, BossListVisibilityMode.All, boss, "#AABBCC", DefeatedBossTreatment.Dimmed, false, "#DDEEFF", 7, true)));

        PersistentTrackerState updated = TrackerStateTransitionService.Apply(configured,
            new UpdateOverlayPresentationCommand(false, false, false, BossListVisibilityMode.Remaining)).State;

        Assert.Equal("Deaths", updated.OverlayConfiguration.TotalDeaths.Appearance.Title);
        Assert.True(updated.OverlayConfiguration.TotalDeaths.CompactTitle);
        Assert.Equal(OverlayTitleIconMode.PrefixSkull, updated.OverlayConfiguration.TotalDeaths.TitleIconMode);
        Assert.Equal("Bosses", updated.OverlayConfiguration.BossList.Appearance.Title);
        Assert.Equal(OverlayTextAlignment.Right, updated.OverlayConfiguration.BossList.Appearance.Alignment);
        Assert.True(updated.OverlayConfiguration.BossList.ShowDefeatedSkull);
    }

    [Fact]
    public void PresentationTransitionRejectsUndefinedModeWithoutChangingState()
    {
        PersistentTrackerState state = PersistentTrackerState.Default;

        Assert.Throws<ArgumentOutOfRangeException>(() => TrackerStateTransitionService.Apply(
            state,
            new UpdateOverlayPresentationCommand(true, true, true, (BossListVisibilityMode)99)));
        Assert.Same(PersistentTrackerState.Default, state);
    }

    [Fact]
    public void AppearanceTransitionProjectsOnlyTypedValidatedValues()
    {
        OverlayAppearance appearance = new("Deaths", "Verdana", 48, "#FFFFFF", "#FFD54F", "#000000", 100, 20, 8, OverlayTextAlignment.Center);
        TrackerTransitionResult result = TrackerStateTransitionService.Apply(PersistentTrackerState.Default,
            new UpdateOverlayAppearanceCommand(false, appearance, true, false, BossListVisibilityMode.Remaining, "#777777", DefeatedBossTreatment.Dimmed, false, "#00FF00", 9));
        BossListOverlayOptions boss = result.State.OverlayConfiguration.BossList;
        Assert.True(result.StateChanged);
        Assert.Equal(TrackerCommandType.UpdateOverlayAppearance, result.CommandType);
        Assert.Same(appearance, boss.Appearance);
        Assert.Equal(BossListVisibilityMode.Remaining, boss.VisibilityMode);
        Assert.Equal(DefeatedBossTreatment.Dimmed, boss.DefeatedTreatment);
        Assert.False(boss.ShowCheckmark);
        Assert.Equal(9, boss.MaximumVisibleCount);
    }

    [Fact]
    public void ResetRestoresTheFullOverlayAppearanceToApprovedDefaults()
    {
        OverlayAppearance custom = new("Custom", "Verdana", 48, "#FFFFFF", "#FFD54F", "#000000", 100, 20, 8, OverlayTextAlignment.Center);
        PersistentTrackerState customized = TrackerStateTransitionService.Apply(PersistentTrackerState.Default,
            new UpdateOverlayAppearanceCommand(true, custom, false, true, BossListVisibilityMode.All, "#8C8C96", DefeatedBossTreatment.Strikethrough, true, "#A78BFA", 25)).State;
        PersistentTrackerState reset = TrackerStateTransitionService.Apply(customized, new ResetOverlayAppearanceCommand(true)).State;
        Assert.Equal("Total Deaths", reset.OverlayConfiguration.TotalDeaths.Appearance.Title);
        Assert.Equal(OverlayAppearance.Default.FontFamily, reset.OverlayConfiguration.TotalDeaths.Appearance.FontFamily);
        Assert.Equal(OverlayAppearance.Default.FontSize, reset.OverlayConfiguration.TotalDeaths.Appearance.FontSize);
        Assert.Equal(OverlayAppearance.Default.Alignment, reset.OverlayConfiguration.TotalDeaths.Appearance.Alignment);
    }

    [Fact]
    public void CommandBoundaryRejectsNullStateNullCommandAndUnknownCommand()
    {
        Assert.Throws<ArgumentNullException>(() => TrackerStateTransitionService.Apply(null!, new SelectGameCommand(GameId.Ds1)));
        Assert.Throws<ArgumentNullException>(() => TrackerStateTransitionService.Apply(PersistentTrackerState.Default, null!));
        Assert.Throws<ArgumentException>(() => TrackerStateTransitionService.Apply(PersistentTrackerState.Default, new UnknownCommand()));
    }

    [Fact]
    public void CommandsAndResultAreImmutableAndApplicationDoesNotExposeExcludedCapabilities()
    {
        Type[] contracts =
        [
            typeof(SelectGameCommand),
            typeof(IncrementManualBloodborneDeathsCommand),
            typeof(DecrementManualBloodborneDeathsCommand),
            typeof(SetBossDefeatedCommand),
            typeof(UpdateOverlayPresentationCommand),
            typeof(ResetOverlayAppearanceCommand),
            typeof(UpdateOverlayAppearanceCommand),
            typeof(TrackerTransitionResult),
        ];

        Assert.All(
            contracts,
            type => Assert.DoesNotContain(
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public),
                property => property.SetMethod?.IsPublic == true &&
                    !property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit))));

        string sourceDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SoulsTracker.Application"));
        string source = string.Concat(
            TransitionSourceFiles
                .Select(file => File.ReadAllText(Path.Combine(sourceDirectory, file))));
        string[] prohibitedTerms = ["SQLite", "File.", "Http", "WebSocket", "System.Windows", "Win32", "Process", "Memory", "DateTime", "Task", "Queue", "Token"];
        Assert.DoesNotContain(prohibitedTerms, term => source.Contains(term, StringComparison.Ordinal));
    }

    private static PersistentTrackerState Select(PersistentTrackerState state, GameId gameId) =>
        TrackerStateTransitionService.Apply(state, new SelectGameCommand(gameId)).State;

    private static GameId CreateUnknownGameId()
    {
        ConstructorInfo constructor = typeof(GameId).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string)],
            modifiers: null)!;
        return (GameId)constructor.Invoke(["unknown"]);
    }

    private sealed record UnknownCommand : ITrackerCommand;
}
