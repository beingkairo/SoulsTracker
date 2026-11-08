using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{
    private string? verifiedHollowKnightDirectory;
    private Guid? verifiedHollowKnightInstallation;
    private bool hollowKnightDirectoryWasAutomatic;
    private long hollowKnightInspection;
    public bool IsHollowKnightInstalled => IsHollowKnightSelected && verifiedHollowKnightInstallation is not null &&
        verifiedHollowKnightInstallation == state?.HollowKnight.InstallationId && PathsEqual(verifiedHollowKnightDirectory, HollowKnightGameDirectory);
    public bool IsHollowKnightInstallVisible => !IsHollowKnightInstalled;
    public bool IsDeathTrackerVisible => !IsHollowKnightSelected || IsHollowKnightInstalled;
    public bool HollowKnightDirectoryWasAutomatic => hollowKnightDirectoryWasAutomatic;
    public string HollowKnightDirectoryPath => HollowKnightGameDirectory ?? "No directory selected";
    public bool CanAdjustHollowKnightDeaths => ControlsEnabled && IsHollowKnightInstalled && CurrentHollowKnightObservation is not null;
    public bool CanIncrementHollowKnightDeaths => CanAdjustHollowKnightDeaths && CurrentHollowKnightObservation!.Total < long.MaxValue - HollowKnightAddedDeaths;
    public bool CanDecrementHollowKnightDeaths => CanAdjustHollowKnightDeaths && HollowKnightAddedDeaths > 0;
    public long HollowKnightAddedDeaths => CurrentHollowKnightObservation is { } value && state?.HollowKnight.InstallationId is { } id
        ? state.HollowKnight.DeathAdjustments.Get(id, value.RunId) : 0;
    private PersistentPluginObservation? CurrentHollowKnightObservation => IsHollowKnightSelected && state is not null && pluginObservation is { } value &&
        EffectiveDeathTotalResult.Resolve(state, null, value).Status != EffectiveDeathTotalStatus.SourceMismatch ? value : null;

    public Task IncrementHollowKnightDeathsAsync(CancellationToken cancellationToken = default) => ChangeHollowKnightDeathsAsync(true, cancellationToken);
    public Task DecrementHollowKnightDeathsAsync(CancellationToken cancellationToken = default) => ChangeHollowKnightDeathsAsync(false, cancellationToken);
    private Task ChangeHollowKnightDeathsAsync(bool increment, CancellationToken token)
    {
        if (!(increment ? CanIncrementHollowKnightDeaths : CanDecrementHollowKnightDeaths) || CurrentHollowKnightObservation is not { } value || state?.HollowKnight.InstallationId is not { } installation)
            return Task.CompletedTask;
        return SubmitAsync(new AdjustHollowKnightDeathsCommand(installation, value.RunId, increment), token);
    }

    public bool IsHollowKnightSelected => state?.SelectedGameId == GameId.HollowKnight;
    public string? HollowKnightGameDirectory => state?.HollowKnight.GameDirectory;
    public string HollowKnightSetupStatus { get; private set; } = "Not installed";
    public bool HasHollowKnightPendingRollback
    {
        get
        {
            try { return HollowKnightGameDirectory is { } root && HollowKnightPluginInstaller.HasPendingRollback(root); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
        }
    }

    public async Task DiscoverHollowKnightAsync()
    {
        if (!IsHollowKnightSelected || IsBusy) return;
        long version = ++hollowKnightInspection;
        string? chosen = HollowKnightGameDirectory;
        if (chosen is null)
        {
            string? discovered = await Task.Run(HollowKnightInstallDiscovery.Discover);
            if (version != hollowKnightInspection || !IsHollowKnightSelected || HollowKnightGameDirectory is not null || discovered is null) return;
            await SetHollowKnightGameDirectoryAsync(discovered);
            hollowKnightDirectoryWasAutomatic = true;
            NotifyHollowKnight();
            return;
        }
        var inspected = await InspectHollowKnightAsync(chosen);
        if (version != hollowKnightInspection || !IsHollowKnightSelected || !PathsEqual(chosen, HollowKnightGameDirectory)) return;
        if (inspected.Id.HasValue && inspected.Id != state?.HollowKnight.InstallationId)
            await SubmitAsync(new UpdateHollowKnightConfigurationCommand(new(chosen, inspected.Id)), CancellationToken.None);
        ApplyHollowKnightInspection(chosen, inspected.Game, inspected.Id);
    }

    private static async Task<(HollowKnightInstallation? Game, Guid? Id)> InspectHollowKnightAsync(string directory) => await Task.Run(() =>
    {
        HollowKnightInstallation? game = HollowKnightInstallDiscovery.Inspect(directory);
        Guid? id = null;
        try { if (game is not null && !HollowKnightPluginInstaller.HasPendingRollback(directory)) id = HollowKnightPluginInstaller.GetInstalledIdentity(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return (game, id);
    });

    private void ApplyHollowKnightInspection(string directory, HollowKnightInstallation? game, Guid? id)
    {
        verifiedHollowKnightDirectory = directory;
        verifiedHollowKnightInstallation = game is { Supported: true } ? id : null;
        HollowKnightSetupStatus = game is null ? "Tracking unavailable" : !game.Supported ? "Unsupported version" : id is not null ? "Installed - restart game" : "Not installed";
        NotifyHollowKnight();
    }

    public async Task SetHollowKnightGameDirectoryAsync(string directory)
    {
        if (IsBusy) return;
        long version = ++hollowKnightInspection;
        var inspected = await InspectHollowKnightAsync(directory);
        if (version != hollowKnightInspection || !IsHollowKnightSelected) return;
        if (inspected.Game is not { } game) { ErrorMessage = "Choose a Hollow Knight folder containing hollow_knight.exe and valid game data."; return; }
        Guid? id = inspected.Id;
        if (id is null && PathsEqual(game.Directory, HollowKnightGameDirectory)) id = state?.HollowKnight.InstallationId;
        await SubmitAsync(new UpdateHollowKnightConfigurationCommand(new(game.Directory, id)), CancellationToken.None);
        hollowKnightDirectoryWasAutomatic = false;
        ApplyHollowKnightInspection(game.Directory, game, inspected.Id);
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
        catch (System.Text.Json.JsonException)
        { ErrorMessage = "Tracking installation recovery data could not be read. Existing files were preserved."; return null; }
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
        catch (System.Text.Json.JsonException)
        { ErrorMessage = "Tracking installation recovery data could not be read. Existing files were preserved."; HollowKnightSetupStatus = "Tracking unavailable"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ErrorMessage = exception.Message; HollowKnightSetupStatus = "Tracking unavailable"; }
        finally { IsBusy = false; }
        await DiscoverHollowKnightAsync();
        NotifyHollowKnight();
    }

    private void NotifyHollowKnight()
    {
        OnPropertyChanged(nameof(IsHollowKnightSelected));
        OnPropertyChanged(nameof(IsHollowKnightInstalled));
        OnPropertyChanged(nameof(IsHollowKnightInstallVisible));
        OnPropertyChanged(nameof(IsDeathTrackerVisible));
        OnPropertyChanged(nameof(HollowKnightDirectoryPath));
        OnPropertyChanged(nameof(HollowKnightDirectoryWasAutomatic));
        OnPropertyChanged(nameof(CanAdjustHollowKnightDeaths));
        OnPropertyChanged(nameof(CanIncrementHollowKnightDeaths));
        OnPropertyChanged(nameof(CanDecrementHollowKnightDeaths));
        OnPropertyChanged(nameof(HollowKnightAddedDeaths));
        OnPropertyChanged(nameof(HollowKnightGameDirectory));
        OnPropertyChanged(nameof(HollowKnightSetupStatus));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }
}
