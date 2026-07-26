using System.Diagnostics;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text.Json;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed class ShutdownBenchmarkRunner(BenchmarkOptions options)
{
    internal const PipeOptions ReadinessPipeOptions =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private const int MaximumReadinessPayloadBytes = 16 * 1024;
    private const string DataRootOption = "--data-root";
    private const string ReadinessPipeOption = "--benchmark-readiness-pipe";
    private const string SingleInstanceMutexName = @"Global\SoulsTracker.SingleInstance.v1";
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly BenchmarkOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<ShutdownBenchmarkReport> RunAsync(CancellationToken cancellationToken = default)
    {
        ValidatePackage();
        if (!IsSingleInstanceMutexReleased())
        {
            throw new InvalidOperationException("Close SoulsTracker before running the benchmark.");
        }

        for (int index = 0; index < options.WarmupCount; index++)
        {
            ShutdownSample warmup = await RunIterationAsync(index + 1, cancellationToken);
            if (!warmup.Passed)
            {
                throw new InvalidOperationException(
                    $"Warm-up {index + 1} failed ({warmup.FailureCode ?? "validation_failed"}).");
            }
        }

        var samples = new List<ShutdownSample>(options.IterationCount);
        for (int index = 0; index < options.IterationCount; index++)
        {
            samples.Add(await RunIterationAsync(index + 1, cancellationToken));
        }

        ShutdownBudgets budgets = ShutdownBudgets.Default;
        ShutdownSummary summary = ShutdownStatistics.Calculate(samples, budgets);
        var report = new ShutdownBenchmarkReport(
            SchemaVersion: 1,
            Scenario: options.Scenario.ToString(),
            WarmupCount: options.WarmupCount,
            MeasuredCount: options.IterationCount,
            TimeoutMilliseconds: checked((int)options.HardTimeout.TotalMilliseconds),
            Budgets: budgets,
            Samples: samples,
            Summary: summary,
            Passed: summary.Passed);
        await WriteReportAsync(report, cancellationToken);
        return report;
    }

    private async Task<ShutdownSample> RunIterationAsync(
        int sampleNumber,
        CancellationToken cancellationToken)
    {
        string iterationRoot = CreateIterationRoot();
        string packageRoot = Path.Combine(iterationRoot, "package");
        string dataRoot = Path.Combine(iterationRoot, "data");
        string pipeName = $"SoulsTrackerShutdown-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dataRoot);
        CopyDirectory(options.PublishPath, packageRoot);

        Process? application = null;
        WindowsProcessTree? processTree = null;
        ClientWebSocket? overlaySocket = null;
        bool cleanExit = false;
        bool overlayConnectionClosed = false;
        bool mutexReleased = false;
        bool temporaryStateDeleted = false;
        double elapsedMilliseconds = 0;
        string? failureCode = null;

        try
        {
            await using var readinessPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                ReadinessPipeOptions);
            string executable = Path.Combine(packageRoot, "SoulsTracker.Desktop.exe");
            application = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = packageRoot,
                UseShellExecute = false,
                Arguments = string.Join(
                    ' ',
                    QuoteArgument(DataRootOption),
                    QuoteArgument(dataRoot),
                    QuoteArgument(ReadinessPipeOption),
                    QuoteArgument(pipeName)),
            }) ?? throw new InvalidOperationException("The packaged application did not start.");

            processTree = new WindowsProcessTree(application);
            BenchmarkReadinessMessage readiness = await WaitForReadinessAsync(
                readinessPipe,
                application.Id,
                cancellationToken);
            overlaySocket = new ClientWebSocket();
            await overlaySocket.ConnectAsync(readiness.CreateWebSocketUri(), cancellationToken);
            await ReceiveInitialOverlayMessageAsync(overlaySocket, cancellationToken);

            processTree.Refresh();
            Task<bool> overlayClosure = WaitForOverlayClosureAsync(overlaySocket);
            var stopwatch = Stopwatch.StartNew();
            if (!application.CloseMainWindow())
            {
                failureCode = "graceful_close_unavailable";
            }
            else
            {
                cleanExit = await WaitForProcessTreeExitAsync(
                    processTree,
                    options.HardTimeout,
                    cancellationToken);
            }

            stopwatch.Stop();
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            if (!cleanExit && failureCode is null)
            {
                failureCode = "shutdown_timeout";
            }

            if (cleanExit)
            {
                cleanExit = application.HasExited && application.ExitCode == 0;
                if (!cleanExit)
                {
                    failureCode = "nonzero_exit";
                }
            }

            overlayConnectionClosed = await overlayClosure.WaitAsync(
                TimeSpan.FromSeconds(1),
                cancellationToken);
            if (!overlayConnectionClosed && failureCode is null)
            {
                failureCode = "overlay_connection_remained_open";
            }

            mutexReleased = IsSingleInstanceMutexReleased();
            if (!mutexReleased && failureCode is null)
            {
                failureCode = "single_instance_mutex_remained_held";
            }
        }
        catch (TimeoutException)
        {
            failureCode ??= "readiness_timeout";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failureCode ??= "readiness_timeout";
        }
        catch (WebSocketException)
        {
            failureCode ??= "overlay_connection_failed";
        }
        catch (Exception)
        {
            failureCode ??= "iteration_failed";
        }
        finally
        {
            if (processTree is not null && !processTree.AllExited)
            {
                processTree.KillRemainingAfterMeasurement();
                await processTree.WaitForExitAfterCleanupAsync(CleanupTimeout);
            }

            overlaySocket?.Dispose();
            processTree?.Dispose();
            application?.Dispose();
            temporaryStateDeleted = await DeleteDirectoryWithRetriesAsync(iterationRoot);
            if (!temporaryStateDeleted && failureCode is null)
            {
                failureCode = "temporary_state_not_deleted";
            }
        }

        return new ShutdownSample(
            sampleNumber,
            Math.Round(elapsedMilliseconds, 3),
            cleanExit,
            overlayConnectionClosed,
            mutexReleased,
            temporaryStateDeleted,
            failureCode);
    }

    private static async Task<BenchmarkReadinessMessage> WaitForReadinessAsync(
        NamedPipeServerStream readinessPipe,
        int expectedProcessId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadinessTimeout);
        await readinessPipe.WaitForConnectionAsync(timeout.Token);
        using var payload = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await readinessPipe.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            if (payload.Length + read > MaximumReadinessPayloadBytes)
            {
                throw new InvalidDataException("The readiness payload was too large.");
            }

            await payload.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }

        return BenchmarkReadinessMessage.Parse(payload.ToArray(), expectedProcessId);
    }

    private static async Task ReceiveInitialOverlayMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadinessTimeout);
        ValueWebSocketReceiveResult received = await socket.ReceiveAsync(
            buffer.AsMemory(),
            timeout.Token);
        if (received.MessageType != WebSocketMessageType.Text || received.Count == 0)
        {
            throw new WebSocketException("The overlay did not publish its initial state.");
        }
    }

    private static async Task<bool> WaitForOverlayClosureAsync(ClientWebSocket socket)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult received = await socket.ReceiveAsync(
                    buffer.AsMemory(),
                    CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return true;
                }
            }
        }
        catch (WebSocketException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task<bool> WaitForProcessTreeExitAsync(
        WindowsProcessTree processTree,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long timeoutTimestamp = Stopwatch.GetTimestamp() +
            (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < timeoutTimestamp)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processTree.AllExited)
            {
                return true;
            }

            await Task.Delay(10, cancellationToken);
        }

        return processTree.AllExited;
    }

    private static bool IsSingleInstanceMutexReleased()
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out bool createdNew);
        if (createdNew)
        {
            mutex.ReleaseMutex();
        }

        return createdNew;
    }

    private void ValidatePackage()
    {
        if (!Directory.Exists(options.PublishPath) ||
            !File.Exists(Path.Combine(options.PublishPath, "SoulsTracker.Desktop.exe")))
        {
            throw new DirectoryNotFoundException("The packaged desktop payload is incomplete.");
        }
    }

    private async Task WriteReportAsync(
        ShutdownBenchmarkReport report,
        CancellationToken cancellationToken)
    {
        string? outputDirectory = Path.GetDirectoryName(options.OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("The benchmark output directory is invalid.");
        }

        Directory.CreateDirectory(outputDirectory);
        await using FileStream output = File.Create(options.OutputPath);
        await JsonSerializer.SerializeAsync(
            output,
            report,
            ReportJsonOptions,
            cancellationToken);
    }

    private static string CreateIterationRoot()
    {
        string parent = Path.Combine(Path.GetTempPath(), "SoulsTracker-shutdown-benchmark");
        Directory.CreateDirectory(parent);
        return Directory.CreateDirectory(Path.Combine(parent, Guid.NewGuid().ToString("N"))).FullName;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static async Task<bool> DeleteDirectoryWithRetriesAsync(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return !Directory.Exists(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(100 * (attempt + 1));
        }

        return !Directory.Exists(path);
    }

    private static string QuoteArgument(string argument) =>
        $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
