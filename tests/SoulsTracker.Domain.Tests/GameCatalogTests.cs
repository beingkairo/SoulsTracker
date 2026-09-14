using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class GameCatalogTests
{
    [Fact]
    public void CatalogContainsSelectableGamesAndSupportsLookup()
    {
        Assert.NotEmpty(GameCatalog.All);
        GameDefinition definition = GameCatalog.GetRequired(GameId.EldenRing);
        Assert.Equal(GameId.EldenRing, definition.Id);
        Assert.True(GameCatalog.TryGet(GameId.EldenRing.Value, out GameDefinition? found));
        Assert.Equal(definition, found);
    }
}
