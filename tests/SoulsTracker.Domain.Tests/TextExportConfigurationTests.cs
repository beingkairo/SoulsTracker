using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class TextExportConfigurationTests
{
    [Fact]
    public void OnlyTxtTargetsAreAcceptedAndDefaultIsDisabled()
    {
        Assert.False(TextExportConfiguration.Default.DeathsEnabled);
        Assert.False(TextExportConfiguration.Default.BossListEnabled);
        Assert.True(new TextExportConfiguration("C:\\exports\\deaths.txt", true, "C:\\exports\\bosses.TXT", true).DeathsEnabled);
        Assert.Throws<ArgumentException>(() => new TextExportConfiguration("C:\\exports\\deaths.csv", true, null, false));
    }

    [Fact]
    public void EnabledExportsRequireAnExplicitTxtTarget()
    {
        TextExportConfiguration configuration = new(null, true, null, true);

        Assert.False(configuration.DeathsEnabled);
        Assert.False(configuration.BossListEnabled);
    }
}
