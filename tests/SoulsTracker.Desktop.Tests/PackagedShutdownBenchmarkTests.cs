using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using SoulsTracker.PackagedAppShutdownBenchmark;

namespace SoulsTracker.Desktop.Tests;

public sealed class PackagedShutdownBenchmarkTests
{
    [Fact]
    public void BothReadinessPipeEndpointsAreRestrictedToTheCurrentUser()
    {
        Assert.Equal(
            PipeOptions.CurrentUserOnly,
            PackagedBenchmarkReadinessReporter.ReadinessPipeOptions &
            PipeOptions.CurrentUserOnly);
        Assert.Equal(
            PipeOptions.CurrentUserOnly,
            ShutdownBenchmarkRunner.ReadinessPipeOptions &
            PipeOptions.CurrentUserOnly);
    }

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
            SchemaVersion = 2,
            MessageType = "preview-ready",
            ProcessId = 42,
            OverlayUrl = "http://127.0.0.1:12345/overlay/total_deaths?token=secret",
        });

        BenchmarkReadinessMessage message = BenchmarkReadinessMessage.ParsePreview(valid, 42);

        Assert.Equal(
            "ws://127.0.0.1:12345/overlay/ws?token=secret",
            message.CreateWebSocketUri().AbsoluteUri);
        Assert.Throws<InvalidDataException>(
            () => BenchmarkReadinessMessage.ParsePreview(valid, 43));
    }

    [Fact]
    public void ExitedOwnedParentCannotSeedProcessDiscovery()
    {
        var root = new FakeProcessHandle(10) { HasExited = true };
        var candidate = new FakeProcessHandle(11);
        var snapshots = new FakeProcessSnapshotSource(
            [new Dictionary<int, int> { [11] = 10 }]);
        var factory = new FakeProcessHandleFactory(candidate);

        using var tree = new WindowsProcessTree(root, snapshots, factory);

        Assert.Equal([10], tree.OwnedProcessIds);
        Assert.Empty(factory.OpenedProcessIds);
    }

    [Fact]
    public void CandidateParentRelationshipIsRevalidatedBeforeOwnership()
    {
        var root = new FakeProcessHandle(10);
        var candidate = new FakeProcessHandle(11);
        var snapshots = new FakeProcessSnapshotSource(
        [
            new Dictionary<int, int> { [11] = 10 },
            new Dictionary<int, int> { [11] = 99 },
        ]);
        var factory = new FakeProcessHandleFactory(candidate);

        using var tree = new WindowsProcessTree(root, snapshots, factory);

        Assert.Equal([10], tree.OwnedProcessIds);
        Assert.True(candidate.IsDisposed);
    }

    [Fact]
    public void ExitedCandidateHandleIsRejectedEvenWhenItsPidReappearsUnderTheParent()
    {
        var root = new FakeProcessHandle(10);
        var candidate = new FakeProcessHandle(11);
        var snapshots = new FakeProcessSnapshotSource(
        [
            new Dictionary<int, int> { [11] = 10 },
            new Dictionary<int, int> { [11] = 10 },
        ],
        captureIndex =>
        {
            if (captureIndex == 1)
            {
                candidate.HasExited = true;
            }
        });
        var factory = new FakeProcessHandleFactory(candidate);

        using var tree = new WindowsProcessTree(root, snapshots, factory);

        Assert.Equal([10], tree.OwnedProcessIds);
        Assert.True(candidate.IsDisposed);
    }

    [Fact]
    public void ParentMustStillBeRunningWhenCandidateOwnershipIsCommitted()
    {
        var root = new FakeProcessHandle(10);
        var candidate = new FakeProcessHandle(11);
        var snapshots = new FakeProcessSnapshotSource(
        [
            new Dictionary<int, int> { [11] = 10 },
            new Dictionary<int, int> { [11] = 10 },
        ],
        captureIndex =>
        {
            if (captureIndex == 1)
            {
                root.HasExited = true;
            }
        });
        var factory = new FakeProcessHandleFactory(candidate);

        using var tree = new WindowsProcessTree(root, snapshots, factory);

        Assert.Equal([10], tree.OwnedProcessIds);
        Assert.True(candidate.IsDisposed);
    }

    [Fact]
    public async Task TwoPhaseReadinessCompletesAfterAcknowledgementAndTwoConnections()
    {
        string pipeName = $"SoulsTrackerShutdown-{Guid.NewGuid():N}";
        await using var server = CreateReadinessServer(pipeName);
        Task reporter = PackagedBenchmarkReadinessReporter.ReportAsync(
            pipeName,
            "http://127.0.0.1:12345/overlay/total_deaths?token=secret",
            () => 2,
            TimeSpan.FromSeconds(2));

        await server.WaitForConnectionAsync();
        PackagedBenchmarkReadinessReporter.ReadinessMessage preview =
            await PackagedBenchmarkReadinessReporter.ReadMessageAsync(
                server,
                CancellationToken.None);
        Assert.Equal(
            PackagedBenchmarkReadinessReporter.PreviewReadyMessageType,
            preview.MessageType);
        await PackagedBenchmarkReadinessReporter.WriteMessageAsync(
            server,
            new PackagedBenchmarkReadinessReporter.ReadinessMessage(
                PackagedBenchmarkReadinessReporter.ProtocolSchemaVersion,
                PackagedBenchmarkReadinessReporter.ObsConnectedMessageType,
                preview.ProcessId,
                OverlayUrl: null),
            CancellationToken.None);
        PackagedBenchmarkReadinessReporter.ReadinessMessage final =
            await PackagedBenchmarkReadinessReporter.ReadMessageAsync(
                server,
                CancellationToken.None);

        await reporter;
        Assert.Equal(
            PackagedBenchmarkReadinessReporter.FinalReadyMessageType,
            final.MessageType);
    }

    [Fact]
    public async Task TwoPhaseReadinessTimesOutWithoutAcknowledgement()
    {
        string pipeName = $"SoulsTrackerShutdown-{Guid.NewGuid():N}";
        await using var server = CreateReadinessServer(pipeName);
        Task reporter = PackagedBenchmarkReadinessReporter.ReportAsync(
            pipeName,
            "http://127.0.0.1:12345/overlay/total_deaths?token=secret",
            () => 2,
            TimeSpan.FromMilliseconds(100));

        await server.WaitForConnectionAsync();
        _ = await PackagedBenchmarkReadinessReporter.ReadMessageAsync(
            server,
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporter);
    }

    [Fact]
    public async Task TwoPhaseReadinessTimesOutWhenTwoConnectionsNeverOverlap()
    {
        string pipeName = $"SoulsTrackerShutdown-{Guid.NewGuid():N}";
        await using var server = CreateReadinessServer(pipeName);
        Task reporter = PackagedBenchmarkReadinessReporter.ReportAsync(
            pipeName,
            "http://127.0.0.1:12345/overlay/total_deaths?token=secret",
            () => 1,
            TimeSpan.FromMilliseconds(150));

        await server.WaitForConnectionAsync();
        PackagedBenchmarkReadinessReporter.ReadinessMessage preview =
            await PackagedBenchmarkReadinessReporter.ReadMessageAsync(
                server,
                CancellationToken.None);
        await PackagedBenchmarkReadinessReporter.WriteMessageAsync(
            server,
            new PackagedBenchmarkReadinessReporter.ReadinessMessage(
                PackagedBenchmarkReadinessReporter.ProtocolSchemaVersion,
                PackagedBenchmarkReadinessReporter.ObsConnectedMessageType,
                preview.ProcessId,
                OverlayUrl: null),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporter);
    }

    [Fact]
    public async Task OverlayClosureTimeoutHasItsOwnFailureCode()
    {
        var neverCloses = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        string? failureCode = await ShutdownBenchmarkRunner.VerifyOverlayClosureAsync(
            neverCloses.Task,
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        Assert.Equal("overlay_connection_remained_open", failureCode);
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

    private static NamedPipeServerStream CreateReadinessServer(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private sealed class FakeProcessHandle(int id) : IOwnedProcessHandle
    {
        public int Id { get; } = id;
        public bool HasExited { get; set; }
        public bool IsDisposed { get; private set; }
        public void Kill() => HasExited = true;
        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeProcessHandleFactory(params FakeProcessHandle[] processes)
        : IProcessHandleFactory
    {
        private readonly Dictionary<int, FakeProcessHandle> processes =
            processes.ToDictionary(process => process.Id);

        public List<int> OpenedProcessIds { get; } = [];

        public IOwnedProcessHandle Open(int processId)
        {
            OpenedProcessIds.Add(processId);
            return processes[processId];
        }
    }

    private sealed class FakeProcessSnapshotSource(
        IReadOnlyList<IReadOnlyDictionary<int, int>> snapshots,
        Action<int>? beforeCapture = null)
        : IProcessSnapshotSource
    {
        private int captureIndex;

        public IReadOnlyDictionary<int, int> CaptureParentProcessIds()
        {
            int currentIndex = captureIndex++;
            beforeCapture?.Invoke(currentIndex);
            return snapshots[Math.Min(currentIndex, snapshots.Count - 1)];
        }
    }
}
