using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class BossProgressTests
{
    [Fact]
    public void MarkDefeatedTracksOnlyTheMatchingGameCatalog()
    {
        BossId ds1Boss = GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id;
        BossId ds2Boss = GameCatalog.GetRequired(GameId.Ds2).BossCatalog[0].Id;
        BossId ds2OtherBoss = GameCatalog.GetRequired(GameId.Ds2).BossCatalog[1].Id;

        BossProgress afterDs1 = BossProgress.Empty.MarkDefeated(GameId.Ds1, ds1Boss);
        BossProgress afterDs1AndDs2 = afterDs1.MarkDefeated(GameId.Ds2, ds2Boss);

        Assert.True(afterDs1.IsDefeated(GameId.Ds1, ds1Boss));
        Assert.False(BossProgress.Empty.IsDefeated(GameId.Ds1, ds1Boss));
        Assert.True(afterDs1AndDs2.IsDefeated(GameId.Ds1, ds1Boss));
        Assert.True(afterDs1AndDs2.IsDefeated(GameId.Ds2, ds2Boss));
        Assert.False(afterDs1AndDs2.IsDefeated(GameId.Ds2, ds2OtherBoss));
    }

    [Fact]
    public void MarkDefeatedRejectsBossesFromAnotherGameAndUnknownBosses()
    {
        BossId ds2Boss = GameCatalog.GetRequired(GameId.Ds2).BossCatalog[0].Id;
        BossId unknownBoss = BossId.Parse("unknown_boss");

        Assert.Throws<ArgumentException>(() =>
            BossProgress.Empty.MarkDefeated(GameId.Ds1, ds2Boss));
        Assert.Throws<ArgumentException>(() =>
            BossProgress.Empty.MarkDefeated(GameId.Ds1, unknownBoss));
    }

    [Fact]
    public void ClearDefeatedRemovesOnlyTheKnownMatchingGameBoss()
    {
        BossId ds1Boss = GameCatalog.GetRequired(GameId.Ds1).BossCatalog[0].Id;
        BossId ds2Boss = GameCatalog.GetRequired(GameId.Ds2).BossCatalog[0].Id;
        BossProgress progress = BossProgress.Empty
            .MarkDefeated(GameId.Ds1, ds1Boss)
            .MarkDefeated(GameId.Ds2, ds2Boss);

        BossProgress cleared = progress.ClearDefeated(GameId.Ds1, ds1Boss);

        Assert.False(cleared.IsDefeated(GameId.Ds1, ds1Boss));
        Assert.True(cleared.IsDefeated(GameId.Ds2, ds2Boss));
        Assert.Same(cleared, cleared.ClearDefeated(GameId.Ds1, ds1Boss));
        Assert.Throws<ArgumentException>(() => cleared.ClearDefeated(GameId.Ds1, ds2Boss));
    }
}
