using System.Diagnostics;

namespace SoulsTracker.Desktop;

/// <summary>Opens only the already-validated official update release page on an explicit user action.</summary>
public interface IUpdateReleasePageLauncher
{
    bool TryOpen(Uri releasePage);
}

internal sealed class ShellUpdateReleasePageLauncher : IUpdateReleasePageLauncher
{
    public bool TryOpen(Uri releasePage)
    {
        try { Process.Start(new ProcessStartInfo(releasePage.AbsoluteUri) { UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
