using System.Reflection;
using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class OverlayConfigurationTests
{
    [Fact]
    public void DefaultOverlayConfigurationIsStableAndEnabled()
    {
        OverlayConfiguration configuration = OverlayConfiguration.Default;

        Assert.Equal(1, configuration.SchemaVersion);
        Assert.False(configuration.Endpoint.IsAssigned);
        Assert.Null(configuration.Endpoint.Port);
        Assert.Null(configuration.Endpoint.AccessToken);
        Assert.True(configuration.TotalDeaths.IsEnabled);
        Assert.False(configuration.TotalDeaths.ShowGameName);
        Assert.True(configuration.TotalDeaths.CompactTitle);
        Assert.True(configuration.BossList.IsEnabled);
        Assert.Equal(BossListVisibilityMode.All, configuration.BossList.VisibilityMode);
    }

    [Fact]
    public void EndpointConfigurationRequiresACompleteValidPortAndTokenPair()
    {
        OverlayAccessToken token = OverlayAccessToken.Parse(ValidToken);

        OverlayEndpointConfiguration unassigned = new(port: null, accessToken: null);
        OverlayEndpointConfiguration minimumPort = new(1024, token);
        OverlayEndpointConfiguration maximumPort = new(65535, token);

        Assert.False(unassigned.IsAssigned);
        Assert.True(minimumPort.IsAssigned);
        Assert.Equal(1024, minimumPort.Port);
        Assert.Equal(token, minimumPort.AccessToken);
        Assert.Equal(65535, maximumPort.Port);
        Assert.Throws<ArgumentException>(() => new OverlayEndpointConfiguration(port: null, token));
        Assert.Throws<ArgumentException>(() => new OverlayEndpointConfiguration(1024, accessToken: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayEndpointConfiguration(1023, token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayEndpointConfiguration(65536, token));
    }

    [Fact]
    public void AccessTokensAreCanonicalRedactedAndNeverSerializedByConfiguration()
    {
        OverlayAccessToken token = OverlayAccessToken.Parse(ValidToken);
        OverlayConfiguration configuration = new(
            OverlayConfiguration.CurrentSchemaVersion,
            new OverlayEndpointConfiguration(8787, token),
            TotalDeathsOverlayOptions.Default,
            BossListOverlayOptions.Default);

        Assert.Equal(token, OverlayAccessToken.Parse(ValidToken));
        Assert.DoesNotContain(ValidToken, token.ToString());

        foreach (string invalidToken in InvalidTokens)
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() => OverlayAccessToken.Parse(invalidToken));

            Assert.DoesNotContain(invalidToken, exception.Message);
        }

        string configurationJson = JsonSerializer.Serialize(configuration);

        Assert.DoesNotContain(ValidToken, configurationJson);
        Assert.DoesNotContain("\"AccessToken\"", configurationJson);
    }

    [Fact]
    public void OverlayOptionsRejectUnsupportedValuesAndVersionsWithoutMutationMethods()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BossListOverlayOptions(
            isEnabled: true,
            visibilityMode: (BossListVisibilityMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayConfiguration(
            schemaVersion: 2,
            OverlayEndpointConfiguration.Unassigned,
            TotalDeathsOverlayOptions.Default,
            BossListOverlayOptions.Default));

        Type[] contractTypes =
        [
            typeof(OverlayConfiguration),
            typeof(OverlayEndpointConfiguration),
            typeof(TotalDeathsOverlayOptions),
            typeof(BossListOverlayOptions),
        ];

        foreach (Type contractType in contractTypes)
        {
            MethodInfo[] mutationMethods = contractType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .ToArray();

            Assert.Empty(mutationMethods);
        }
    }

    [Fact]
    public void AppearanceUsesOnlyBoundedColorsFontsAndGeometry()
    {
        OverlayAppearance appearance = new("Deaths", "Verdana", 48, "#FFFFFF", "#FFD54F", "#000000", 100, 20, 8, OverlayTextAlignment.Center);
        Assert.Equal("Verdana", appearance.FontFamily);
        Assert.Equal(OverlayTextAlignment.Center, appearance.Alignment);
        OverlayAppearance blankTitle = new("", "Segoe UI", 42, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left);
        Assert.Equal("", blankTitle.Title);
        Assert.Throws<ArgumentException>(() => new OverlayAppearance("Deaths", "Remote; color:red", 42, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayAppearance("Deaths", "Segoe UI", 97, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left));
        Assert.Throws<ArgumentException>(() => new OverlayAppearance("Deaths", "Segoe UI", 42, "red", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left));
    }

    [Fact]
    public void AppearanceTextEffectsAreBoundedAndDefaultToDisabled()
    {
        OverlayAppearance appearance = new("Deaths", "Segoe UI", 42, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left,
            outlineEnabled: true, outlineColor: "#112233", outlineWidth: 8,
            shadowEnabled: true, shadowColor: "#445566", shadowOffsetX: -20, shadowOffsetY: 20, shadowBlur: 20);

        Assert.True(appearance.OutlineEnabled);
        Assert.Equal("#112233", appearance.OutlineColor);
        Assert.Equal(8, appearance.OutlineWidth);
        Assert.True(appearance.ShadowEnabled);
        Assert.Equal("#445566", appearance.ShadowColor);
        Assert.Equal(-20, appearance.ShadowOffsetX);
        Assert.Equal(20, appearance.ShadowOffsetY);
        Assert.Equal(20, appearance.ShadowBlur);
        Assert.True(OverlayAppearance.Default.OutlineEnabled);
        Assert.False(OverlayAppearance.Default.ShadowEnabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayAppearance("Deaths", "Segoe UI", 42, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left, outlineWidth: 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayAppearance("Deaths", "Segoe UI", 42, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left, shadowOffsetX: -21));
        Assert.Throws<ArgumentException>(() => new OverlayAppearance("Deaths", "Segoe UI", 42, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Left, outlineColor: "red"));
    }

    [Fact]
    public void OverlayDefaultsAreIndependentAndTotalDeathsAlwaysUsesLeftAlignment()
    {
        OverlayAppearance centered = new("Deaths", "Segoe UI", 42, "#FFFFFF", "#A78BFA", "#15171B", 88, 16, 8, OverlayTextAlignment.Center);
        TotalDeathsOverlayOptions total = new(true, true, appearance: centered);

        Assert.Equal("Total Deaths", OverlayAppearance.Default.Title);
        Assert.Equal("Boss List", BossListOverlayOptions.Default.Appearance.Title);
        Assert.Equal(OverlayTextAlignment.Left, total.Appearance.Alignment);
        Assert.Equal(OverlayTextAlignment.Center, centered.Alignment);
    }

    [Fact]
    public void CenteredBossListsNormalizeMarkersToNone()
    {
        OverlayAppearance centered = new("Bosses", "Segoe UI", 30, "#FFFFFF", "#A78BFA", "#15171B", 0, 4, 0, OverlayTextAlignment.Center);

        BossListOverlayOptions options = new(true, BossListVisibilityMode.All, centered, showCheckmark: true, showDefeatedSkull: true);

        Assert.False(options.ShowCheckmark);
        Assert.False(options.ShowDefeatedSkull);
    }

    private static string ValidToken { get; } = new('A', 43);

    private static IReadOnlyList<string> InvalidTokens { get; } =
    [
        new string('A', 42),
        new string('A', 44),
        new string('A', 42) + "=",
        new string('A', 42) + "$",
        new string('A', 42) + "B",
    ];
}
