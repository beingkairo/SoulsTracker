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
    private bool hollowKnightManualSelectionPending;
    private bool hollowKnightInspectionPending;
    private long? hollowKnightHistoricalTotal;
    internal Func<Task<string?>> HollowKnightDirectoryDiscovery { get; set; } = () => Task.Run(HollowKnightInstallDiscovery.Discover);
    internal Func<string, Task<(HollowKnightInstallation? Game, Guid? Id)>> HollowKnightDirectoryInspector { get; set; } = InspectHollowKnightAsync;
    public bool IsHollowKnightInstalled => IsHollowKnightSelected && verifiedHollowKnightInstallation is not null &&
        verifiedHollowKnightInstallation == state?.HollowKnight.InstallationId && PathsEqual(verifiedHollowKnightDirectory, HollowKnightGameDirectory);
    public bool IsHollowKnightInstallVisible => !IsHollowKnightInstalled;
    public bool HollowKnightDirectoryWasAutomatic => hollowKnightDirectoryWasAutomatic;
    public string HollowKnightDirectoryPath => HollowKnightGameDirectory ?? "No directory selected";
    public string HollowKnightDirectoryHeading => HollowKnightGameDirectory is null ? "Game Directory" : "Chosen Directory";
    public bool CanAdjustHollowKnightDeaths => ControlsEnabled && IsHollowKnightInstalled && CurrentHollowKnightObservation is not null;
    public bool CanIncrementHollowKnightDeaths => CanAdjustHollowKnightDeaths && CurrentHollowKnightObservation!.Total < long.MaxValue - HollowKnightAddedDeaths;
    public bool CanDecrementHollowKnightDeaths => CanAdjustHollowKnightDeaths && HollowKnightAddedDeaths > 0;
    public long HollowKnightAddedDeaths => CurrentHollowKnightObservation is { } value && state?.HollowKnight.InstallationId is { } id
        ? state.HollowKnight.DeathAdjustments.Get(id, value.RunId) : 0;
    public string HollowKnightSavedDeathsText => CurrentHollowKnightObservation?.Total.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable";
    private PersistentPluginObservation? CurrentHollowKnightObservation => IsHollowKnightSelected && state is not null && pluginObservation is { } value &&
        EffectiveDeathTotalResult.Resolve(state, null, value).Status != EffectiveDeathTotalStatus.SourceMismatch ? value : null;

    public Task IncrementHollowKnightDeathsAsync(CancellationToken cancellationToken = default) => ChangeHollowKnightDeathsAsync(true, cancellationToken);
    public Task DecrementHollowKnightDeathsAsync(CancellationToken cancellationToken = default) => ChangeHollowKnightDeathsAsync(false, cancellationToken);
    private Task ChangeHollowKnightDeathsAsync(bool increment, CancellationToken token)
    {
        if (!(increment ? CanIncrementHollowKnightDeaths : CanDecrementHollowKnightDeaths) || CurrentHollowKnightObservation is not { } value || state?.HollowKnight.InstallationId is not { } installation)
            return Task.CompletedTask;
        return SubmitCounterAsync(new AdjustHollowKnightDeathsCommand(installation, value.RunId, increment),
            () => HollowKnightAddedDeaths, nameof(HollowKnightAddedDeaths),
            () => CanDecrementHollowKnightDeaths, nameof(CanDecrementHollowKnightDeaths), token,
            () => CanIncrementHollowKnightDeaths, nameof(CanIncrementHollowKnightDeaths));
    }

    public bool IsHollowKnightSelected => state?.SelectedGameId == GameId.HollowKnight;
    public bool IsHollowKnightTrackingVerifiedUninstalled => IsHollowKnightSelected
        && state?.HollowKnight.GameDirectory is { } directory
        && PathsEqual(verifiedHollowKnightDirectory, directory)
        && verifiedHollowKnightInstallation is null
        && HollowKnightSetupStatus == "Not installed"
        && ErrorMessage is null
        && HollowKnightDirectoryFeedback is null
        && !HasHollowKnightPendingRollback;
    public bool IsHollowKnightHistoricalTotalVisible => IsHollowKnightTrackingVerifiedUninstalled && hollowKnightHistoricalTotal.HasValue;
    public string? HollowKnightGameDirectory => state?.HollowKnight.GameDirectory;
    public string HollowKnightSetupStatus { get; private set; } = "Not installed";
    public string? HollowKnightDirectoryFeedback { get; private set; }
    private string HollowKnightReaderStatusText
    {
        get
        {
            if (hollowKnightInspectionPending)
                return HollowKnightGameDirectory is null ? "Looking for Hollow Knight..." : "Checking the Hollow Knight game folder...";
            if (!string.IsNullOrWhiteSpace(HollowKnightDirectoryFeedback)) return HollowKnightDirectoryFeedback;
            if (!string.IsNullOrWhiteSpace(ErrorMessage)) return FriendlyHollowKnightError(ErrorMessage);
            if (HasHollowKnightPendingRollback)
                return "A previous tracking change needs attention. Existing game files are preserved.";
            if (IsHollowKnightTrackingVerifiedUninstalled) return "Death tracking is not installed.";
            bool hasRecordedTotal = state is not null
                && EffectiveDeathTotalResult.Resolve(state, null, pluginObservation).EffectiveDisplayedTotal.HasValue;
            if (IsHollowKnightInstalled && hasRecordedTotal) return GameSyncedMessage;
            if (HollowKnightGameDirectory is null)
                return "Hollow Knight wasn't found automatically. Click Choose Directory to select the game folder.";
            if (HollowKnightSetupStatus == "Unsupported version") return "This Hollow Knight version isn't supported.";
            if (HollowKnightSetupStatus == "Tracking unavailable") return "Death tracking isn't available. Check the selected game folder.";
            if (PathsEqual(verifiedHollowKnightDirectory, HollowKnightGameDirectory) && !IsHollowKnightInstalled)
                return "Death tracking is not installed.";
            return GameUnavailableMessage;
        }
    }
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
        if (!IsHollowKnightSelected || IsBusy || hollowKnightManualSelectionPending) return;
        long version = ++hollowKnightInspection;
        string? chosen = HollowKnightGameDirectory;
        SetHollowKnightInspectionPending(true);
        HollowKnightDirectoryFeedback = null;
        try
        {
            if (chosen is null)
            {
                string? discovered = await HollowKnightDirectoryDiscovery();
                if (version != hollowKnightInspection || !IsHollowKnightSelected || HollowKnightGameDirectory is not null) return;
                if (discovered is null)
                {
                    HollowKnightDirectoryFeedback = "Hollow Knight wasn't found automatically. Click Choose Directory to select the game folder.";
                    NotifyHollowKnight();
                    return;
                }
                bool configured = await SetHollowKnightGameDirectoryAsync(discovered);
                if (!configured) return;
                hollowKnightDirectoryWasAutomatic = true;
                NotifyHollowKnight();
                return;
            }
            var inspected = await HollowKnightDirectoryInspector(chosen);
            if (version != hollowKnightInspection || !IsHollowKnightSelected || !PathsEqual(chosen, HollowKnightGameDirectory)) return;
            if (inspected.Id.HasValue && inspected.Id != state?.HollowKnight.InstallationId)
                await SubmitAsync(new UpdateHollowKnightConfigurationCommand(new(chosen, inspected.Id)), CancellationToken.None);
            HollowKnightDirectoryFeedback = inspected.Game is null
                ? "Hollow Knight's game folder couldn't be found. Click Change Directory to select it again."
                : null;
            ApplyHollowKnightInspection(chosen, inspected.Game, inspected.Id);
        }
        finally
        {
            if (version == hollowKnightInspection) SetHollowKnightInspectionPending(false);
        }
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
        bool sameDirectory = PathsEqual(verifiedHollowKnightDirectory, directory);
        if (!sameDirectory || id is not null) hollowKnightHistoricalTotal = null;
        verifiedHollowKnightDirectory = directory;
        verifiedHollowKnightInstallation = game is { Supported: true } ? id : null;
        HollowKnightSetupStatus = game is null ? "Tracking unavailable" : !game.Supported ? "Unsupported version" : id is not null ? "Installed - restart game" : "Not installed";
        if (game is { Supported: true } && id is null && ErrorMessage is null && !HasHollowKnightPendingRollback)
        {
            CaptureHollowKnightHistoricalTotal(pluginObservation);
            pluginObservation = null;
            pluginStatus = null;
        }
        UpdateTotalDeathsText();
        NotifyHollowKnight();
    }

    private void CaptureHollowKnightHistoricalTotal(PersistentPluginObservation? observation)
    {
        if (hollowKnightHistoricalTotal is not null || state is null || observation is null) return;
        EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(state, null, observation);
        if (result.Status != EffectiveDeathTotalStatus.SourceMismatch)
            hollowKnightHistoricalTotal = result.EffectiveDisplayedTotal;
    }

    public async Task<bool> SetHollowKnightGameDirectoryAsync(string directory)
    {
        if (IsBusy) return false;
        long version = ++hollowKnightInspection;
        hollowKnightManualSelectionPending = true;
        SetHollowKnightInspectionPending(true);
        HollowKnightDirectoryFeedback = null;
        ErrorMessage = null;
        try
        {
            var inspected = await HollowKnightDirectoryInspector(directory);
            if (version != hollowKnightInspection || !IsHollowKnightSelected) return false;
            if (inspected.Game is not { } game) { HollowKnightDirectoryFeedback = "That folder couldn't be selected. Choose the Hollow Knight game folder and try again."; NotifyHollowKnight(); return false; }
            Guid? id = inspected.Id;
            if (id is null && PathsEqual(game.Directory, HollowKnightGameDirectory)) id = state?.HollowKnight.InstallationId;
            await SubmitAsync(new UpdateHollowKnightConfigurationCommand(new(game.Directory, id)), CancellationToken.None);
            if (ErrorMessage is not null || !PathsEqual(game.Directory, HollowKnightGameDirectory)) { HollowKnightDirectoryFeedback = "The selected folder couldn't be saved. Please try again."; NotifyHollowKnight(); return false; }
            hollowKnightDirectoryWasAutomatic = false;
            HollowKnightDirectoryFeedback = null;
            ApplyHollowKnightInspection(game.Directory, game, inspected.Id);
            return true;
        }
        finally
        {
            if (version == hollowKnightInspection)
            {
                hollowKnightManualSelectionPending = false;
                SetHollowKnightInspectionPending(false);
            }
        }
    }

    public async Task<HollowKnightInstallPlan?> PrepareHollowKnightInstallAsync()
    {
        if (IsBusy) return null;
        if (HollowKnightGameDirectory is not { } directory) { HollowKnightDirectoryFeedback = "Choose the Hollow Knight game folder before installing death tracking."; NotifyHollowKnight(); return null; }
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
        OnPropertyChanged(nameof(IsHollowKnightTrackingVerifiedUninstalled));
        OnPropertyChanged(nameof(IsHollowKnightHistoricalTotalVisible));
        OnPropertyChanged(nameof(HollowKnightDirectoryHeading));
        OnPropertyChanged(nameof(HollowKnightDirectoryPath));
        OnPropertyChanged(nameof(HollowKnightDirectoryWasAutomatic));
        OnPropertyChanged(nameof(CanAdjustHollowKnightDeaths));
        OnPropertyChanged(nameof(CanIncrementHollowKnightDeaths));
        OnPropertyChanged(nameof(CanDecrementHollowKnightDeaths));
        OnPropertyChanged(nameof(HollowKnightAddedDeaths));
        OnPropertyChanged(nameof(HollowKnightSavedDeathsText));
        OnPropertyChanged(nameof(HollowKnightGameDirectory));
        OnPropertyChanged(nameof(HollowKnightSetupStatus));
        OnPropertyChanged(nameof(HollowKnightDirectoryFeedback));
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private void SetHollowKnightInspectionPending(bool value)
    {
        if (hollowKnightInspectionPending == value) return;
        hollowKnightInspectionPending = value;
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    private static string FriendlyHollowKnightError(string error)
    {
        if (error.Contains("Close Hollow Knight", StringComparison.OrdinalIgnoreCase))
            return "Close Hollow Knight before installing or changing death tracking.";
        if (error.Contains("Unsupported Hollow Knight version", StringComparison.OrdinalIgnoreCase)
            || error.Contains("incompatible", StringComparison.OrdinalIgnoreCase)
            || error.Contains("do not match the verified clean", StringComparison.OrdinalIgnoreCase))
            return "This Hollow Knight installation isn't compatible with death tracking.";
        if (error.Contains("previous install", StringComparison.OrdinalIgnoreCase)
            || error.Contains("recovery", StringComparison.OrdinalIgnoreCase)
            || error.Contains("journal", StringComparison.OrdinalIgnoreCase))
            return "A previous tracking change needs attention before continuing. Existing game files are preserved.";
        if (error.Contains("API", StringComparison.OrdinalIgnoreCase)
            || error.Contains("verification", StringComparison.OrdinalIgnoreCase)
            || error.Contains("component", StringComparison.OrdinalIgnoreCase))
            return "The tracking setup files couldn't be checked. No game files were changed.";
        if (error.Contains("Invalid Hollow Knight game folder", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invalid game folder", StringComparison.OrdinalIgnoreCase))
            return "That folder couldn't be used. Choose the Hollow Knight game folder and try again.";
        return "Hollow Knight setup couldn't be completed. Check the game folder and try again.";
    }
}
