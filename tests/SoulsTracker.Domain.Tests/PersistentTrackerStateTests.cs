using System.Reflection;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class PersistentTrackerStateTests
{
    [Fact]
    public void InvalidPersistedHotkeyConfigurationFallsBackToTheSafeDefaults()
    {
        var state = new PersistentTrackerState(
            PersistentTrackerState.CurrentSchemaVersion,
            null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            new ManualBloodborneHotkeyConfiguration(0, 0x26, 0, 0x26));

        Assert.Equal(ManualBloodborneHotkeyConfiguration.Default, state.ManualBloodborneHotkeys);
    }

    [Fact]
    public void DefaultStateIsVersionedEmptyAndUsesStableOverlayDefaults()
    {
        PersistentTrackerState state = PersistentTrackerState.Default;

        Assert.Equal(1, state.SchemaVersion);
        Assert.Null(state.SelectedGameId);
        Assert.Equal(0L, state.ManualBloodborneDeathCounter.Value);
        Assert.Equal(0L, state.ManualDemonsSoulsDeathCounter.Value);

        foreach (GameDefinition definition in GameCatalog.All)
        {
            foreach (BossDefinition boss in definition.BossCatalog)
            {
                Assert.False(state.BossProgress.IsDefeated(definition.Id, boss.Id));
            }
        }

        Assert.Equal(1, state.OverlayConfiguration.SchemaVersion);
        Assert.False(state.OverlayConfiguration.Endpoint.IsAssigned);
        Assert.Null(state.OverlayConfiguration.Endpoint.Port);
        Assert.Null(state.OverlayConfiguration.Endpoint.AccessToken);
        Assert.True(state.OverlayConfiguration.TotalDeaths.IsEnabled);
        Assert.False(state.OverlayConfiguration.TotalDeaths.ShowGameName);
        Assert.True(state.OverlayConfiguration.TotalDeaths.CompactTitle);
        Assert.True(state.OverlayConfiguration.BossList.IsEnabled);
        Assert.Equal(BossListVisibilityMode.All, state.OverlayConfiguration.BossList.VisibilityMode);
    }

    [Fact]
    public void SelectedGameValidationAllowsOnlyKnownSelectableGamesWithoutAFallback()
    {
        foreach (GameDefinition definition in GameCatalog.All.Where(static definition => definition.IsSelectable))
        {
            PersistentTrackerState state = CreateState(definition.Id);

            Assert.Equal(definition.Id, state.SelectedGameId);
        }

        Assert.Throws<ArgumentException>(() => CreateState(GameId.EldenRing));
        Assert.Throws<ArgumentException>(() => CreateState(CreateUnknownGameId()));
        Assert.Null(PersistentTrackerState.Default.SelectedGameId);
    }

    [Fact]
    public void PersistentStateRejectsUnsupportedVersionsAndDoesNotContainRuntimeObservations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PersistentTrackerState(
            schemaVersion: 2,
            selectedGameId: null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default));

        PropertyInfo[] properties = typeof(PersistentTrackerState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        MethodInfo[] mutationMethods = typeof(PersistentTrackerState)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName && method.Name != nameof(PersistentTrackerState.GetManualDeathCounter))
            .ToArray();

        Assert.DoesNotContain(properties, static property => property.PropertyType == typeof(RuntimeGameObservation));
        Assert.Empty(mutationMethods);
    }

    private static PersistentTrackerState CreateState(GameId? selectedGameId) => new(
        PersistentTrackerState.CurrentSchemaVersion,
        selectedGameId,
        ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
        BossProgress.Empty,
        OverlayConfiguration.Default);

    private static GameId CreateUnknownGameId()
    {
        ConstructorInfo constructor = typeof(GameId).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException("GameId's private constructor was not found.");

        return (GameId)constructor.Invoke(["unknown_game"]);
    }
}
