using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class GameCatalogTests
{
    [Fact]
    public void DefinitionsMatchTheApprovedCapabilityMatrix()
    {
        ExpectedGameDefinition[] expected =
        [
            new(
                GameId.DemonsSouls,
                "Demon Souls",
                GameUiAvailability.Selectable,
                GameTrackingMode.ManualOnly,
                ReaderBindingState.IntentionallyUnavailable,
                16),
            new(
                GameId.Ds1,
                "Dark Souls: Remastered",
                GameUiAvailability.Selectable,
                GameTrackingMode.GameLifetimeReadOnly,
                ReaderBindingState.PendingVerification,
                26),
            new(
                GameId.Ds2,
                "Dark Souls II: Scholar of the First Sin",
                GameUiAvailability.Selectable,
                GameTrackingMode.GameLifetimeReadOnly,
                ReaderBindingState.PendingVerification,
                41),
            new(
                GameId.Ds3,
                "Dark Souls III",
                GameUiAvailability.Selectable,
                GameTrackingMode.GameLifetimeReadOnly,
                ReaderBindingState.PendingVerification,
                25),
            new(
                GameId.Bloodborne,
                "Bloodborne",
                GameUiAvailability.Selectable,
                GameTrackingMode.ManualOnly,
                ReaderBindingState.IntentionallyUnavailable,
                22),
            new(
                GameId.Sekiro,
                "Sekiro: Shadows Die Twice",
                GameUiAvailability.Selectable,
                GameTrackingMode.GameLifetimeReadOnly,
                ReaderBindingState.PendingVerification,
                16),
            new(
                GameId.EldenRing,
                "Elden Ring",
                GameUiAvailability.DisabledSoon,
                GameTrackingMode.Unavailable,
                ReaderBindingState.IntentionallyUnavailable,
                0),
        ];

        ExpectedGameDefinition[] actual = GameCatalog.All
            .Select(static definition => new ExpectedGameDefinition(
                definition.Id,
                definition.DisplayName,
                definition.UiAvailability,
                definition.TrackingMode,
                definition.ReaderBindingState,
                definition.BossCatalog.Count))
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(7, actual.Select(static definition => definition.Id).Distinct().Count());
        Assert.Equal(GameId.All, actual.Select(static definition => definition.Id));
    }

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    [InlineData("elden_ring")]
    [InlineData("demons_souls")]
    public void CanonicalGameIdsParseExactly(string value)
    {
        Assert.True(GameId.TryParse(value, out GameId? gameId));
        Assert.NotNull(gameId);
        Assert.Equal(value, gameId.Value);
    }

    [Fact]
    public void UnknownGameLookupNeverDefaultsToDs2OrAnotherGame()
    {
        Assert.False(GameId.TryParse("not_a_game", out GameId? parsedGameId));
        Assert.Null(parsedGameId);
        Assert.False(GameCatalog.TryGet("not_a_game", out GameDefinition? definition));
        Assert.Null(definition);
        Assert.Throws<ArgumentException>(() => GameCatalog.GetRequired("not_a_game"));
    }

    [Theory]
    [InlineData("elden_ring")]
    public void SoonDefinitionsAreVisibleButNotSelectableAndHaveNoBossCatalogFallback(string gameId)
    {
        GameDefinition definition = GameCatalog.GetRequired(gameId);

        Assert.Equal(GameUiAvailability.DisabledSoon, definition.UiAvailability);
        Assert.Equal(GameTrackingMode.Unavailable, definition.TrackingMode);
        Assert.Equal(ReaderBindingState.IntentionallyUnavailable, definition.ReaderBindingState);
        Assert.False(definition.IsSelectable);
        Assert.Empty(definition.BossCatalog);
        Assert.Throws<ArgumentException>(() =>
            GameCatalog.GetRequiredBoss(definition.Id, BossId.Parse("not_a_v1_boss")));
    }

    [Fact]
    public void DemonsSoulsIsASelectableManualProfileWithItsCanonicalBossCatalog()
    {
        GameDefinition definition = GameCatalog.GetRequired(GameId.DemonsSouls);
        Assert.True(definition.IsSelectable);
        Assert.Equal(GameTrackingMode.ManualOnly, definition.TrackingMode);
        Assert.Equal(16, definition.BossCatalog.Count);
        Assert.Equal("Phalanx", definition.BossCatalog[0].DisplayName);
        Assert.Equal("Maiden Astraea", definition.BossCatalog[^1].DisplayName);
    }

    private sealed record ExpectedGameDefinition(
        GameId Id,
        string DisplayName,
        GameUiAvailability UiAvailability,
        GameTrackingMode TrackingMode,
        ReaderBindingState ReaderBindingState,
        int BossCount);
}
