using SoulsTracker.PackagedAppShutdownBenchmark;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This benchmark requires Windows.");
    return 2;
}

try
{
    BenchmarkOptions options = BenchmarkOptions.Parse(args);
    var runner = new ShutdownBenchmarkRunner(options);
    ShutdownBenchmarkReport report = await runner.RunAsync();

    Console.WriteLine("Sample  Shutdown (ms)  Result");
    foreach (ShutdownSample sample in report.Samples)
    {
        Console.WriteLine(
            $"{sample.Sample,6}  {sample.Milliseconds,13:F3}  " +
            $"{(sample.Passed ? "PASS" : $"FAIL ({sample.FailureCode})")}");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"Median {report.Summary.MedianMilliseconds:F3} ms | " +
        $"p95 {report.Summary.P95Milliseconds:F3} ms | " +
        $"maximum {report.Summary.MaximumMilliseconds:F3} ms");
    Console.WriteLine(
        $"Budgets: median <= {report.Budgets.MedianMilliseconds:F0} ms, " +
        $"p95 <= {report.Budgets.P95Milliseconds:F0} ms, " +
        $"maximum <= {report.Budgets.MaximumMilliseconds:F0} ms");
    Console.WriteLine(report.Passed ? "PASS" : "FAIL");
    return report.Passed ? 0 : 1;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (Exception)
{
    Console.Error.WriteLine("The packaged shutdown benchmark could not complete.");
    return 1;
}
