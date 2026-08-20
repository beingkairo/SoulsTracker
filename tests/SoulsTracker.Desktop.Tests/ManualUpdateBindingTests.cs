using System.IO;

namespace SoulsTracker.Desktop.Tests;

public sealed class ManualUpdateBindingTests
{
    [Fact]
    public void SettingsExposeAccessibleManualCheckBusyRetryAndReleaseBindings()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "SoulsTracker.Desktop", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"CheckForUpdatesButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanCheckForUpdates}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RetryUpdateCheckButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanRetryUpdateCheck", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenUpdateReleasePageButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanOpenAvailableUpdateReleasePage", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpdateCheckStatusTextBlock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CheckForUpdates_Click", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SoulsTracker.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
