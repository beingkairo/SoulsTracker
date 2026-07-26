using System.IO;
using System.Text.Json;
using SoulsTracker.PackagedAppShutdownBenchmark;

namespace SoulsTracker.Desktop.Tests;

public sealed class PackagedShutdownBenchmarkTests
{
    [Fact]
    public void OptionsUseTheRequiredDefaults()
    {
        string publishPath = Path.Combine(Path.GetTempPath(), "publish");
        string outputPath = Path.Combine(Path.GetTempPath(), "result.json");

        BenchmarkOptions options = BenchmarkOptions.Parse(
            ["--publish-path", publishPath, "--output-path", outputPath]);

        Assert.Equal(1, options.WarmupCount);
        Assert.Equal(10, options.IterationCount);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HardTimeout);
        Assert.Equal(BenchmarkScenario.PreviewAndObs, options.Scenario);
    }

    [Theory]
    [InlineData("--warmup", "-1")]
    [InlineData("--iterations", "0")]
    [InlineData("--timeout-seconds", "0")]
    [InlineData("--scenario", "Unknown")]
    [InlineData("--scenario", "0")]
    public void OptionsRejectInvalidValues(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(
        [
            "--publish-path",
            Path.Combine(Path.GetTempPath(), "publish"),
            "--output-path",
            Path.Combine(Path.GetTempPath(), "result.json"),
            option,
            value,
        ]));
    }

    [Fact]
    public void StatisticsUseNearestRankAndEnforceEveryBudget()
    {
        ShutdownSample[] samples = Enumerable.Range(1, 10)
            .Select(index => PassingSample(index, index * 100))
            .ToArray();

        ShutdownSummary summary = ShutdownStatistics.Calculate(
            samples,
            new ShutdownBudgets(550, 950, 1000));

        Assert.Equal(500, summary.MedianMilliseconds);
        Assert.Equal(1000, summary.P95Milliseconds);
        Assert.Equal(1000, summary.MaximumMilliseconds);
        Assert.False(summary.Passed);
    }

    [Fact]
    public void FailedCorrectnessCheckFailsTheSummaryWithinTimingBudgets()
    {
        ShutdownSample failed = PassingSample(1, 100) with { MutexReleased = false };

        ShutdownSummary summary = ShutdownStatistics.Calculate(
            [failed],
            ShutdownBudgets.Default);

        Assert.False(summary.Passed);
    }

    [Fact]
    public void ReadinessAcceptsOnlyTheExpectedLoopbackProcessAndRoute()
    {
        byte[] valid = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            ProcessId = 42,
            OverlayUrl = "http://127.0.0.1:12345/overlay/total_deaths?token=secret",
        });

        BenchmarkReadinessMessage message = BenchmarkReadinessMessage.Parse(valid, 42);

        Assert.Equal(
            "ws://127.0.0.1:12345/overlay/ws?token=secret",
            message.CreateWebSocketUri().AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => BenchmarkReadinessMessage.Parse(valid, 43));
    }

    [Fact]
    public void FailureCleanupOwnershipExcludesUnrelatedProcesses()
    {
        var parents = new Dictionary<int, int>
        {
            [11] = 10,
            [12] = 11,
            [20] = 1,
            [21] = 20,
        };

        IReadOnlySet<int> owned = ProcessTreeOwnership.ExpandOwnedProcessIds([10], parents);

        Assert.Equal([10, 11, 12], owned.Order());
        Assert.DoesNotContain(20, owned);
        Assert.DoesNotContain(21, owned);
    }

    [Fact]
    public void SerializedReportContainsNoPathsOrOverlayCredentials()
    {
        ShutdownSample sample = PassingSample(1, 100);
        ShutdownSummary summary = ShutdownStatistics.Calculate([sample], ShutdownBudgets.Default);
        var report = new ShutdownBenchmarkReport(
            1,
            "PreviewAndObs",
            1,
            1,
            10_000,
            ShutdownBudgets.Default,
            [sample],
            summary,
            true);

        string json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    private static ShutdownSample PassingSample(int sample, double milliseconds) =>
        new(sample, milliseconds, true, true, true, true, null);
}
