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
    private readonly HostedOverlayProvisioningClient? provisioningClient;
    private readonly Func<HostedPublisherConfiguration, HostedOverlayPublisher> createSender;
    private readonly Action<HostedDesktopPublisher, HostedOverlayPublisher> attachSender;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private readonly CancellationTokenSource stop = new();
    private HostedPublisherConfiguration? configuration;
    private HostedProvisioningState? pending;
    private HostedDesktopPublisher? adapter;
    private HostedOverlayPublisher? sender;
    private Task? operation, disposal;
    private bool closing, busy, setupStopped, canRetry, provisioningRequested;
    private string statusText = string.Empty, copyFeedbackText = string.Empty;

    internal HostedOverlayConnection(Dispatcher dispatcher, HostedPublisherConfigurationStore store,
        Func<HostedPublisherConfiguration, HostedOverlayPublisher>? createSender = null,
        HostedProvisioningStateStore? pendingStore = null,
        HostedOverlayProvisioningClient? provisioningClient = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Action<HostedDesktopPublisher, HostedOverlayPublisher>? attachSender = null)
    {
        this.dispatcher = dispatcher; this.store = store; this.pendingStore = pendingStore;
        this.provisioningClient = provisioningClient;
        this.createSender = createSender ?? (config => new HostedOverlayPublisher(config));
        this.retryDelay = retryDelay ?? Task.Delay;
        this.attachSender = attachSender ?? ((adapter, next) => adapter.Attach(next));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string UrlText => configuration?.BuildReadUrl() ?? (busy ? "Preparing overlay URL..." : string.Empty);
    public string StatusText => statusText;
    public string CopyFeedbackText => copyFeedbackText;
    public bool CanCopy => adapter is not null && !closing && !setupStopped && !busy && configuration is not null;
    public bool CanRetry => canRetry && adapter is not null && !closing && !setupStopped && !busy && configuration is null;
    public bool IsPreparing => busy && configuration is null;

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
        return RunOperationAsync(async () =>
        {
            configuration = await store.LoadAsync(stop.Token);
            if (configuration is not null) await ConnectAsync();
            if (pendingStore is null) return;
            pending = await pendingStore.LoadPendingAsync(stop.Token);
            if (configuration is not null)
            {
                if (pending?.Matches(configuration) == true) { await pendingStore.RemovePendingAsync(stop.Token); pending = null; }
                return;
            }
            if (pending is not null || provisioningRequested)
                await RecoverOrProvisionAsync(allowNewRequest: provisioningRequested);
        }, showFailure: true);
    }

    internal Task EnsureProvisionedAsync()
    {
        dispatcher.VerifyAccess();
        provisioningRequested = true;
        if (configuration is not null || pendingStore is null || provisioningClient is null || closing || setupStopped)
            return Task.CompletedTask;
        if (adapter is null || busy) return operation ?? Task.CompletedTask;
        return RunOperationAsync(() => RecoverOrProvisionAsync(allowNewRequest: true), showFailure: true);
    }

    internal Task RetryAsync() => !CanRetry ? Task.CompletedTask : EnsureProvisionedAsync();

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

        for (int attempt = 0; attempt < 3 && !closing && !setupStopped; attempt++)
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
        HostedOverlayPublisher next = createSender(nextConfiguration);
        await ActivateSenderAsync(next);
        configuration = nextConfiguration;
        try { await pendingStore!.RemovePendingAsync(stop.Token); pending = null; }
        catch (OperationCanceledException) when (closing || setupStopped) { throw; }
        catch { }
        canRetry = false; statusText = string.Empty;
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
        if (closing || busy) return Task.CompletedTask;
        busy = true; copyFeedbackText = string.Empty; Changed();
        return operation = RunAsync();
        async Task RunAsync()
        {
            try { await action(); }
            catch (OperationCanceledException) when (closing || setupStopped) { }
            catch { if (showFailure) { canRetry = configuration is null && pending is not null; statusText = "The overlay URL could not be prepared. Check your connection and try again."; } }
            finally { busy = false; if (!closing) Changed(); }
        }
    }

    private async Task ConnectAsync()
    {
        if (closing || setupStopped || configuration is null) return;
        await ActivateSenderAsync(createSender(configuration));
    }

    private async Task ActivateSenderAsync(HostedOverlayPublisher next)
    {
        HostedOverlayPublisher? previous = sender;
        try { attachSender(adapter!, next); }
        catch { await next.DisposeAsync(); throw; }
        sender = next;
        if (previous is not null) await previous.DisposeAsync();
    }

    private async Task RetireAsync()
    {
        HostedOverlayPublisher? previous = sender; sender = null; adapter?.Attach(null);
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
