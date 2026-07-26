using System.Globalization;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed record BenchmarkOptions(
    string PublishPath,
    string OutputPath,
    int WarmupCount,
    int IterationCount,
    TimeSpan HardTimeout,
    BenchmarkScenario Scenario)
{
    public const int DefaultWarmupCount = 1;
    public const int DefaultIterationCount = 10;
    public const int DefaultTimeoutSeconds = 10;

    public static BenchmarkOptions Parse(IReadOnlyList<string> arguments)
    {
        string? publishPath = null;
        string? outputPath = null;
        int warmupCount = DefaultWarmupCount;
        int iterationCount = DefaultIterationCount;
        int timeoutSeconds = DefaultTimeoutSeconds;
        BenchmarkScenario scenario = BenchmarkScenario.PreviewAndObs;

        for (int index = 0; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count)
            {
                throw new ArgumentException("Every benchmark option requires a value.");
            }

            string value = arguments[index + 1];
            switch (arguments[index])
            {
                case "--publish-path":
                    publishPath = RequireAbsolutePath(value, "publish path");
                    break;
                case "--output-path":
                    outputPath = RequireAbsolutePath(value, "output path");
                    break;
                case "--warmup":
                    warmupCount = ParseNonNegativeInteger(value, "warmup count");
                    break;
                case "--iterations":
                    iterationCount = ParsePositiveInteger(value, "iteration count");
                    break;
                case "--timeout-seconds":
                    timeoutSeconds = ParsePositiveInteger(value, "timeout");
                    break;
                case "--scenario":
                    if (!string.Equals(
                        value,
                        nameof(BenchmarkScenario.PreviewAndObs),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("The benchmark scenario is not supported.");
                    }

                    scenario = BenchmarkScenario.PreviewAndObs;
                    break;
                default:
                    throw new ArgumentException("An unknown benchmark option was supplied.");
            }
        }

        if (publishPath is null || outputPath is null)
        {
            throw new ArgumentException("Publish and output paths are required.");
        }

        return new BenchmarkOptions(
            publishPath,
            outputPath,
            warmupCount,
            iterationCount,
            TimeSpan.FromSeconds(timeoutSeconds),
            scenario);
    }

    private static string RequireAbsolutePath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"The {name} must be absolute.");
        }

        return Path.GetFullPath(value);
    }

    private static int ParseNonNegativeInteger(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ||
            result < 0)
        {
            throw new ArgumentException($"The {name} must be a non-negative integer.");
        }

        return result;
    }

    private static int ParsePositiveInteger(string value, string name)
    {
        int result = ParseNonNegativeInteger(value, name);
        if (result == 0)
        {
            throw new ArgumentException($"The {name} must be greater than zero.");
        }

        return result;
    }
}

internal enum BenchmarkScenario
{
    PreviewAndObs,
}
