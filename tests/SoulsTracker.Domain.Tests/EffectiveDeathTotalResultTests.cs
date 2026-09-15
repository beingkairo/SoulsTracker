using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class EffectiveDeathTotalResultTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MatchingObservationPublishesEffectiveTotal()
    {
        PersistentTrackerState state = Wukong("/saves/ArchiveSaveFile.1.sav");
        EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(
            state, new RuntimeGameObservation(GameId.BlackMythWukong, new GameLifetimeDeathTotal(12), ObservedAt, EffectiveDeathTotalResult.SourceIdentityFor(state)));

        Assert.Equal(EffectiveDeathTotalStatus.Synced, result.Status);
        Assert.Equal(12, result.EffectiveDisplayedTotal);
    }

    [Theory]
    [InlineData("Black Myth: Wukong:C:/saves/other.sav")]
    [InlineData("Lies of P:C:/saves/other.sav")]
    public void MismatchedSaveObservationIsUnavailable(string observedSource)
    {
        GameId game = observedSource.StartsWith("Lies", StringComparison.Ordinal) ? GameId.LiesOfP : GameId.BlackMythWukong;
        PersistentTrackerState state = game == GameId.LiesOfP ? Lies("/saves/SaveData-1_Character_1.sav") : Wukong("/saves/ArchiveSaveFile.1.sav");
        EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(state, new RuntimeGameObservation(game, new GameLifetimeDeathTotal(12), ObservedAt, observedSource));

        Assert.Equal(EffectiveDeathTotalStatus.SourceMismatch, result.Status);
        Assert.Null(result.EffectiveDisplayedTotal);
    }

    [Fact]
    public void EldenRingSaveOrSlotMismatchCannotApplyAdjustment()
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.EldenRing,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true, eldenRingSave: new EldenRingSaveConfiguration("C:/saves/ER0000.sl2", 1),
            eldenRingMissedDeathAdjustments: new EldenRingMissedDeathAdjustments([new EldenRingMissedDeathAdjustment("C:/saves/ER0000.sl2", 1, 3)]));
        EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(state,
            new RuntimeGameObservation(GameId.EldenRing, new GameLifetimeDeathTotal(12), ObservedAt, "Elden Ring:C:/saves/other.sl2:1"));

        Assert.Equal(EffectiveDeathTotalStatus.SourceMismatch, result.Status);
        Assert.Null(result.EffectiveDisplayedTotal);
    }

    private static PersistentTrackerState Wukong(string path) => new(PersistentTrackerState.CurrentSchemaVersion, GameId.BlackMythWukong,
        OverlayConfiguration.Default,
        blackMythWukongSave: new BlackMythWukongSaveConfiguration(path));

    private static PersistentTrackerState Lies(string path) => new(PersistentTrackerState.CurrentSchemaVersion, GameId.LiesOfP,
        OverlayConfiguration.Default,
        liesOfPSave: new LiesOfPSaveConfiguration(path));
}
