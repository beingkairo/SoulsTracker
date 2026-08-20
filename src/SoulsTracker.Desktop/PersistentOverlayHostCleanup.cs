using SoulsTracker.Application;

namespace SoulsTracker.Desktop;

internal enum ActiveOverlayPath
{
    None,
    InApp,
    ConfirmedInAppFallback,
    PersistentHost,
}

/// <summary>Orders optional-host disable work so a failed stop never removes next-sign-in registration.</summary>
internal static class PersistentOverlayHostCleanup
{
    public static Task<bool> DisableAsync(
        ActiveOverlayPath activePath,
        IOverlayEndpointAccess endpoint,
        IOverlayHostStartupRegistration registration,
        Func<IOverlayEndpointAccess, Task<bool>> stopAsync) =>
        activePath == ActiveOverlayPath.ConfirmedInAppFallback
            ? RemoveRegistrationAsync(registration)
            : StopThenRemoveRegistrationAsync(endpoint, registration, stopAsync);

    public static async Task<bool> StopThenRemoveRegistrationAsync(
        IOverlayEndpointAccess endpoint,
        IOverlayHostStartupRegistration registration,
        Func<IOverlayEndpointAccess, Task<bool>> stopAsync)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(stopAsync);
        try
        {
            if (!await stopAsync(endpoint).ConfigureAwait(false)) return false;
            registration.Disable();
            return true;
        }
        catch { return false; }
    }

    private static Task<bool> RemoveRegistrationAsync(IOverlayHostStartupRegistration registration)
    {
        try { registration.Disable(); return Task.FromResult(true); }
        catch { return Task.FromResult(false); }
    }
}
