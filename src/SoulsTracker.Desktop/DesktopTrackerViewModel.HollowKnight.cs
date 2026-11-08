using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{
    public bool IsHollowKnightSelected => state?.SelectedGameId == GameId.HollowKnight;
    public string? HollowKnightGameDirectory => state?.HollowKnight.GameDirectory;
    public string HollowKnightSetupStatus { get; private set; } = "Not installed";

    public async Task DiscoverHollowKnightAsync()
    {
        if (!IsHollowKnightSelected || IsBusy) return;
        if (HollowKnightGameDirectory is { } chosen)
        {
            HollowKnightInstallation? game = await Task.Run(() => HollowKnightInstallDiscovery.Inspect(chosen));
            Guid? installed = null;
            try { if (game is not null) installed = HollowKnightPluginInstaller.GetInstalledIdentity(chosen); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (installed.HasValue && installed != state?.HollowKnight.InstallationId)
                await SubmitAsync(new UpdateHollowKnightConfigurationCommand(new(chosen, installed)), CancellationToken.None);
            HollowKnightSetupStatus = game is null ? "Tracking unavailable" : !game.Supported ? "Unsupported version" : installed is not null ? "Installed - restart game" :
                Directory.Exists(Path.Combine(chosen, HollowKnightPluginInstaller.PluginFolder)) ? "Tracking unavailable" : "Not installed";
            NotifyHollowKnight();
            return;
        }
        string? directory = await Task.Run(HollowKnightInstallDiscovery.Discover);
        if (IsHollowKnightSelected && HollowKnightGameDirectory is null && directory is not null)
            await SetHollowKnightGameDirectoryAsync(directory);
    }

    public async Task SetHollowKnightGameDirectoryAsync(string directory)
    {
        if (IsBusy) return;
        HollowKnightInstallation? game = await Task.Run(() => HollowKnightInstallDiscovery.Inspect(directory));
        if (game is null) { ErrorMessage = "Choose a Hollow Knight folder containing hollow_knight.exe and valid game data."; return; }
        Guid? id = null;
        try { id = HollowKnightPluginInstaller.GetInstalledIdentity(game.Directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        bool installed = id.HasValue;
        if (id is null && PathsEqual(game.Directory, HollowKnightGameDirectory)) id = state?.HollowKnight.InstallationId;
        await SubmitAsync(new UpdateHollowKnightConfigurationCommand(new(game.Directory, id)), CancellationToken.None);
        HollowKnightSetupStatus = !game.Supported ? "Unsupported version" : installed ? "Installed - restart game" :
            Directory.Exists(Path.Combine(game.Directory, HollowKnightPluginInstaller.PluginFolder)) ? "Tracking unavailable" : "Not installed";
        NotifyHollowKnight();
    }

    public async Task<HollowKnightInstallPlan?> PrepareHollowKnightInstallAsync()
    {
        if (IsBusy || HollowKnightGameDirectory is not { } directory) return null;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            return await Task.Run(async () =>
            {
                HollowKnightInstallation game = HollowKnightInstallDiscovery.Inspect(directory) ?? throw new IOException("Invalid game folder.");
                if (!game.Supported) throw new IOException("Unsupported Hollow Knight version.");
                if (HollowKnightInstallDiscovery.IsGameRunning()) throw new IOException("Close Hollow Knight before installing death tracking.");
                if (game.HasApi && !HollowKnightPluginInstaller.HasCompatibleApi(directory)) throw new IOException("The installed Modding API is incompatible; it will not be replaced.");
                var installer = new HollowKnightPluginInstaller();
                byte[]? api = HollowKnightPluginInstaller.HasCompatibleApi(directory) ? null : game.Version == "1.5.12620"
                    ? HollowKnightPluginInstaller.ReadVerifiedModernApiBundle()
                    : await HollowKnightPluginInstaller.DownloadOfficialApiAsync(CancellationToken.None);
                byte[] plugin = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "HollowKnight", HollowKnightPluginInstaller.PluginName));
                return installer.Prepare(directory, plugin, api, state?.HollowKnight.InstallationId);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or TaskCanceledException)
        { ErrorMessage = exception.Message; return null; }
        finally { IsBusy = false; NotifyHollowKnight(); }
    }

    // The window must obtain confirmation for this exact prepared plan before calling Apply.
    public async Task ApplyHollowKnightInstallAsync(HollowKnightInstallPlan plan)
    {
        if (IsBusy || HollowKnightGameDirectory != plan.Directory) return;
        await ChangeHollowKnightInstallationAsync(() => new HollowKnightPluginInstaller().Apply(plan));
        if (ErrorMessage is null)
            await SubmitAsync(new UpdateHollowKnightConfigurationCommand(new(plan.Directory, plan.InstallationId)), CancellationToken.None);
    }

    public async Task UninstallHollowKnightAsync()
    {
        await ChangeHollowKnightInstallationAsync(() => new HollowKnightPluginInstaller().Uninstall(HollowKnightGameDirectory!));
        if (ErrorMessage is null) { HollowKnightSetupStatus = "Not installed"; NotifyHollowKnight(); }
    }
    public Task RollbackHollowKnightAsync() => ChangeHollowKnightInstallationAsync(() => new HollowKnightPluginInstaller().RollbackPending(HollowKnightGameDirectory!));

    private async Task ChangeHollowKnightInstallationAsync(Action action)
    {
        if (IsBusy || HollowKnightGameDirectory is null) return;
        IsBusy = true; ErrorMessage = null;
        try { await Task.Run(action); HollowKnightSetupStatus = "Installed - restart game"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ErrorMessage = exception.Message; HollowKnightSetupStatus = "Tracking unavailable"; }
        finally { IsBusy = false; NotifyHollowKnight(); }
    }

    private void NotifyHollowKnight()
    {
        OnPropertyChanged(nameof(IsHollowKnightSelected));
        OnPropertyChanged(nameof(HollowKnightGameDirectory));
        OnPropertyChanged(nameof(HollowKnightSetupStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }
}
