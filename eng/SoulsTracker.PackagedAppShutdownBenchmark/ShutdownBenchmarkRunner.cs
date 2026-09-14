using System.Diagnostics;
using System.Text.Json;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed class ShutdownBenchmarkRunner(BenchmarkOptions options)
{
    private const string DataRootOption = "--data-root";
    private const string SingleInstanceMutexName = @"Global\SoulsTracker.SingleInstance.v1";
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
        Directory.CreateDirectory(dataRoot);
        CopyDirectory(options.PublishPath, packageRoot);

        Process? application = null;
        WindowsProcessTree? processTree = null;
        bool cleanExit = false;
        bool overlayConnectionClosed = false;
        bool mutexReleased = false;
        bool temporaryStateDeleted = false;
        double elapsedMilliseconds = 0;
        string? failureCode = null;

        try
        {
            string executable = Path.Combine(packageRoot, "SoulsTracker.Desktop.exe");
            application = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = packageRoot,
                UseShellExecute = false,
                Arguments = string.Join(' ', QuoteArgument(DataRootOption), QuoteArgument(dataRoot)),
            }) ?? throw new InvalidOperationException("The packaged application did not start.");

            processTree = new WindowsProcessTree(application);
            processTree.Refresh();
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

            overlayConnectionClosed = true;

            mutexReleased = IsSingleInstanceMutexReleased();
            if (!mutexReleased && failureCode is null)
            {
                failureCode = "single_instance_mutex_remained_held";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
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
