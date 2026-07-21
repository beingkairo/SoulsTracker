using System.IO;
using SoulsTracker.Desktop;

namespace SoulsTracker.Desktop.Tests;

public sealed class DesktopDataRootResolverTests
{
    [Fact]
    public void DefaultUsesTheNormalLocalApplicationDataSoulsTrackerFolder()
    {
        string localApplicationData = Path.Combine(Path.GetTempPath(), "SoulsTracker-tests", Guid.NewGuid().ToString("N"));

        DesktopDataRootSelection selection = DesktopDataRootResolver.Resolve([], localApplicationData);

        Assert.Equal(Path.Combine(Path.GetFullPath(localApplicationData), "SoulsTracker"), selection.RootPath);
        Assert.False(selection.IsDevelopmentOverride);
    }

    [Fact]
    public void ExplicitDataRootUsesTheRequestedIndependentAbsoluteFolder()
    {
        string localApplicationData = Path.Combine(Path.GetTempPath(), "SoulsTracker-tests", Guid.NewGuid().ToString("N"));
        string developmentRoot = Path.Combine(Path.GetTempPath(), "SoulsTracker-development", Guid.NewGuid().ToString("N"));

        DesktopDataRootSelection selection = DesktopDataRootResolver.Resolve([DesktopDataRootResolver.DataRootOption, developmentRoot], localApplicationData);

        Assert.Equal(Path.GetFullPath(developmentRoot), selection.RootPath);
        Assert.True(selection.IsDevelopmentOverride);
        Assert.False(string.Equals(
            Path.Combine(Path.GetFullPath(localApplicationData), "SoulsTracker"),
            selection.RootPath,
            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("relative-folder")]
    [InlineData("")]
    public void ExplicitDataRootRejectsUnsafeOrAmbiguousPaths(string root)
    {
        Assert.Throws<ArgumentException>(() => DesktopDataRootResolver.Resolve(
            [DesktopDataRootResolver.DataRootOption, root],
            Path.Combine(Path.GetTempPath(), "SoulsTracker-tests")));
    }

    [Fact]
    public void ExplicitDataRootCannotPointAtTheNormalProductionFolder()
    {
        string localApplicationData = Path.Combine(Path.GetTempPath(), "SoulsTracker-tests", Guid.NewGuid().ToString("N"));
        string normalRoot = Path.Combine(localApplicationData, "SoulsTracker");

        Assert.Throws<ArgumentException>(() => DesktopDataRootResolver.Resolve(
            [DesktopDataRootResolver.DataRootOption, normalRoot],
            localApplicationData));
    }
}
