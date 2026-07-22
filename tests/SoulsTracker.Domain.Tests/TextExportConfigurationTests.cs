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
    public void EnablementIntentIsPreservedBeforeATxtTargetIsSelected()
    {
        TextExportConfiguration configuration = new(null, true, null, true);

        Assert.True(configuration.DeathsEnabled);
        Assert.True(configuration.BossListEnabled);
        Assert.Null(configuration.DeathsPath);
        Assert.Null(configuration.BossListPath);
    }
}
