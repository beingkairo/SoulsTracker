using System.Text.Json.Serialization;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed record ShutdownBudgets(
    double MedianMilliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds)
{
    public static ShutdownBudgets Default { get; } = new(1250, 2000, 3000);
}

internal sealed record ShutdownSummary(
    double MedianMilliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds,
    bool Passed);

internal sealed record ShutdownSample(
    int Sample,
    double Milliseconds,
    bool CleanExit,
    bool OverlayConnectionClosed,
    bool MutexReleased,
    bool TemporaryStateDeleted,
    string? FailureCode)
{
    [JsonIgnore]
    public bool Passed =>
        CleanExit &&
        OverlayConnectionClosed &&
        MutexReleased &&
        TemporaryStateDeleted &&
        FailureCode is null;
}

internal sealed record ShutdownBenchmarkReport(
    int SchemaVersion,
    string Scenario,
    int WarmupCount,
    int MeasuredCount,
    int TimeoutMilliseconds,
    ShutdownBudgets Budgets,
    IReadOnlyList<ShutdownSample> Samples,
    ShutdownSummary Summary,
    bool Passed);

internal static class ShutdownStatistics
{
    public static ShutdownSummary Calculate(
        IReadOnlyList<ShutdownSample> samples,
        ShutdownBudgets budgets)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(budgets);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one measured sample is required.", nameof(samples));
        }

        double[] ordered = samples.Select(sample => sample.Milliseconds).Order().ToArray();
        double median = Percentile(ordered, 50);
        double p95 = Percentile(ordered, 95);
        double maximum = ordered[^1];
        bool passed = samples.All(sample => sample.Passed) &&
            median <= budgets.MedianMilliseconds &&
            p95 <= budgets.P95Milliseconds &&
            maximum <= budgets.MaximumMilliseconds;
        return new ShutdownSummary(median, p95, maximum, passed);
    }

    internal static double Percentile(IReadOnlyList<double> orderedValues, int percentile)
    {
        if (orderedValues.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(orderedValues));
        }

        if (percentile is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        int rank = (int)Math.Ceiling(percentile / 100d * orderedValues.Count);
        return orderedValues[Math.Clamp(rank - 1, 0, orderedValues.Count - 1)];
    }
}
