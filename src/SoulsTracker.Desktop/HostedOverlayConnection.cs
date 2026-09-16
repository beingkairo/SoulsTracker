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
    private readonly Func<HostedPublisherConfiguration, HostedOverlayPublisher> createSender;
    private readonly CancellationTokenSource stop = new();
    private HostedPublisherConfiguration? configuration;
    private HostedDesktopPublisher? adapter;
    private HostedOverlayPublisher? sender;
    private Task? operation, disposal;
    private bool closing, busy, setupStopped;
    private bool pairingMayExist;
    private string statusText = "Not paired. Import an operator-issued pairing file to connect.";

    internal HostedOverlayConnection(Dispatcher dispatcher, HostedPublisherConfigurationStore store,
        Func<HostedPublisherConfiguration, HostedOverlayPublisher>? createSender = null)
    {
        this.dispatcher = dispatcher;
        this.store = store;
        this.createSender = createSender ?? (config => new HostedOverlayPublisher(config));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Host => configuration?.DisplayOrigin ?? "No hosted connection";
    public string StatusText => statusText;
    public bool CanImport => adapter is not null && !closing && !setupStopped && !busy;
    public bool CanCopy => CanImport && configuration is not null;
    public bool CanReconnect => CanCopy;
    public bool CanRemove => CanImport && pairingMayExist;

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
        });
    }

    internal Task ImportAsync(string path, bool consent)
    {
        dispatcher.VerifyAccess();
        if (!consent || !CanImport) return Task.CompletedTask;
        return RunOperationAsync(async () =>
        {
            var imported = await store.ReadPairingAsync(path, stop.Token);
            await store.SaveAsync(imported, stop.Token);
            if (closing || setupStopped) return;
            configuration = imported;
            pairingMayExist = true;
            await ConnectAsync();
        });
    }

    internal Task ReconnectAsync() => !CanReconnect ? Task.CompletedTask : RunOperationAsync(ConnectAsync);

    internal Task RemoveAsync(bool confirmed) => !confirmed || !CanRemove ? Task.CompletedTask : RunOperationAsync(async () =>
    {
        await store.RemoveAsync(stop.Token);
        await RetireAsync();
        configuration = null;
        pairingMayExist = false;
        statusText = "Pairing removed from this PC. Cloud state and capabilities have not been revoked or deleted.";
    });

    internal bool CopyReadUrl(Action<string> copy)
    {
        dispatcher.VerifyAccess();
        if (!CanCopy) return false;
        try
        {
            copy(configuration!.BuildReadUrl());
            statusText = "Read-only OBS URL copied. Keep the URL private.";
            Changed();
            return true;
        }
        catch
        {
            statusText = "Could not copy the URL. Try Copy OBS URL again.";
            Changed();
            return false;
        }
    }

    private Task RunOperationAsync(Func<Task> action)
    {
        dispatcher.VerifyAccess();
        if (closing || busy) return Task.CompletedTask;
        busy = true;
        Changed();
        return operation = RunAsync();
        async Task RunAsync()
        {
            try { await action(); }
            catch (OperationCanceledException) when (closing || setupStopped) { }
            catch { statusText = "Pairing operation failed. Previous pairing is retained if available. Import a valid bundle for an approved host, or retry."; }
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
        HostedPublisherStatus.Retrying => "Retrying hosted delivery. Local tracking and TXT continue.",
        HostedPublisherStatus.CredentialsRequired => "Credentials rejected. Import replacement pairing from the operator.",
        HostedPublisherStatus.Conflict => "Another publisher owns this overlay. Close it before explicitly reconnecting.",
        HostedPublisherStatus.InvalidProtocol or HostedPublisherStatus.CounterExhausted => "Hosted publication paused. Check pairing with the operator before reconnecting.",
        HostedPublisherStatus.Stopped => "Hosted publisher stopped.",
        _ => "Hosted delivery pending.",
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
        stop.Cancel();
        Changed();
    }

    private async Task FinishAsync()
    {
        if (operation is not null) await operation;
        await RetireAsync();
        stop.Dispose();
    }
}
