using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class PersistentTrackerStateTests
{
    [Fact]
    public void ActiveStateHasNoBloodborneManualContract()
    {
        Assert.DoesNotContain(typeof(PersistentTrackerState).GetProperties(),
            property => property.Name.Contains("Bloodborne", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(PersistentTrackerState).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.Name!.Contains("bloodborne", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultStateRetainsTotalDeathsOverlayConfiguration()
    {
        Assert.True(OverlayConfiguration.Default.TotalDeaths.IsEnabled);
        Assert.Equal(OverlayConfiguration.CurrentSchemaVersion, OverlayConfiguration.Default.SchemaVersion);
    }
}
