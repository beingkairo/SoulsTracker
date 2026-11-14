using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Net.Http;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{
    private bool isCheckingForUpdates;
    private bool isUpdateProgressVisible;
    private readonly object updateCheckGate = new();
    private readonly CancellationTokenSource updateCheckCancellation = new();
    private Task pendingUpdateCheck = Task.CompletedTask;
    private bool updateChecksStopped;
    private bool updateCheckCancellationDisposed;
    private bool startupUpdateCheckStarted;
    private bool startupUpdateCheckEnabled;
    private bool updateNoticeDismissed;
    private bool isUpdateNoticeVisible;
    private string updateCheckTone = "Idle";
    private string updatePreferenceStatus = string.Empty;
    private bool isSavingUpdatePreference;
    private bool pendingUpdatePreferenceValue;
    private string updatePageActionError = string.Empty;
    private bool hasCheckedForUpdates;
    private string? updateCurrentVersion;
    private string? updateLatestVersion;
    private string? updateCheckStatus;
    private Uri? availableUpdateReleasePage;

    public bool IsCheckingForUpdates { get => isCheckingForUpdates; private set => SetField(ref isCheckingForUpdates, value); }
    public bool HasCheckedForUpdates { get => hasCheckedForUpdates; private set => SetField(ref hasCheckedForUpdates, value); }
    public bool IsUpdateProgressVisible { get => isUpdateProgressVisible; private set => SetField(ref isUpdateProgressVisible, value); }
    public string? UpdateCurrentVersion { get => updateCurrentVersion; private set => SetField(ref updateCurrentVersion, value); }
    public string? UpdateLatestVersion { get => updateLatestVersion; private set => SetField(ref updateLatestVersion, value); }
    public string? UpdateCheckStatus { get => updateCheckStatus; private set => SetField(ref updateCheckStatus, value); }
    public Uri? AvailableUpdateReleasePage { get => availableUpdateReleasePage; private set { if (SetField(ref availableUpdateReleasePage, value)) OnPropertyChanged(nameof(CanOpenAvailableUpdateReleasePage)); } }
    // Manual update checks do not depend on tracker commands, including saving the startup preference.
    public bool CanCheckForUpdates => state is not null && !IsLoading && !updateChecksStopped;

    public bool CanOpenAvailableUpdateReleasePage => !updateChecksStopped && AvailableUpdateReleasePage is not null;

    public bool CheckForUpdatesOnStartup => state?.CheckForUpdatesOnStartup ?? false;
    public bool IsSavingUpdatePreference => isSavingUpdatePreference;
    public bool PendingUpdatePreferenceValue => pendingUpdatePreferenceValue;
    public bool CanChangeUpdatePreference => state is not null && !IsLoading && (!IsBusy || IsSavingUpdatePreference);
    public string UpdatePreferenceStatus { get => updatePreferenceStatus; private set => SetField(ref updatePreferenceStatus, value); }
    public string UpdatePageActionError { get => updatePageActionError; private set => SetField(ref updatePageActionError, value); }
    public string UpdateCheckTone { get => updateCheckTone; private set => SetField(ref updateCheckTone, value); }
    public bool IsUpdateNoticeVisible { get => isUpdateNoticeVisible; private set => SetField(ref isUpdateNoticeVisible, value); }
    public string UpdateNoticeText { get; private set; } = string.Empty;

    public async Task SetCheckForUpdatesOnStartupAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (IsSavingUpdatePreference || !ControlsEnabled) return;
        pendingUpdatePreferenceValue = enabled;
        isSavingUpdatePreference = true;
        OnPropertyChanged(nameof(CanChangeUpdatePreference));
        try
        {
            await SubmitAsync(new SetCheckForUpdatesOnStartupCommand(enabled), cancellationToken);
            UpdatePreferenceStatus = CheckForUpdatesOnStartup == enabled
                ? string.Empty
                : "The update setting could not be saved. Your previous choice is still active.";
            OnPropertyChanged(nameof(CheckForUpdatesOnStartup));
        }
        finally
        {
            isSavingUpdatePreference = false;
            OnPropertyChanged(nameof(CanChangeUpdatePreference));
        }
    }

    /// <summary>Called once after desktop startup; the owned check never delays readers or input.</summary>
    public void StartStartupUpdateCheck()
    {
        lock (updateCheckGate)
        {
            if (startupUpdateCheckStarted || state is null || updateChecksStopped) return;
            startupUpdateCheckStarted = true;
            if (startupUpdateCheckEnabled && !IsCheckingForUpdates && CanCheckForUpdates)
                pendingUpdateCheck = CheckForUpdatesCoreAsync(CancellationToken.None, isStartup: true);
        }
    }

    public Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        lock (updateCheckGate)
        {
            if (IsCheckingForUpdates) return pendingUpdateCheck;
            if (!CanCheckForUpdates) return Task.CompletedTask;
            return pendingUpdateCheck = CheckForUpdatesCoreAsync(cancellationToken);
        }
    }

    public async Task StopUpdateChecksAsync()
    {
        Task pending;
        lock (updateCheckGate)
        {
            if (!updateChecksStopped)
            {
                updateChecksStopped = true;
                updateCheckCancellation.Cancel();
            }
            pending = pendingUpdateCheck;
        }
        await pending;
        lock (updateCheckGate)
        {
            if (!updateCheckCancellationDisposed)
            {
                updateCheckCancellation.Dispose();
                updateCheckCancellationDisposed = true;
            }
        }
        IsUpdateNoticeVisible = false;
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanOpenAvailableUpdateReleasePage));
    }

    public ValueTask DisposeAsync() => new(StopUpdateChecksAsync());

    public void DismissUpdateNotice()
    {
        updateNoticeDismissed = true;
        IsUpdateNoticeVisible = false;
    }

    public string OpenUpdateProductPage()
    {
        if (!IsUpdateNoticeVisible || updateChecksStopped) return string.Empty;
        return TryOpenUpdateProductPage();
    }

    private string TryOpenUpdateProductPage() => updateReleasePageLauncher.TryOpen(ShellUpdateReleasePageLauncher.ProductPage)
        ? string.Empty : "The product page could not be opened. Try again.";

    private async Task CheckForUpdatesCoreAsync(CancellationToken cancellationToken, bool isStartup = false)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, updateCheckCancellation.Token);
        cancellationToken = linkedCancellation.Token;
        IsCheckingForUpdates = true;

        string installedVersion = NormalizeProductVersion(installedVersionProvider());
        using var presentationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task presentationDelay = Task.CompletedTask;
        try
        {
            Task<ManualReleaseUpdateResult> request = manualReleaseUpdateChecker.CheckAsync(installedVersion, cancellationToken).AsTask();
            presentationDelay = Task.Delay(TimeSpan.FromMilliseconds(300), timeProvider, presentationCancellation.Token);
            await Task.WhenAny(request, presentationDelay);
            // The request owns this continuation. A completed or cancelled request never discloses progress.
            if (!request.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                IsUpdateProgressVisible = true;
                UpdateCheckStatus = "Checking for updates…";
            }
            ManualReleaseUpdateResult result = await request;
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCurrentVersion = installedVersion;
            HasCheckedForUpdates = true;

            UpdateCheckTone = result.Status switch
            {
                ManualReleaseUpdateStatus.UpToDate => "Current",
                ManualReleaseUpdateStatus.UpdateAvailable => "Available",
                _ => "Error",
            };
            switch (result.Status)
            {
                case ManualReleaseUpdateStatus.UpToDate: UpdateLatestVersion = result.AvailableVersion ?? UpdateCurrentVersion; UpdateCheckStatus = "All up to date."; AvailableUpdateReleasePage = null; break;
                case ManualReleaseUpdateStatus.UpdateAvailable: UpdateLatestVersion = result.AvailableVersion ?? "Unavailable"; UpdateCheckStatus = "New version out!"; AvailableUpdateReleasePage = ShellUpdateReleasePageLauncher.ProductPage; break;
                case ManualReleaseUpdateStatus.RateLimited: SetRetryableUpdateFailure("GitHub asked you to try again later."); break;
                case ManualReleaseUpdateStatus.InvalidResponse or ManualReleaseUpdateStatus.InvalidInstalledVersion: SetRetryableUpdateFailure("Update information could not be verified. Try again or open the official Releases page."); break;
                default: SetRetryableUpdateFailure("Couldn’t reach GitHub right now. Check your connection and try again."); break;
            }
            if (isStartup && result.Status == ManualReleaseUpdateStatus.UpdateAvailable && !updateNoticeDismissed)
            {
                UpdateNoticeText = $"SoulsTracker {UpdateLatestVersion} is available.\nYou're currently using {UpdateCurrentVersion}.";
                IsUpdateNoticeVisible = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { SetRetryableUpdateFailure("Update check cancelled. Try again when you’re ready."); }
        catch (Exception) { SetRetryableUpdateFailure("Couldn’t reach GitHub right now. Check your connection and try again."); }
        finally
        {
            presentationCancellation.Cancel();
            try { await presentationDelay; }
            catch (OperationCanceledException) when (presentationCancellation.IsCancellationRequested) { }
            IsUpdateProgressVisible = false;
            IsCheckingForUpdates = false;
        }
    }

    public void OpenAvailableUpdateReleasePage()
    {
        if (!CanOpenAvailableUpdateReleasePage) return;
        UpdatePageActionError = TryOpenUpdateProductPage();
    }

    private void SetRetryableUpdateFailure(string status)
    {
        UpdateCheckTone = "Error";

        AvailableUpdateReleasePage = ShellUpdateReleasePageLauncher.ProductPage;
        UpdateLatestVersion = "Unavailable";
        UpdateCheckStatus = status;
    }

    private static string CurrentInstalledVersion() => typeof(DesktopTrackerViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static string NormalizeProductVersion(string version) =>
        ReleaseSemanticVersion.TryParse(version, out ReleaseSemanticVersion? parsed)
            ? parsed!.ToString()
            : version;
}
