using System.Diagnostics;
using System.Threading;

namespace SoulsTracker.Desktop;

/// <summary>Owns the desktop lifecycle lease that prevents concurrent SoulsTracker processes.</summary>
internal sealed class DesktopStartupController(IDesktopSingleInstanceLeaseFactory leaseFactory) : IDisposable
{
    private readonly IDesktopSingleInstanceLeaseFactory leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
    private IDesktopSingleInstanceLease? lease;
    private bool startAttempted;
    private bool disposed;

    /// <summary>Acquires the startup lease before invoking any desktop component composition.</summary>
    public async Task<DesktopStartupDecision> StartAsync(Func<Task> composeAndStartAsync)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(composeAndStartAsync);

        if (startAttempted)
        {
            throw new InvalidOperationException("Desktop startup can be attempted only once.");
        }

        startAttempted = true;
        DesktopSingleInstanceLeaseAcquisition acquisition = leaseFactory.Acquire();
        switch (acquisition.Status)
        {
            case DesktopSingleInstanceLeaseStatus.Acquired when acquisition.Lease is not null:
                lease = acquisition.Lease;
                await composeAndStartAsync();
                return DesktopStartupDecision.Started;
            case DesktopSingleInstanceLeaseStatus.AlreadyRunning:
                return DesktopStartupDecision.AlreadyRunning;
            default:
                return DesktopStartupDecision.ProtectionUnavailable;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        IDesktopSingleInstanceLease? acquiredLease = Interlocked.Exchange(ref lease, null);
        acquiredLease?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal enum DesktopStartupStatus
{
    Started,
    AlreadyRunning,
    ProtectionUnavailable,
}

internal sealed record DesktopStartupDecision(DesktopStartupStatus Status, string? UserMessage)
{
    public static DesktopStartupDecision Started { get; } = new(DesktopStartupStatus.Started, null);

    public static DesktopStartupDecision AlreadyRunning { get; } = new(
        DesktopStartupStatus.AlreadyRunning,
        "Unable to open SoulsTracker: another instance is already running.");

    public static DesktopStartupDecision ProtectionUnavailable { get; } = new(
        DesktopStartupStatus.ProtectionUnavailable,
        "Unable to open SoulsTracker because its startup protection could not be established. Close SoulsTracker and try again.");

    public bool CanStart => Status == DesktopStartupStatus.Started;
}

internal interface IDesktopSingleInstanceLeaseFactory
{
    DesktopSingleInstanceLeaseAcquisition Acquire();
}

internal sealed record DesktopSingleInstanceLeaseAcquisition(
    DesktopSingleInstanceLeaseStatus Status,
    IDesktopSingleInstanceLease? Lease)
{
    public static DesktopSingleInstanceLeaseAcquisition Acquired(IDesktopSingleInstanceLease lease) =>
        new(DesktopSingleInstanceLeaseStatus.Acquired, lease ?? throw new ArgumentNullException(nameof(lease)));

    public static DesktopSingleInstanceLeaseAcquisition AlreadyRunning() =>
        new(DesktopSingleInstanceLeaseStatus.AlreadyRunning, null);

    public static DesktopSingleInstanceLeaseAcquisition Failed() =>
        new(DesktopSingleInstanceLeaseStatus.Failed, null);
}

internal enum DesktopSingleInstanceLeaseStatus
{
    Acquired,
    AlreadyRunning,
    Failed,
}

internal interface IDesktopSingleInstanceLease : IDisposable;

internal sealed class WindowsSingleInstanceLeaseFactory : IDesktopSingleInstanceLeaseFactory
{
    private const string ProductionMutexName = @"Global\SoulsTracker.SingleInstance.v1";
    private readonly string mutexName;
    private readonly INamedMutexFactory mutexFactory;

    public WindowsSingleInstanceLeaseFactory()
        : this(ProductionMutexName, new WindowsNamedMutexFactory())
    {
    }

    internal WindowsSingleInstanceLeaseFactory(string mutexName, INamedMutexFactory mutexFactory)
    {
        this.mutexName = string.IsNullOrWhiteSpace(mutexName)
            ? throw new ArgumentException("A named mutex identifier is required.", nameof(mutexName))
            : mutexName;
        this.mutexFactory = mutexFactory ?? throw new ArgumentNullException(nameof(mutexFactory));
    }

    public DesktopSingleInstanceLeaseAcquisition Acquire()
    {
        try
        {
            INamedMutexHandle mutex = mutexFactory.CreateInitiallyOwned(mutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return DesktopSingleInstanceLeaseAcquisition.AlreadyRunning();
            }

            return DesktopSingleInstanceLeaseAcquisition.Acquired(new NamedMutexLease(mutex));
        }
        catch (Exception)
        {
            return DesktopSingleInstanceLeaseAcquisition.Failed();
        }
    }
}

internal interface INamedMutexFactory
{
    INamedMutexHandle CreateInitiallyOwned(string mutexName, out bool createdNew);
}

internal interface INamedMutexHandle : IDisposable
{
    void ReleaseMutex();
}

internal sealed class WindowsNamedMutexFactory : INamedMutexFactory
{
    public INamedMutexHandle CreateInitiallyOwned(string mutexName, out bool createdNew)
    {
        var mutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out createdNew);
        return new WindowsNamedMutexHandle(mutex);
    }

    private sealed class WindowsNamedMutexHandle(Mutex mutex) : INamedMutexHandle
    {
        private readonly Mutex mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));

        public void ReleaseMutex() => mutex.ReleaseMutex();

        public void Dispose() => mutex.Dispose();
    }
}

internal sealed class NamedMutexLease(INamedMutexHandle mutex) : IDesktopSingleInstanceLease
{
    private INamedMutexHandle? mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));

    public void Dispose()
    {
        INamedMutexHandle? acquiredMutex = Interlocked.Exchange(ref mutex, null);
        if (acquiredMutex is null)
        {
            return;
        }

        try
        {
            acquiredMutex.ReleaseMutex();
        }
        finally
        {
            acquiredMutex.Dispose();
        }
    }
}

/// <summary>Serializes desktop component shutdown so the single-instance lease is released only after component disposal completes.</summary>
internal sealed class DesktopShutdownCoordinator(
    Func<ValueTask> stopInputsAsync,
    Func<ValueTask> drainProducersAsync,
    Func<ValueTask> drainOutputsAsync,
    IDisposable singleInstanceLease,
    TimeSpan? totalShutdownTimeout = null,
    Action? cancelPending = null) : IDisposable
{
    private readonly Func<ValueTask> stopInputsAsync = stopInputsAsync ?? throw new ArgumentNullException(nameof(stopInputsAsync));
    private readonly Func<ValueTask> drainProducersAsync = drainProducersAsync ?? throw new ArgumentNullException(nameof(drainProducersAsync));
    private readonly Func<ValueTask> drainOutputsAsync = drainOutputsAsync ?? throw new ArgumentNullException(nameof(drainOutputsAsync));
    private readonly IDisposable singleInstanceLease = singleInstanceLease ?? throw new ArgumentNullException(nameof(singleInstanceLease));
    private readonly TimeSpan totalShutdownTimeout = totalShutdownTimeout is { } configured && configured > TimeSpan.Zero
        ? configured
        // Allow the hosted sender's two-second flush after producer quiescence.
        : TimeSpan.FromSeconds(5);
    private readonly object sync = new();
    private Task? shutdownTask;
    private Task? applicationShutdownTask;

    /// <summary>Disposes the existing desktop components in order and then releases the single-instance lease.</summary>
    public Task ShutdownAsync()
    {
        lock (sync)
        {
            return shutdownTask ??= ShutdownCoreAsync();
        }
    }

    /// <summary>Completes component disposal before requesting final WPF application shutdown.</summary>
    public Task RequestApplicationShutdownAsync(Action finalApplicationShutdown)
    {
        ArgumentNullException.ThrowIfNull(finalApplicationShutdown);

        lock (sync)
        {
            return applicationShutdownTask ??= CompleteApplicationShutdownAsync(finalApplicationShutdown);
        }
    }

    public void Dispose()
    {
        ShutdownAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task CompleteApplicationShutdownAsync(Action finalApplicationShutdown)
    {
        try
        {
            await ShutdownAsync();
        }
        finally
        {
            finalApplicationShutdown();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var shutdownStopwatch = Stopwatch.StartNew();
        try
        {
            await DisposeComponentAsync(stopInputsAsync, shutdownStopwatch).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await DisposeComponentAsync(drainProducersAsync, shutdownStopwatch).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await DisposeComponentAsync(drainOutputsAsync, shutdownStopwatch).ConfigureAwait(false);
                }
                finally
                {
                    singleInstanceLease.Dispose();
                }
            }
        }
    }

    private async Task DisposeComponentAsync(Func<ValueTask> disposeComponentAsync, Stopwatch shutdownStopwatch)
    {
        Task disposal = disposeComponentAsync().AsTask();
        TimeSpan remaining = totalShutdownTimeout - shutdownStopwatch.Elapsed;
        try { await disposal.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // A deadline requests cooperative cancellation. It cannot abandon a
            // producer or sender and release the single-instance lease underneath it.
            cancelPending?.Invoke();
            await disposal.ConfigureAwait(false);
            throw;
        }
    }
}
