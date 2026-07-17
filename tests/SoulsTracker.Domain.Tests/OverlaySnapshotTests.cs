using System.Reflection;
using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class OverlaySnapshotTests
{
    [Fact]
    public void NoGameSnapshotRequiresUnavailableDeathsAndAnEmptyBossList()
    {
        OverlaySnapshot snapshot = new(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 0,
            UtcTimestamp,
            selectedGame: null,
            TotalDeathsDisplayValue.Unavailable,
            bosses: []);

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal(0L, snapshot.SequenceNumber);
        Assert.Null(snapshot.SelectedGame);
        Assert.Equal(TotalDeathsDisplaySource.Unavailable, snapshot.TotalDeaths.Source);
        Assert.Null(snapshot.TotalDeaths.Value);
        Assert.Empty(snapshot.Bosses);
        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            UtcTimestamp,
            selectedGame: null,
            TotalDeathsDisplayValue.FromManualBloodborneCounter(ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne)),
            bosses: []));
        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            UtcTimestamp,
            selectedGame: null,
            TotalDeathsDisplayValue.Unavailable,
            bosses: [CreateBossEntry(GameId.Ds1, 0, isDefeated: false)]));
    }

    [Fact]
    public void ManualBloodborneSnapshotUsesTheManualCounterAndPreservesBossOrder()
    {
        ManualBloodborneDeathCounter counter = ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, initialValue: 7);
        OverlayBossEntry firstBoss = CreateBossEntry(GameId.Bloodborne, 1, isDefeated: true);
        OverlayBossEntry secondBoss = CreateBossEntry(GameId.Bloodborne, 0, isDefeated: false);
        OverlaySnapshot snapshot = new(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 4,
            UtcTimestamp,
            new OverlayGameMetadata(GameId.Bloodborne),
            TotalDeathsDisplayValue.FromManualBloodborneCounter(counter),
            [firstBoss, secondBoss]);

        Assert.Equal(GameId.Bloodborne, snapshot.SelectedGame?.GameId);
        Assert.Equal(TotalDeathsDisplaySource.ManualBloodborne, snapshot.TotalDeaths.Source);
        Assert.Equal(7L, snapshot.TotalDeaths.Value);
        Assert.Equal(
            [firstBoss.BossId, secondBoss.BossId],
            snapshot.Bosses.Select(static boss => boss.BossId));
        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 5,
            UtcTimestamp,
            new OverlayGameMetadata(GameId.Ds1),
            TotalDeathsDisplayValue.FromManualBloodborneCounter(counter),
            bosses: []));
    }

    [Fact]
    public void AutomaticSnapshotUsesAMatchingRuntimeObservation()
    {
        RuntimeGameObservation observation = new(GameId.Ds1, totalDeaths: 12, UtcTimestamp);
        OverlaySnapshot snapshot = new(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 2,
            UtcTimestamp,
            new OverlayGameMetadata(GameId.Ds1),
            TotalDeathsDisplayValue.FromRuntimeObservation(observation),
            [CreateBossEntry(GameId.Ds1, 0, isDefeated: true)]);

        Assert.Equal(TotalDeathsDisplaySource.GameLifetimeReader, snapshot.TotalDeaths.Source);
        Assert.Equal(GameId.Ds1, snapshot.TotalDeaths.GameId);
        Assert.Equal(12L, snapshot.TotalDeaths.Value);
        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 3,
            UtcTimestamp,
            new OverlayGameMetadata(GameId.Ds2),
            TotalDeathsDisplayValue.FromRuntimeObservation(observation),
            bosses: []));
        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 3,
            UtcTimestamp,
            new OverlayGameMetadata(GameId.Bloodborne),
            TotalDeathsDisplayValue.FromRuntimeObservation(observation),
            bosses: []));
    }

    [Fact]
    public void SnapshotRejectsMismatchedBossesUnsupportedValuesAndNonUtcTimestamps()
    {
        OverlayGameMetadata ds1 = new(GameId.Ds1);

        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            UtcTimestamp,
            ds1,
            TotalDeathsDisplayValue.Unavailable,
            bosses: [CreateBossEntry(GameId.Ds2, 0, isDefeated: false)]));
        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            UtcTimestamp,
            ds1,
            TotalDeathsDisplayValue.Unavailable,
            bosses: [new OverlayBossEntry(new BossDefinition(BossId.Parse("unknown_boss"), "Unknown Boss"), false)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: -1,
            UtcTimestamp,
            ds1,
            TotalDeathsDisplayValue.Unavailable,
            bosses: []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlaySnapshot(
            schemaVersion: 2,
            sequenceNumber: 1,
            UtcTimestamp,
            ds1,
            TotalDeathsDisplayValue.Unavailable,
            bosses: []));
        Assert.Throws<ArgumentException>(() => new OverlaySnapshot(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.FromHours(-4)),
            ds1,
            TotalDeathsDisplayValue.Unavailable,
            bosses: []));
    }

    [Fact]
    public void SnapshotSerializesWithoutSecretsOrMutationCapabilities()
    {
        OverlaySnapshot snapshot = new(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            UtcTimestamp,
            new OverlayGameMetadata(GameId.Ds1),
            TotalDeathsDisplayValue.Unavailable,
            bosses: [CreateBossEntry(GameId.Ds1, 0, isDefeated: false)]);

        string json = JsonSerializer.Serialize(snapshot);
        PropertyInfo[] properties = typeof(OverlaySnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        MethodInfo[] mutationMethods = typeof(OverlaySnapshot)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();

        Assert.Contains("\"SchemaVersion\":1", json);
        Assert.DoesNotContain(new string('A', 43), json);
        Assert.DoesNotContain("\"AccessToken\"", json);
        Assert.DoesNotContain("\"Endpoint\"", json);
        Assert.DoesNotContain(properties, static property =>
            property.PropertyType == typeof(OverlayAccessToken) ||
            property.PropertyType == typeof(OverlayEndpointConfiguration) ||
            property.PropertyType == typeof(OverlayConfiguration));
        Assert.Empty(mutationMethods);
    }

    [Fact]
    public void PresentationProjectionIncludesOnlyValidatedBrowserSettings()
    {
        OverlayConfiguration configuration = new(
            OverlayConfiguration.CurrentSchemaVersion,
            OverlayEndpointConfiguration.Unassigned,
            new TotalDeathsOverlayOptions(isEnabled: false, showGameName: false),
            new BossListOverlayOptions(isEnabled: true, BossListVisibilityMode.Remaining));

        OverlayPresentationConfiguration presentation = OverlayPresentationConfiguration.From(configuration);
        OverlaySnapshot snapshot = new(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            UtcTimestamp,
            selectedGame: null,
            TotalDeathsDisplayValue.Unavailable,
            bosses: [],
            presentation);

        string json = JsonSerializer.Serialize(snapshot);

        Assert.False(presentation.IsTotalDeathsEnabled);
        Assert.False(presentation.ShowGameName);
        Assert.True(presentation.IsBossListEnabled);
        Assert.Equal(BossListVisibilityMode.Remaining, presentation.BossListVisibilityMode);
        Assert.Contains("\"Presentation\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Endpoint\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AccessToken\"", json, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayPresentationConfiguration(true, true, true, (BossListVisibilityMode)99));
    }

    private static DateTimeOffset UtcTimestamp { get; } = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static OverlayBossEntry CreateBossEntry(GameId gameId, int index, bool isDefeated) =>
        new(GameCatalog.GetRequired(gameId).BossCatalog[index], isDefeated);
}
