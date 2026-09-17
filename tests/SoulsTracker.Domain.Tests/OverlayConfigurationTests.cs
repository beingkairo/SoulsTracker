using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class OverlayConfigurationTests
{
    [Fact]
    public void ConfigurationContainsPresentationWithoutLocalEndpointCapability()
    {
        Assert.Null(typeof(OverlayConfiguration).GetProperty("Endpoint"));
        Assert.Null(typeof(OverlayConfiguration).Assembly.GetType("SoulsTracker.Domain.OverlayAccessToken"));
        Assert.Null(typeof(OverlayConfiguration).Assembly.GetType("SoulsTracker.Domain.OverlayEndpointConfiguration"));
        string json = JsonSerializer.Serialize(OverlayConfiguration.Default);
        Assert.DoesNotContain("Port", json);
        Assert.DoesNotContain("AccessToken", json);
    }

    [Fact]
    public void DefaultsAreTotalDeathsOnlyAndEnabled()
    {
        OverlayConfiguration configuration = OverlayConfiguration.Default;
        Assert.Equal(1, configuration.SchemaVersion);

        Assert.True(configuration.TotalDeaths.IsEnabled);
        Assert.False(configuration.TotalDeaths.ShowGameName);
        Assert.True(configuration.TotalDeaths.CompactTitle);
    }


}
