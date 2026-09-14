using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class TextExportConfigurationTests
{
    [Fact]
    public void OnlyTxtDeathTargetsAreAcceptedAndDefaultIsDisabled()
    {
        Assert.False(TextExportConfiguration.Default.DeathsEnabled);
        Assert.True(new TextExportConfiguration("C:\\exports\\deaths.txt", true).DeathsEnabled);
        Assert.Throws<ArgumentException>(() => new TextExportConfiguration("C:\\exports\\deaths.csv", true));
    }

    [Fact]
    public void EnablementIntentIsPreservedBeforeATxtTargetIsSelected()
    {
        TextExportConfiguration configuration = new(null, true);
        Assert.True(configuration.DeathsEnabled);
        Assert.Null(configuration.DeathsPath);
    }
}