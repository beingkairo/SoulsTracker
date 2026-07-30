using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class EldenRingMissedDeathAdjustmentsTests
{
    [Fact]
    public void AdjustmentIsCaseInsensitivePerSaveAndIsolatedPerCharacter()
    {
        var firstCharacter = new EldenRingSaveConfiguration("C:\\Saves\\ER0000.sl2", 0);
        var sameFirstCharacter = new EldenRingSaveConfiguration("c:\\saves\\er0000.sl2", 0);
        var secondCharacter = new EldenRingSaveConfiguration("C:\\Saves\\ER0000.sl2", 1);

        EldenRingMissedDeathAdjustments adjustments = EldenRingMissedDeathAdjustments.Empty
            .Increment(firstCharacter)
            .Increment(firstCharacter);

        Assert.Equal(2, adjustments.Get(sameFirstCharacter));
        Assert.Equal(0, adjustments.Get(secondCharacter));
        Assert.Equal(0, adjustments.Decrement(firstCharacter).Decrement(firstCharacter).Decrement(firstCharacter).Get(firstCharacter));
    }

    [Fact]
    public void ProjectionAddsOnlyTheSelectedEldenRingCharacterAdjustment()
    {
        var save = new EldenRingSaveConfiguration("C:\\Saves\\ER0000.sl2", 1);
        var adjustments = EldenRingMissedDeathAdjustments.Empty.Increment(save);
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true,
            eldenRingSave: save,
            eldenRingMissedDeathAdjustments: adjustments);

        Assert.Equal(204, TotalDeathsDisplayProjection.Combine(state, new RuntimeGameObservation(GameId.EldenRing, 203, DateTimeOffset.UtcNow)));
        Assert.Equal(1, TotalDeathsDisplayProjection.Combine(state, observation: null));
    }
}
