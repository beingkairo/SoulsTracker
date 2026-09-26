using System.Diagnostics;

namespace SoulsTracker.Desktop;

/// <summary>Opens only the already-validated official update release page on an explicit user action.</summary>
public interface IUpdateReleasePageLauncher
{
    bool TryOpen(Uri releasePage);
}

internal sealed class ShellUpdateReleasePageLauncher : IUpdateReleasePageLauncher
{
    internal static readonly Uri ProductPage = new("https://beingkairo.com/tools/souls-tracker/");

    internal static bool IsAllowed(Uri page) => page.IsAbsoluteUri &&
        page.Scheme == Uri.UriSchemeHttps &&
        string.Equals(page.OriginalString, ProductPage.AbsoluteUri, StringComparison.Ordinal);

    public bool TryOpen(Uri releasePage)
    {
        if (!IsAllowed(releasePage)) return false;
        try { Process.Start(new ProcessStartInfo(releasePage.AbsoluteUri) { UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
