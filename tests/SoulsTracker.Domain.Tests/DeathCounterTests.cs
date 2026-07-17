using System.Reflection;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class DeathCounterTests
{
    [Fact]
    public void ManualBloodborneCounterStartsAtZeroAndNeverBecomesNegative()
    {
        ManualBloodborneDeathCounter counter = ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne);
        ManualBloodborneDeathCounter incrementedCounter = counter.Increment();
        ManualBloodborneDeathCounter decrementedCounter = incrementedCounter.Decrement();

        Assert.Equal(0L, counter.Value);
        Assert.Equal(1L, incrementedCounter.Value);
        Assert.Equal(0L, decrementedCounter.Value);
        Assert.Equal(0L, decrementedCounter.Decrement().Value);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, -1));
    }

    [Fact]
    public void ManualBloodborneCounterIsUnavailableForEveryOtherGame()
    {
        foreach (GameId gameId in GameId.All.Where(static gameId => gameId != GameId.Bloodborne && gameId != GameId.DemonsSouls))
        {
            Assert.Throws<InvalidOperationException>(() =>
                ManualBloodborneDeathCounter.CreateFor(gameId));
        }
    }

    [Fact]
    public void ManualBloodborneCounterCannotExistAsAUsableDefaultValue()
    {
        ManualBloodborneDeathCounter defaultCounter = default!;
        ConstructorInfo[] publicInstanceConstructors = typeof(ManualBloodborneDeathCounter)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(defaultCounter);
        Assert.True(typeof(ManualBloodborneDeathCounter).IsClass);
        Assert.True(typeof(ManualBloodborneDeathCounter).IsSealed);
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
