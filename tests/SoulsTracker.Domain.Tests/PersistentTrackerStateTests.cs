using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class PersistentTrackerStateTests
{
    [Fact]
    public void DefaultStateRetainsTotalDeathsOverlayConfiguration()
    {
        Assert.True(OverlayConfiguration.Default.TotalDeaths.IsEnabled);
        Assert.Equal(OverlayConfiguration.CurrentSchemaVersion, OverlayConfiguration.Default.SchemaVersion);
    }
}
