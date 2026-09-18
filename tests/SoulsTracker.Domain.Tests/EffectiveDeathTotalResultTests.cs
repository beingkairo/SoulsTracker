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

    [Theory]
    [InlineData("demons_souls", "demons_souls")]
    [InlineData("ds1", "ds1")]
    [InlineData("ds2", "ds2")]
    [InlineData("ds3", "ds3")]
    [InlineData("sekiro", "sekiro")]
    [InlineData("bloodborne", "bloodborne")]
    [InlineData("elden_ring", "elden_ring::-1")]
    [InlineData("black_myth_wukong", "black_myth_wukong:")]
    [InlineData("lies_of_p", "lies_of_p:")]
    public void SourceIdentityUsesExactDefaultStateString(string gameId, string expected)
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.Parse(gameId),
            OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true);

        Assert.Equal(expected, EffectiveDeathTotalResult.SourceIdentityFor(state));
    }

    [Theory]
    [InlineData("demons_souls", "demons_souls")]
    [InlineData("ds1", "ds1")]
    [InlineData("ds2", "ds2")]
    [InlineData("ds3", "ds3")]
    [InlineData("sekiro", "sekiro")]
    [InlineData("bloodborne", "bloodborne")]
    public void SourceIdentityIgnoresIrrelevantSaveConfigurations(string gameId, string expected)
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.Parse(gameId),
            OverlayConfiguration.Default,
            eldenRingSave: new EldenRingSaveConfiguration("C:/Synthetic/ER0000.sl2", 9),
            blackMythWukongSave: new BlackMythWukongSaveConfiguration("C:/Synthetic/ArchiveSaveFile.1.sav"),
            liesOfPSave: new LiesOfPSaveConfiguration("C:/Synthetic/SaveData-1_Character_2.sav"));

        Assert.Equal(expected, EffectiveDeathTotalResult.SourceIdentityFor(state));
    }

    [Theory]
    [InlineData(null, -1, "elden_ring::-1")]
    [InlineData("", -1, "elden_ring::-1")]
    [InlineData(" \t ", -1, "elden_ring::-1")]
    [InlineData(null, 0, "elden_ring::0")]
    [InlineData(null, 9, "elden_ring::9")]
    [InlineData("", 1, "elden_ring::1")]
    [InlineData(" \t ", 9, "elden_ring::9")]
    [InlineData("C:/Synthetic/ER0000.sl2", -1, "elden_ring:C:/Synthetic/ER0000.sl2:-1")]
    [InlineData("C:/Synthetic/ER0000.sl2", 0, "elden_ring:C:/Synthetic/ER0000.sl2:0")]
    [InlineData("C:/Synthetic/ER0000.sl2", 1, "elden_ring:C:/Synthetic/ER0000.sl2:1")]
    [InlineData("C:/Synthetic/ER0000.sl2", 9, "elden_ring:C:/Synthetic/ER0000.sl2:9")]
    [InlineData("C:/Other/ER0000.sl2", 0, "elden_ring:C:/Other/ER0000.sl2:0")]
    [InlineData("c:/sYnThEtIc/er0000.SL2", 0, "elden_ring:c:/sYnThEtIc/er0000.SL2:0")]
    [InlineData(@"C:\Synthetic\ER0000.sl2", 0, @"elden_ring:C:\Synthetic\ER0000.sl2:0")]
    [InlineData("Synthetic:Selection/ER0000.sl2", 9, "elden_ring:Synthetic:Selection/ER0000.sl2:9")]
    public void SourceIdentityPreservesExactEldenRingPathAndSlot(string? path, int slot, string expected)
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.EldenRing,
            OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true,
            eldenRingSave: new EldenRingSaveConfiguration(path, slot));

        Assert.Equal(expected, EffectiveDeathTotalResult.SourceIdentityFor(state));
    }

    [Theory]
    [InlineData(null, "black_myth_wukong:")]
    [InlineData("", "black_myth_wukong:")]
    [InlineData(" \t ", "black_myth_wukong:")]
    [InlineData("C:/Synthetic/ArchiveSaveFile.1.sav", "black_myth_wukong:C:/Synthetic/ArchiveSaveFile.1.sav")]
    [InlineData("C:/Synthetic/ArchiveSaveFile.2.sav", "black_myth_wukong:C:/Synthetic/ArchiveSaveFile.2.sav")]
    [InlineData("C:/Other/ArchiveSaveFile.1.sav", "black_myth_wukong:C:/Other/ArchiveSaveFile.1.sav")]
    [InlineData("c:/sYnThEtIc/archivesavefile.1.SAV", "black_myth_wukong:c:/sYnThEtIc/archivesavefile.1.SAV")]
    [InlineData(@"C:\Synthetic\ArchiveSaveFile.1.sav", @"black_myth_wukong:C:\Synthetic\ArchiveSaveFile.1.sav")]
    [InlineData("Synthetic:Selection/ArchiveSaveFile.1.sav", "black_myth_wukong:Synthetic:Selection/ArchiveSaveFile.1.sav")]
    public void SourceIdentityPreservesExactWukongPath(string? path, string expected)
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.BlackMythWukong,
            OverlayConfiguration.Default, blackMythWukongSave: new BlackMythWukongSaveConfiguration(path));

        Assert.Equal(expected, EffectiveDeathTotalResult.SourceIdentityFor(state));
    }

    [Theory]
    [InlineData(null, "lies_of_p:")]
    [InlineData("", "lies_of_p:")]
    [InlineData(" \t ", "lies_of_p:")]
    [InlineData("C:/Synthetic/SaveData-1_Character_1.sav", "lies_of_p:C:/Synthetic/SaveData-1_Character_1.sav")]
    [InlineData("C:/Synthetic/SaveData-1_Character_2.sav", "lies_of_p:C:/Synthetic/SaveData-1_Character_2.sav")]
    [InlineData("C:/Other/SaveData-12_Character_1.sav", "lies_of_p:C:/Other/SaveData-12_Character_1.sav")]
    [InlineData("c:/sYnThEtIc/savedata-1_character_1.SAV", "lies_of_p:c:/sYnThEtIc/savedata-1_character_1.SAV")]
    [InlineData(@"C:\Synthetic\SaveData-1_Character_2.sav", @"lies_of_p:C:\Synthetic\SaveData-1_Character_2.sav")]
    [InlineData("Synthetic:Selection/SaveData-1_Character_1.sav", "lies_of_p:Synthetic:Selection/SaveData-1_Character_1.sav")]
    public void SourceIdentityPreservesExactLiesPath(string? path, string expected)
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.LiesOfP,
            OverlayConfiguration.Default, liesOfPSave: new LiesOfPSaveConfiguration(path));

        Assert.Equal(expected, EffectiveDeathTotalResult.SourceIdentityFor(state));
    }

    private static PersistentTrackerState Wukong(string path) => new(PersistentTrackerState.CurrentSchemaVersion, GameId.BlackMythWukong,
        OverlayConfiguration.Default,
        blackMythWukongSave: new BlackMythWukongSaveConfiguration(path));

    private static PersistentTrackerState Lies(string path) => new(PersistentTrackerState.CurrentSchemaVersion, GameId.LiesOfP,
        OverlayConfiguration.Default,
        liesOfPSave: new LiesOfPSaveConfiguration(path));
}
