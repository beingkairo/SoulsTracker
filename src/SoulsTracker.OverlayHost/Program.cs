using SoulsTracker.Application;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.OverlayHost;

/// <summary>Small sign-in process for the explicit local OBS recovery option.</summary>
internal static class Program
{
    public static async Task<int> Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulsTracker");
        await using var repository = new SqliteTrackerStateRepository(root, "tracker.db");
        TrackerStateLoadResult loaded = await repository.LoadAsync().ConfigureAwait(false);
        if (!loaded.IsSuccess || !loaded.State!.OverlayConfiguration.Endpoint.IsAssigned) return 1;
        var access = new OverlayEndpointAccessFactory().FromConfiguration(loaded.State.OverlayConfiguration.Endpoint);
        await using var host = new PersistentOverlayHost(access);
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; stopping.Cancel(); };
        try
        {
            await host.StartAsync(stopping.Token).ConfigureAwait(false);
            await OverlayHostPipe.RunServerAsync(host, stopping.Cancel, stopping.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        catch { return 1; }
    }
}
