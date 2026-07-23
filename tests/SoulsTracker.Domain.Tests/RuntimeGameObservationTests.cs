using System.Reflection;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class RuntimeGameObservationTests
{
    [Fact]
    public void RuntimeObservationsAcceptOnlySelectableAutomaticProfilesWithUtcTotals()
    {
        GameId[] automaticGameIds = [GameId.Ds1, GameId.Ds2, GameId.Ds3, GameId.Sekiro, GameId.EldenRing];
        DateTimeOffset observedAtUtc = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

        foreach (GameId gameId in automaticGameIds)
        {
            RuntimeGameObservation observation = new(gameId, new GameLifetimeDeathTotal(42), observedAtUtc);

            Assert.Equal(gameId, observation.GameId);
            Assert.Equal(42L, observation.TotalDeaths.Value);
            Assert.Equal(TimeSpan.Zero, observation.ObservedAtUtc.Offset);
        }
    }

    [Fact]
    public void RuntimeObservationsRejectManualSoonUnknownNegativeAndNonUtcInputs()
    {
        DateTimeOffset observedAtUtc = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        GameLifetimeDeathTotal total = new(42);

        Assert.Throws<ArgumentException>(() => new RuntimeGameObservation(GameId.Bloodborne, total, observedAtUtc));
        Assert.Throws<ArgumentException>(() => new RuntimeGameObservation(GameId.DemonsSouls, total, observedAtUtc));
        Assert.Throws<ArgumentException>(() => new RuntimeGameObservation(CreateUnknownGameId(), total, observedAtUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeGameObservation(GameId.Ds1, -1, observedAtUtc));
        Assert.Throws<ArgumentException>(() => new RuntimeGameObservation(
            GameId.Ds1,
            total,
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.FromHours(-4))));
    }

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
