using System.ComponentModel;
using System.Security.Cryptography;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Dispatcher-owned hosted publisher and first-use provisioning lifecycle.</summary>
public sealed class HostedOverlayConnection : INotifyPropertyChanged, ITrackerStateChangePublisher, IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly HostedPublisherConfigurationStore store;
    private readonly HostedProvisioningStateStore? pendingStore;
    private HostedOverlayProvisioningClient? provisioningClient;
    private readonly Func<HostedOverlayProvisioningClient>? createProvisioningClient;
    private readonly HostedOverlayPreferenceStore? preferenceStore;
    private readonly Func<HostedPublisherConfiguration, HostedOverlayPublisher> createSender;
    private readonly Action<HostedDesktopPublisher, HostedOverlayPublisher> attachSender;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private CancellationTokenSource stop = new();
    private HostedPublisherConfiguration? configuration, persistedConfiguration;
    private HostedProvisioningState? pending;
    private HostedDesktopPublisher? adapter;
    private HostedOverlayPublisher? sender;
    private Task? operation, disposal;
    private bool closing, busy, setupStopped, canRetry, provisioningRequested, enabled, stopping;
    private string statusText = string.Empty, copyFeedbackText = string.Empty;

    internal HostedOverlayConnection(Dispatcher dispatcher, HostedPublisherConfigurationStore store,
        Func<HostedPublisherConfiguration, HostedOverlayPublisher>? createSender = null,
        HostedProvisioningStateStore? pendingStore = null,
        HostedOverlayProvisioningClient? provisioningClient = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Action<HostedDesktopPublisher, HostedOverlayPublisher>? attachSender = null,
        HostedOverlayPreferenceStore? preferenceStore = null, bool initiallyEnabled = true,
        Func<HostedOverlayProvisioningClient>? createProvisioningClient = null)
    {
        this.dispatcher = dispatcher; this.store = store; this.pendingStore = pendingStore;
        this.provisioningClient = provisioningClient;
        this.createProvisioningClient = createProvisioningClient;
        this.preferenceStore = preferenceStore;
        enabled = initiallyEnabled;
        this.createSender = createSender ?? (config => new HostedOverlayPublisher(config));
        this.retryDelay = retryDelay ?? Task.Delay;
        this.attachSender = attachSender ?? ((adapter, next) => adapter.Attach(next));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string UrlText => configuration?.BuildReadUrl() ?? (busy ? "Preparing overlay URL..." : string.Empty);
    public string StatusText => statusText;
    public string CopyFeedbackText => copyFeedbackText;
    public bool CanCopy => adapter is not null && !closing && !setupStopped && !busy && configuration is not null;
    public bool CanRetry => enabled && canRetry && adapter is not null && !closing && !setupStopped && !busy && configuration is null;
    public bool IsPreparing => busy && configuration is null;
    public bool IsEnabled => enabled;
    public bool CanChangeEnabled => adapter is not null && !closing && !setupStopped && (!busy || enabled) && !stopping;

    private void Changed()
    {
        dispatcher.VerifyAccess();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    internal Task InitializeAsync(PersistentTrackerState initial)
    {
        dispatcher.VerifyAccess();
        if (adapter is not null || closing) return Task.CompletedTask;
        adapter = new HostedDesktopPublisher(null, initial);
        if (!enabled)
        {
            busy = true;
            Changed();
            return LoadDisabledIdentityAsync();
        }
        return RunOperationAsync(async () =>
        {
            persistedConfiguration = await store.LoadAsync(stop.Token);
            provisioningClient ??= createProvisioningClient?.Invoke();
            if (persistedConfiguration is not null)
            {
                try
                {
                    await ConnectAsync(persistedConfiguration);
                    configuration = persistedConfiguration;
                }
                finally
                {
                    if (pendingStore is not null) pending = await pendingStore.LoadPendingAsync(stop.Token);
                }
                if (pendingStore is not null && pending?.Matches(configuration) == true)
                { await pendingStore.RemovePendingAsync(stop.Token); pending = null; }
                return;
            }
            if (pendingStore is null) return;
            pending = await pendingStore.LoadPendingAsync(stop.Token);
            if (pending is not null || provisioningRequested)
                await RecoverOrProvisionAsync(allowNewRequest: provisioningRequested);
            else if (preferenceStore is not null)
            {
                canRetry = true;
                statusText = "No saved Hosted Overlay pairing was found. Choose Try again to prepare a new URL.";
            }
        }, showFailure: true);
    }

    private async Task LoadDisabledIdentityAsync()
    {
        try
        {
            configuration = persistedConfiguration = await store.LoadAsync(stop.Token);
            statusText = "Hosted Overlay is off. Existing shared URLs may still display previously published content.";
        }
        catch { statusText = "Hosted Overlay is off. The saved URL could not be read; local tracking is available."; }
        finally { busy = false; Changed(); }
    }

    internal Task EnsureProvisionedAsync()
    {
        dispatcher.VerifyAccess();
        if (!enabled) return Task.CompletedTask;
        provisioningRequested = true;
        bool canRecoverSavedPromotion = persistedConfiguration is not null && pending?.Matches(persistedConfiguration) == true;
        if (configuration is not null || (persistedConfiguration is not null && !canRecoverSavedPromotion) ||
            pendingStore is null || provisioningClient is null || closing || setupStopped)
            return Task.CompletedTask;
        if (adapter is null || busy) return operation ?? Task.CompletedTask;
        return RunOperationAsync(() => RecoverOrProvisionAsync(allowNewRequest: true), showFailure: true);
    }

    internal Task RetryAsync() => !CanRetry ? Task.CompletedTask : EnsureProvisionedAsync();

    public async Task SetEnabledAsync(bool next)
    {
        dispatcher.VerifyAccess();
        if (preferenceStore is null || closing || setupStopped || stopping || (next && busy) || enabled == next) return;
        if (!next)
        {
            // Fence synchronous callers before the durable save or any await.
            enabled = false;
            stopping = true;
            canRetry = false;
            adapter?.Attach(null);
            stop.Cancel();
            HostedOverlayPublisher? previous = sender;
            sender = null;
            if (previous is not null) previous.StatusChanged -= Sender_StatusChanged;
            Task stoppedSender = previous?.CancelAsync().AsTask() ?? Task.CompletedTask;
            statusText = "Stopping Hosted Overlay...";
            Changed();
            bool saved = true;
            try { await preferenceStore.SaveAsync(false); }
            catch { saved = false; }
            if (operation is not null) await operation;
            await stoppedSender;
            stopping = false;
            statusText = saved
                ? "Hosted Overlay is off. Previously published content and shared URLs remain available online."
                : "Could not save the off setting. Hosted Overlay is stopped for this session; it may restart next launch.";
            Changed();
            return;
        }
        try { await preferenceStore.SaveAsync(true); }
        catch { statusText = "Could not save the Hosted Overlay setting. Try again."; Changed(); return; }
        stop.Dispose(); stop = new CancellationTokenSource();
        enabled = true;
        configuration = null;
        persistedConfiguration = null;
        pending = null;
        provisioningRequested = true;
        canRetry = false;
        statusText = "Connecting to Hosted Overlay...";
        Changed();
        await RunOperationAsync(async () =>
        {
            provisioningClient ??= createProvisioningClient?.Invoke();
            persistedConfiguration = await store.LoadAsync(stop.Token);
            if (persistedConfiguration is not null)
            {
                if (pendingStore is not null) pending = await pendingStore.LoadPendingAsync(stop.Token);
                await ConnectAsync(persistedConfiguration);
                configuration = persistedConfiguration;
                if (pending?.Matches(configuration) == true)
                { await pendingStore!.RemovePendingAsync(stop.Token); pending = null; }
            }
            else if (pendingStore is not null)
            {
                pending = await pendingStore.LoadPendingAsync(stop.Token);
                await RecoverOrProvisionAsync(allowNewRequest: true);
            }

        }, showFailure: true);
    }

    private async Task RecoverOrProvisionAsync(bool allowNewRequest)
    {
        canRetry = false; statusText = string.Empty; Changed();
        if (pending is { IsPreReleaseVersion1: true } old)
        {
            if (old.Phase == HostedProvisioningPhase.Acknowledged) { await PromoteAsync(old); return; }
            HostedLegacyProbeResult probe = await provisioningClient!.ProbePreReleaseAsync(old.PriorConfiguration!, stop.Token);
            if (probe == HostedLegacyProbeResult.Valid) { await PromoteAsync(old); return; }
            if (probe == HostedLegacyProbeResult.Ambiguous)
            {
                canRetry = true; statusText = "The existing overlay setup could not be verified. Check your connection and try again."; return;
            }
            await pendingStore!.RemovePendingAsync(stop.Token); pending = null;
            if (!allowNewRequest) return;
        }
        if (pending is null)
        {
            if (!allowNewRequest) return;
            pending = HostedProvisioningState.Create(HostedProductionOrigins.Approved.Single(), HostedProductionOrigins.Approved);
            await pendingStore!.SavePendingAsync(pending, stop.Token);
        }
        if (pending.Phase == HostedProvisioningPhase.Acknowledged) { await PromoteAsync(pending); return; }

        for (int attempt = 0; attempt < 3 && enabled && !closing && !setupStopped; attempt++)
        {
            HostedProvisioningResult result = await provisioningClient!.ProvisionAsync(pending, stop.Token);
            if (result.Kind == HostedProvisioningResultKind.Provisioned && result.OverlayId is not null)
            {
                pending = pending.Acknowledged(result.OverlayId);
                await pendingStore!.SavePendingAsync(pending, stop.Token);
                await PromoteAsync(pending);
                return;
            }
            if (result.Kind == HostedProvisioningResultKind.Protocol)
            {
                statusText = "The overlay URL is unavailable for this version."; return;
            }
            if (attempt < 2)
            {
                int capMilliseconds = 500 * (1 << attempt);
                TimeSpan delay = result.RetryAfter ?? TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, capMilliseconds + 1));
                await retryDelay(delay, stop.Token);
            }
        }
        canRetry = true;
        statusText = "The overlay URL could not be prepared. Check your connection and try again.";
    }

    private async Task PromoteAsync(HostedProvisioningState acknowledged)
    {
        HostedPublisherConfiguration nextConfiguration = acknowledged.Configuration(HostedProductionOrigins.Approved);
        await store.SaveAsync(nextConfiguration, stop.Token);
        persistedConfiguration = nextConfiguration;
        stop.Token.ThrowIfCancellationRequested();
        HostedOverlayPublisher next = createSender(nextConfiguration);
        await ActivateSenderAsync(next);
        configuration = nextConfiguration;
        try { await pendingStore!.RemovePendingAsync(stop.Token); pending = null; }
        catch (OperationCanceledException) when (closing || setupStopped) { throw; }
        catch { }
        canRetry = false;
        if (sender is not null) Sender_StatusChanged(sender, EventArgs.Empty);
    }

    internal bool CopyReadUrl(Action<string> copy)
    {
        dispatcher.VerifyAccess();
        if (!CanCopy) return false;
        try { copy(configuration!.BuildReadUrl()); copyFeedbackText = "URL copied"; Changed(); return true; }
        catch { copyFeedbackText = "Could not copy the Overlay URL. Try Copy URL again."; Changed(); return false; }
    }

    private Task RunOperationAsync(Func<Task> action, bool showFailure)
    {
        dispatcher.VerifyAccess();
        if (closing || busy || !enabled) return Task.CompletedTask;
        busy = true; copyFeedbackText = string.Empty; Changed();
        return operation = RunAsync();
        async Task RunAsync()
        {
            try { await action(); }
            catch (OperationCanceledException) when (closing || setupStopped || !enabled) { }
            catch
            {
                if (showFailure)
                {
                    canRetry = configuration is null && pending is not null &&
                        (persistedConfiguration is null || pending.Matches(persistedConfiguration));
                    statusText = "The overlay URL could not be prepared. Check your connection and try again.";
                }
            }
            finally { busy = false; if (!closing) Changed(); }
        }
    }

    private async Task ConnectAsync(HostedPublisherConfiguration nextConfiguration)
    {
        if (closing || setupStopped || !enabled) return;
        stop.Token.ThrowIfCancellationRequested();
        await ActivateSenderAsync(createSender(nextConfiguration));
    }

    private async Task ActivateSenderAsync(HostedOverlayPublisher next)
    {
        HostedOverlayPublisher? previous = sender;
        try { stop.Token.ThrowIfCancellationRequested(); attachSender(adapter!, next); }
        catch { await next.CancelAsync(); throw; }
        if (previous is not null) previous.StatusChanged -= Sender_StatusChanged;
        sender = next;
        next.StatusChanged += Sender_StatusChanged;
        Sender_StatusChanged(next, EventArgs.Empty);
        if (previous is not null) await previous.DisposeAsync();
    }

    private void Sender_StatusChanged(object? source, EventArgs args)
    {
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => Sender_StatusChanged(source, args));
            return;
        }
        if (preferenceStore is null || !enabled || !ReferenceEquals(source, sender)) return;
        statusText = sender!.Status switch
        {
            HostedPublisherStatus.Ready => "Hosted Overlay is connected.",
            HostedPublisherStatus.Retrying => "Hosted Overlay is offline. Retrying while enabled; local tracking continues.",
            HostedPublisherStatus.CredentialsRequired or HostedPublisherStatus.Conflict or HostedPublisherStatus.InvalidProtocol or HostedPublisherStatus.CounterExhausted =>
                "Hosted Overlay could not connect. Local tracking continues; check your connection or pairing.",
            _ => "Connecting to Hosted Overlay...",
        };
        Changed();
    }

    private async Task RetireAsync()
    {
        HostedOverlayPublisher? previous = sender; sender = null; adapter?.Attach(null);
        if (previous is not null) previous.StatusChanged -= Sender_StatusChanged;
        if (previous is not null) await previous.DisposeAsync();
    }

    internal void PublishAccepted(PersistentTrackerState current, RuntimeGameReadResult? accepted)
    { dispatcher.VerifyAccess(); if (!closing) adapter?.PublishAccepted(current, accepted); }

    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    { dispatcher.VerifyAccess(); return closing ? Task.CompletedTask : adapter?.PublishAsync(notification, cancellationToken) ?? Task.CompletedTask; }

    public ValueTask DisposeAsync()
    {
        dispatcher.VerifyAccess();
        if (disposal is not null) return new(disposal);
        closing = true; copyFeedbackText = string.Empty; adapter?.StopOffering(); stop.Cancel(); Changed();
        return new(disposal = FinishAsync());
    }

    internal void StopSetup()
    {
        dispatcher.VerifyAccess();
        if (setupStopped || closing) return;
        setupStopped = true; copyFeedbackText = string.Empty; stop.Cancel(); Changed();
    }

    private async Task FinishAsync()
    {
        if (operation is not null) await operation;
        await RetireAsync(); provisioningClient?.Dispose(); stop.Dispose();
    }
}
