using System.Reflection;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class DeathCounterTests
{
    [Fact]
    public void ManualCounterStartsAtZeroAndNeverBecomesNegative()
    {
        ManualDeathCounter counter = ManualDeathCounter.CreateFor(GameId.DemonsSouls);
        ManualDeathCounter incrementedCounter = counter.Increment();
        ManualDeathCounter decrementedCounter = incrementedCounter.Decrement();

        Assert.Equal(0L, counter.Value);
        Assert.Equal(1L, incrementedCounter.Value);
        Assert.Equal(0L, decrementedCounter.Value);
        Assert.Equal(0L, decrementedCounter.Decrement().Value);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManualDeathCounter.CreateFor(GameId.DemonsSouls, -1));
    }

    [Fact]
    public void ManualCounterSupportsEachApprovedManualProfileOnly()
    {
        foreach (GameId gameId in GameId.All.Where(static gameId => gameId != GameId.DemonsSouls))
        {
            Assert.Throws<InvalidOperationException>(() =>
                ManualDeathCounter.CreateFor(gameId));
        }
    }

    [Fact]
    public void ManualDisplayRejectsEveryAutomaticGame()
    {
        var counter = ManualDeathCounter.CreateFor(GameId.DemonsSouls, 7);
        foreach (GameId gameId in GameId.All.Where(static gameId => gameId != GameId.DemonsSouls))
        {
            Assert.Throws<ArgumentException>(() => TotalDeathsDisplayValue.FromManualCounter(gameId, counter));
        }

        TotalDeathsDisplayValue display = TotalDeathsDisplayValue.FromManualCounter(GameId.DemonsSouls, counter);
        Assert.Equal(GameId.DemonsSouls, display.GameId);
        Assert.Equal(7, display.Value);
    }

    [Fact]
    public void ManualCounterCannotExistAsAUsableDefaultValue()
    {
        ManualDeathCounter defaultCounter = default!;
        ConstructorInfo[] publicInstanceConstructors = typeof(ManualDeathCounter)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(defaultCounter);
        Assert.True(typeof(ManualDeathCounter).IsClass);
        Assert.True(typeof(ManualDeathCounter).IsSealed);
        Assert.Empty(publicInstanceConstructors);
    }

    [Fact]
    public void GameLifetimeDeathTotalRejectsNegativeValuesAndHasNoAdjustmentOperations()
    {
        GameLifetimeDeathTotal total = new(42);
        string[] declaredOperationNames = typeof(GameLifetimeDeathTotal)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .ToArray();

        Assert.Equal(42L, total.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameLifetimeDeathTotal(-1));
        Assert.DoesNotContain("Increment", declaredOperationNames);
        Assert.DoesNotContain("Decrement", declaredOperationNames);
        Assert.DoesNotContain("Reset", declaredOperationNames);
        Assert.DoesNotContain("Adjust", declaredOperationNames);
    }
}
