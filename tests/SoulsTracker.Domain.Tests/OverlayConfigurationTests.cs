using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class OverlayConfigurationTests
{
    [Fact]
    public void DefaultsAreTotalDeathsOnlyAndEnabled()
    {
        OverlayConfiguration configuration = OverlayConfiguration.Default;
        Assert.Equal(1, configuration.SchemaVersion);
        Assert.False(configuration.Endpoint.IsAssigned);
        Assert.True(configuration.TotalDeaths.IsEnabled);
        Assert.False(configuration.TotalDeaths.ShowGameName);
        Assert.True(configuration.TotalDeaths.CompactTitle);
    }

    [Fact]
    public void EndpointValidatesAndTokensAreRedacted()
    {
        OverlayAccessToken token = OverlayAccessToken.Parse(new string('A', 43));
        Assert.True(new OverlayEndpointConfiguration(1024, token).IsAssigned);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayEndpointConfiguration(1023, token));
        Assert.DoesNotContain(new string('A', 43), token.ToString());
        Assert.DoesNotContain("AccessToken", JsonSerializer.Serialize(OverlayConfiguration.Default));
    }
}
