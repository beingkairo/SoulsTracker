using System.ComponentModel;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Dispatcher-owned pairing lifecycle and accepted output connection.</summary>
public sealed class HostedOverlayConnection : INotifyPropertyChanged, ITrackerStateChangePublisher, IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly HostedPublisherConfigurationStore store;
    private readonly HostedProvisioningStateStore? pendingStore;
    private readonly HostedOverlayProvisioningClient? provisioningClient;
    private readonly Func<HostedPublisherConfiguration, HostedOverlayPublisher> createSender;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private readonly CancellationTokenSource stop = new();
    private HostedPublisherConfiguration? configuration;
    private HostedProvisioningState? pending;
    private HostedDesktopPublisher? adapter;
    private HostedOverlayPublisher? sender;
    private Task? operation, disposal;
    private CancellationTokenSource? setupRequest;
    private bool closing, busy, setupStopped;
    private bool pairingMayExist;
    private string statusText = "Set up the overlay to connect.";
    private string copyFeedbackText = string.Empty;

    internal HostedOverlayConnection(Dispatcher dispatcher, HostedPublisherConfigurationStore store,
        Func<HostedPublisherConfiguration, HostedOverlayPublisher>? createSender = null,
        HostedProvisioningStateStore? pendingStore = null,
        HostedOverlayProvisioningClient? provisioningClient = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        this.dispatcher = dispatcher;
        this.store = store;
        this.pendingStore = pendingStore;
        this.provisioningClient = provisioningClient;
        this.createSender = createSender ?? (config => new HostedOverlayPublisher(config));
        this.retryDelay = retryDelay ?? Task.Delay;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Host => configuration?.DisplayOrigin ?? "No overlay connection";
    public string StatusText => statusText;
    public string CopyFeedbackText => copyFeedbackText;
    public bool CanSetUp => adapter is not null && pendingStore is not null && provisioningClient is not null &&
        !closing && !setupStopped && !busy && pending is null;
    public bool CanCopy => adapter is not null && !closing && !setupStopped && !busy && configuration is not null;
    public bool CanReconnect => CanCopy;
    public bool CanRemove => adapter is not null && !closing && !setupStopped && !busy && pairingMayExist;
    public bool HasPendingSetup => pending is not null;
    public bool IsSetupPaused => pending?.Paused == true;
    public bool CanPauseSetup => pending is { Paused: false } && !closing && !setupStopped;
    public bool CanResumeSetup => pending is { Paused: true } && !closing && !setupStopped && !busy;
    public bool CanAbandonSetup => pending is not null && !closing && !setupStopped && !busy;

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
            pairingMayExist = true;
            var loaded = await store.LoadAsync(stop.Token);
            pairingMayExist = loaded is not null;
            if (closing || setupStopped) return;
            configuration = loaded;
            if (loaded is not null) await ConnectAsync();
            if (pendingStore is null) return;
            pending = await pendingStore.LoadPendingAsync(stop.Token);
            if (pending is null || closing || setupStopped) return;
            if (configuration is not null && pending.Phase == HostedProvisioningPhase.Acknowledged &&
                configuration.BuildReadUrl() == pending.Configuration.BuildReadUrl())
            {
                await pendingStore.RemovePendingAsync(stop.Token);
                pending = null;
                return;
            }
            if (pending.Paused)
                statusText = "Overlay setup is paused. Resume when you are ready.";
            else
                await ProvisionAndPromoteAsync();
        });
    }

    internal Task SetUpAsync(string setupCode, bool consent, bool replacementConfirmed, Action? pendingProtected = null)
    {
        dispatcher.VerifyAccess();
        if (!consent || !CanSetUp || (configuration is not null && !replacementConfirmed)) return Task.CompletedTask;
        return RunOperationAsync(async () =>
        {
            pending = HostedProvisioningState.Create(HostedProductionOrigins.Approved.Single(), setupCode,
                HostedProductionOrigins.Approved);
            await pendingStore!.SavePendingAsync(pending, stop.Token);
            pendingProtected?.Invoke();
            statusText = "Setting up overlay...";
            Changed();
            await ProvisionAndPromoteAsync();
        });
    }

    internal Task ResumeSetupAsync() => !CanResumeSetup ? Task.CompletedTask : RunOperationAsync(async () =>
    {
        pending = pending!.WithPaused(false);
        await pendingStore!.SavePendingAsync(pending, stop.Token);
        await ProvisionAndPromoteAsync();
    });

    internal async Task PauseSetupAsync()
    {
        dispatcher.VerifyAccess();
        if (!CanPauseSetup) return;
        pending = pending!.WithPaused(true);
        await pendingStore!.SavePendingAsync(pending, stop.Token);
        setupRequest?.Cancel();
        statusText = "Overlay setup is paused. Resume when you are ready.";
        Changed();
    }

    internal Task AbandonSetupAsync(bool operatorResetConfirmed) => !operatorResetConfirmed || !CanAbandonSetup
        ? Task.CompletedTask : RunOperationAsync(async () =>
        {
            await pendingStore!.RemovePendingAsync(stop.Token);
            pending = null;
            statusText = configuration is null ? "Set up the overlay to connect." : "Overlay ready";
        });

    internal Task ReconnectAsync() => !CanReconnect ? Task.CompletedTask : RunOperationAsync(ConnectAsync);

    internal Task RemoveAsync(bool confirmed) => !confirmed || !CanRemove ? Task.CompletedTask : RunOperationAsync(async () =>
    {
        await store.RemoveAsync(stop.Token);
        await RetireAsync();
        configuration = null;
        pairingMayExist = false;
        statusText = "Overlay connection removed from this PC. Online state and access have not been revoked or deleted.";
    });

    internal bool CopyReadUrl(Action<string> copy)
    {
        dispatcher.VerifyAccess();
        if (!CanCopy) return false;
        try
        {
            copy(configuration!.BuildReadUrl());
            copyFeedbackText = "URL copied";
            Changed();
            return true;
        }
        catch
        {
            copyFeedbackText = "Could not copy the Overlay URL. Try Copy URL again.";
            Changed();
            return false;
        }
    }

    private Task RunOperationAsync(Func<Task> action)
    {
        dispatcher.VerifyAccess();
        if (closing || busy) return Task.CompletedTask;
        busy = true;
        copyFeedbackText = string.Empty;
        Changed();
        return operation = RunAsync();
        async Task RunAsync()
        {
            try { await action(); }
            catch (OperationCanceledException) when (closing || setupStopped || pending?.Paused == true) { }
            catch { statusText = "Overlay setup failed. Previous connection and recoverable setup are retained if available."; }
            finally { busy = false; if (!closing) Changed(); }
        }
    }

    private async Task ConnectAsync()
    {
        await RetireAsync();
        if (closing || setupStopped || configuration is null) return;
        sender = createSender(configuration);
        sender.StatusChanged += SenderStatusChanged;
        adapter!.Attach(sender);
        UpdateSenderStatus();
    }

    private async Task ProvisionAndPromoteAsync()
    {
        while (!closing && !setupStopped && pending is { Paused: false } current)
        {
            if (current.Phase == HostedProvisioningPhase.Claiming)
            {
                setupRequest?.Dispose();
                setupRequest = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                HostedProvisioningResult result;
                try { result = await provisioningClient!.ProvisionAsync(current, setupRequest.Token); }
                catch (OperationCanceledException) when (current.Paused || closing || setupStopped || setupRequest.IsCancellationRequested)
                {
                    return;
                }
                if (pending?.Paused == true || closing || setupStopped) return;
                switch (result.Kind)
                {
                    case HostedProvisioningResultKind.Provisioned:
                        pending = current.Acknowledged();
                        await pendingStore!.SavePendingAsync(pending, stop.Token);
                        break;
                    case HostedProvisioningResultKind.Retry:
                        statusText = "Setup is waiting for the online service. Local tracking and TXT continue.";
                        Changed();
                        await retryDelay(result.RetryAfter ?? TimeSpan.FromSeconds(2), setupRequest.Token);
                        continue;
                    case HostedProvisioningResultKind.Denied:
                        pending = current.WithPaused(true);
                        await pendingStore!.SavePendingAsync(pending, stop.Token);
                        statusText = "This setup code is not valid. Request a new code.";
                        return;
                    case HostedProvisioningResultKind.Used:
                        pending = current.WithPaused(true);
                        await pendingStore!.SavePendingAsync(pending, stop.Token);
                        statusText = "This setup code has already been used. Request a new code.";
                        return;
                    default:
                        pending = current.WithPaused(true);
                        await pendingStore!.SavePendingAsync(pending, stop.Token);
                        statusText = "Overlay setup is unavailable for this version. Update SoulsTracker or contact the operator.";
                        return;
                }
            }

            if (pending is not { Phase: HostedProvisioningPhase.Acknowledged } acknowledged) continue;
            await store.SaveAsync(acknowledged.Configuration, stop.Token);
            if (closing || setupStopped) return;
            configuration = acknowledged.Configuration;
            pairingMayExist = true;
            await ConnectAsync();
            await pendingStore!.RemovePendingAsync(stop.Token);
            pending = null;
            statusText = "Overlay ready";
            return;
        }
    }

    private void SenderStatusChanged(object? source, EventArgs args)
    {
        if (dispatcher.HasShutdownStarted) return;
        _ = dispatcher.InvokeAsync(() =>
        {
            if (closing || !ReferenceEquals(source, sender)) return;
            UpdateSenderStatus();
            Changed();
        });
    }

    private void UpdateSenderStatus() => statusText = sender?.Status switch
    {
        HostedPublisherStatus.Ready => "Latest offered state acknowledged. Waiting for accepted game data if none is available yet.",
        HostedPublisherStatus.Retrying => "Retrying online delivery. Local tracking and TXT continue.",
        HostedPublisherStatus.CredentialsRequired => "Connection access was rejected. Set up a replacement with a new code.",
        HostedPublisherStatus.Conflict => "Another publisher owns this overlay. Close it before explicitly reconnecting.",
        HostedPublisherStatus.InvalidProtocol or HostedPublisherStatus.CounterExhausted => "Online publication paused. Contact the operator before reconnecting.",
        HostedPublisherStatus.Stopped => "Overlay publisher stopped.",
        _ => "Overlay delivery pending.",
    };

    private async Task RetireAsync()
    {
        var previous = sender;
        if (previous is not null) previous.StatusChanged -= SenderStatusChanged;
        sender = null;
        adapter?.Attach(null);
        if (previous is not null) await previous.DisposeAsync();
    }

    internal void PublishAccepted(PersistentTrackerState current, RuntimeGameReadResult? accepted)
    {
        dispatcher.VerifyAccess();
        if (!closing) adapter?.PublishAccepted(current, accepted);
    }

    public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
    {
        dispatcher.VerifyAccess();
        return closing ? Task.CompletedTask : adapter?.PublishAsync(notification, cancellationToken) ?? Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        dispatcher.VerifyAccess();
        if (disposal is not null) return new(disposal);
        closing = true;
        copyFeedbackText = string.Empty;
        adapter?.StopOffering();
        stop.Cancel();
        Changed();
        return new(disposal = FinishAsync());
    }

    internal void StopSetup()
    {
        dispatcher.VerifyAccess();
        if (setupStopped || closing) return;
        setupStopped = true;
        copyFeedbackText = string.Empty;
        setupRequest?.Cancel();
        stop.Cancel();
        Changed();
    }

    private async Task FinishAsync()
    {
        if (operation is not null) await operation;
        await RetireAsync();
        setupRequest?.Dispose();
        provisioningClient?.Dispose();
        stop.Dispose();
    }
}
