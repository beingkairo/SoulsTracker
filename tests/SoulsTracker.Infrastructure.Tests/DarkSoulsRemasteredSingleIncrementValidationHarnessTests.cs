using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class DarkSoulsRemasteredSingleIncrementValidationHarnessTests
{
    private static readonly ProcessModuleFileIdentity ExactCandidate = new(
        "DarkSoulsRemastered.exe",
        "1,0,0,0",
        "1",
        "A45AAA36DD2F6CC151670A639EA5547043CF38EA79FF4178B963C6ED71F98D7B");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("incorrect")]
    public async Task MissingOrIncorrectConfirmationPreventsEveryProcessInteraction(string? confirmation)
    {
        var enumerator = new FakeProcessEnumerator();
        var factory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var harness = CreateHarness(enumerator, factory, new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await harness.RunIfConfirmedAsync(confirmation, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, enumerator.EnumerationCalls);
        Assert.Equal(0, factory.AttachCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ZeroOrMultipleCandidatesReturnUnsupportedAndDisposeCandidates(int candidateCount)
    {
        FakeProcessCandidate[] candidates = Enumerable.Range(0, candidateCount).Select(_ => new FakeProcessCandidate()).ToArray();
        var enumerator = new FakeProcessEnumerator(candidates);
        var factory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var harness = CreateHarness(enumerator, factory, new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredSingleIncrementValidationHarness.RequiredConfirmation,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, result.Outcome);
        Assert.Equal(1, enumerator.EnumerationCalls);
        Assert.Equal(0, factory.AttachCalls);
        Assert.All(candidates, candidate => Assert.True(candidate.IsDisposed));
    }

    [Fact]
    public async Task SameAttachmentIsExactlyIdentifiedBeforeEveryBoundedMemoryRead()
    {
        var candidate = new FakeProcessCandidate();
        var attachment = new FakeAttachment(ExactCandidate, Observation(pointer: 0x2000, value: 12).Concat(Observation(pointer: 0x2000, value: 13)).ToArray());
        var factory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment));
        var delay = new RecordingDelay();
        var harness = CreateHarness(new FakeProcessEnumerator(candidate), factory, delay);

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredSingleIncrementValidationHarness.RequiredConfirmation,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.ObservedSingleIncrement, result.Outcome);
        Assert.Equal(1, factory.AttachCalls);
        Assert.Equal(4, attachment.IdentityQueryCalls);
        Assert.Equal(2, attachment.ModuleBaseQueryCalls);
        Assert.Equal([8, 4, 8, 4], attachment.DestinationLengths);
        Assert.Equal(4, attachment.MemoryReadCalls);
        Assert.Single(delay.RequestedDelays);
        Assert.Equal(TimeSpan.FromSeconds(2), delay.RequestedDelays[0]);
        Assert.True(candidate.IsDisposed);
        Assert.True(attachment.IsDisposed);
    }

    [Fact]
    public async Task UnsupportedIdentityPreventsModuleAndMemoryReadsOnTheSameAttachment()
    {
        var candidate = new FakeProcessCandidate();
        var attachment = new FakeAttachment(ExactCandidate with { Sha256 = new string('0', 64) });
        var harness = CreateHarness(
            new FakeProcessEnumerator(candidate),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredSingleIncrementValidationHarness.RequiredConfirmation,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, result.Outcome);
        Assert.Equal(1, attachment.IdentityQueryCalls);
        Assert.Equal(0, attachment.ModuleBaseQueryCalls);
        Assert.Equal(0, attachment.MemoryReadCalls);
        Assert.True(candidate.IsDisposed);
        Assert.True(attachment.IsDisposed);
    }

    [Fact]
    public async Task NullPointerFailsClosedWithoutAttemptingFinalValueRead()
    {
        var attachment = new FakeAttachment(ExactCandidate, PointerOnly(0));
        var harness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await RunConfirmedAsync(harness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, result.Outcome);
        Assert.Equal([8], attachment.DestinationLengths);
        Assert.Equal(1, attachment.IdentityQueryCalls);
    }

    [Fact]
    public async Task UnavailableOrCancelledMainModuleBaseFailsClosedBeforeMemoryRead()
    {
        var unavailableAttachment = new FakeAttachment(ExactCandidate, ReadOnlyMainModuleBaseResult.Unavailable());
        var unavailableHarness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(unavailableAttachment)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? unavailableResult = await RunConfirmedAsync(unavailableHarness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, unavailableResult.Outcome);
        Assert.Equal(0, unavailableAttachment.MemoryReadCalls);

        var cancelledAttachment = new FakeAttachment(ExactCandidate, ReadOnlyMainModuleBaseResult.Cancelled());
        var cancelledHarness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(cancelledAttachment)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? cancelledResult = await RunConfirmedAsync(cancelledHarness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Cancelled, cancelledResult.Outcome);
        Assert.Equal(0, cancelledAttachment.MemoryReadCalls);
    }

    [Fact]
    public async Task PartialPointerOrValueReadFailsClosed()
    {
        var partialPointer = new FakeAttachment(ExactCandidate, new MemoryPlan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(7)));
        var partialPointerHarness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(partialPointer)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? pointerResult = await RunConfirmedAsync(partialPointerHarness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, pointerResult.Outcome);
        Assert.Equal([8], partialPointer.DestinationLengths);

        var partialValue = new FakeAttachment(ExactCandidate, PointerOnly(0x2000), new MemoryPlan(ValueBytes(12), ReadOnlyMemoryReadResult.Succeeded(3)));
        var partialValueHarness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(partialValue)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? valueResult = await RunConfirmedAsync(partialValueHarness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, valueResult.Outcome);
        Assert.Equal([8, 4], partialValue.DestinationLengths);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public async Task SignedLittleEndianOutOfRangeValuesFailClosed(int invalidValue)
    {
        var attachment = new FakeAttachment(ExactCandidate, Observation(pointer: 0x2000, value: invalidValue));
        var harness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await RunConfirmedAsync(harness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, result.Outcome);
        Assert.Equal([8, 4], attachment.DestinationLengths);
    }

    [Fact]
    public async Task UnexpectedIncrementFailsClosed()
    {
        var attachment = new FakeAttachment(ExactCandidate, Observation(pointer: 0x2000, value: 12).Concat(Observation(pointer: 0x2000, value: 14)).ToArray());
        var harness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await RunConfirmedAsync(harness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Unsupported, result.Outcome);
    }

    [Fact]
    public async Task StableValueUsesExactlyTheThreeMinutePollBudgetThenTimesOut()
    {
        MemoryPlan[] reads = Enumerable.Range(0, 91)
            .SelectMany(_ => Observation(pointer: 0x2000, value: 12))
            .ToArray();
        var attachment = new FakeAttachment(ExactCandidate, reads);
        var delay = new RecordingDelay();
        var harness = CreateHarness(
            new FakeProcessEnumerator(new FakeProcessCandidate()),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)),
            delay);

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await RunConfirmedAsync(harness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.TimedOut, result.Outcome);
        Assert.Equal(90, delay.RequestedDelays.Count);
        Assert.All(delay.RequestedDelays, requested => Assert.Equal(TimeSpan.FromSeconds(2), requested));
        Assert.Equal(182, attachment.MemoryReadCalls);
        Assert.Equal(182, attachment.IdentityQueryCalls);
    }

    [Fact]
    public async Task CancelledReadReturnsOnlyCancelledAndDisposesResources()
    {
        var candidate = new FakeProcessCandidate();
        var attachment = new FakeAttachment(ExactCandidate, new MemoryPlan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Cancelled()));
        var harness = CreateHarness(
            new FakeProcessEnumerator(candidate),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)),
            new RecordingDelay());

        DarkSoulsRemasteredSingleIncrementValidationResult? result = await RunConfirmedAsync(harness);

        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.Cancelled, result.Outcome);
        Assert.True(candidate.IsDisposed);
        Assert.True(attachment.IsDisposed);
    }

    [Fact]
    public void ResultContainsOnlyFixedOutcomeAndNoSensitiveDiagnosticPayload()
    {
        PropertyInfo[] properties = typeof(DarkSoulsRemasteredSingleIncrementValidationResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        PropertyInfo property = Assert.Single(properties);
        Assert.Equal(nameof(DarkSoulsRemasteredSingleIncrementValidationResult.Outcome), property.Name);
        Assert.False(property.CanWrite);
        Assert.DoesNotContain(properties, candidate =>
            candidate.Name.Contains("Address", StringComparison.Ordinal) ||
            candidate.Name.Contains("Bytes", StringComparison.Ordinal) ||
            candidate.Name.Contains("Path", StringComparison.Ordinal) ||
            candidate.Name.Contains("Process", StringComparison.Ordinal) ||
            candidate.PropertyType == typeof(Exception));
        Assert.DoesNotContain("example", JsonSerializer.Serialize(DarkSoulsRemasteredSingleIncrementValidationResult.Unsupported()), StringComparison.Ordinal);
    }

    [Fact]
    public void HarnessSourceContainsNoScanWriteInjectionInputOrProcessControlApi()
    {
        string source = File.ReadAllText(GetHarnessSourcePath());

        Assert.DoesNotContain("Virtual" + "Query", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process." + "Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("process." + "Kill", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Close" + "MainWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Write" + "ProcessMemory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Create" + "RemoteThread", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Send" + "Input", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindows" + "Hook", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File." + "Write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File." + "Delete", source, StringComparison.Ordinal);
    }

    private static async ValueTask<DarkSoulsRemasteredSingleIncrementValidationResult> RunConfirmedAsync(
        DarkSoulsRemasteredSingleIncrementValidationHarness harness)
    {
        DarkSoulsRemasteredSingleIncrementValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredSingleIncrementValidationHarness.RequiredConfirmation,
            CancellationToken.None);
        return Assert.IsType<DarkSoulsRemasteredSingleIncrementValidationResult>(result);
    }

    private static DarkSoulsRemasteredSingleIncrementValidationHarness CreateHarness(
        FakeProcessEnumerator enumerator,
        FakeAttachmentFactory attachmentFactory,
        IValidationDelay delay) => new(
        enumerator,
        attachmentFactory,
        delay);

    private static MemoryPlan[] Observation(ulong pointer, int value) =>
    [
        new MemoryPlan(PointerBytes(pointer), ReadOnlyMemoryReadResult.Succeeded(8)),
        new MemoryPlan(ValueBytes(value), ReadOnlyMemoryReadResult.Succeeded(4)),
    ];

    private static MemoryPlan PointerOnly(ulong pointer) => new(PointerBytes(pointer), ReadOnlyMemoryReadResult.Succeeded(8));

    private static byte[] PointerBytes(ulong pointer)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, pointer);
        return bytes;
    }

    private static byte[] ValueBytes(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static string GetHarnessSourcePath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "tests",
        "SoulsTracker.Infrastructure.Tests",
        "DarkSoulsRemasteredSingleIncrementValidationHarness.cs"));

    private sealed class FakeProcessEnumerator : IDarkSoulsRemasteredProcessEnumerator
    {
        private readonly IReadOnlyList<IDarkSoulsRemasteredProcessCandidate> candidates;

        public FakeProcessEnumerator(params IDarkSoulsRemasteredProcessCandidate[] candidates)
        {
            this.candidates = candidates;
        }

        public int EnumerationCalls { get; private set; }

        public ValueTask<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
        {
            EnumerationCalls++;
            return ValueTask.FromResult(candidates);
        }
    }

    private sealed class FakeProcessCandidate : IDarkSoulsRemasteredProcessCandidate
    {
        public int ProcessId => 1234;

        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAttachmentFactory : IReadOnlyProcessAttachmentFactory
    {
        private readonly ReadOnlyProcessAttachmentResult result;

        public FakeAttachmentFactory(ReadOnlyProcessAttachmentResult result)
        {
            this.result = result;
        }

        public int AttachCalls { get; private set; }

        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            AttachCalls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeAttachment : IReadOnlyProcessAttachment
    {
        private readonly ProcessModuleFileIdentity identity;
        private readonly ReadOnlyMainModuleBaseResult moduleBaseResult;
        private readonly Queue<MemoryPlan> plans;

        public FakeAttachment(ProcessModuleFileIdentity identity, params MemoryPlan[] plans)
            : this(identity, ReadOnlyMainModuleBaseResult.Available(0x1000), plans)
        {
        }

        public FakeAttachment(
            ProcessModuleFileIdentity identity,
            ReadOnlyMainModuleBaseResult moduleBaseResult,
            params MemoryPlan[] plans)
        {
            this.identity = identity;
            this.moduleBaseResult = moduleBaseResult;
            this.plans = new Queue<MemoryPlan>(plans);
        }

        public int IdentityQueryCalls { get; private set; }

        public int ModuleBaseQueryCalls { get; private set; }

        public int MemoryReadCalls { get; private set; }

        public List<int> DestinationLengths { get; } = [];

        public bool IsDisposed { get; private set; }

        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken)
        {
            IdentityQueryCalls++;
            return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Available(identity));
        }

        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken)
        {
            ModuleBaseQueryCalls++;
            return ValueTask.FromResult(moduleBaseResult);
        }

        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(
            nuint address,
            byte[] destination,
            CancellationToken cancellationToken)
        {
            MemoryReadCalls++;
            DestinationLengths.Add(destination.Length);
            MemoryPlan plan = plans.Dequeue();
            Array.Copy(plan.Bytes, destination, Math.Min(plan.Bytes.Length, destination.Length));
            return ValueTask.FromResult(plan.Result);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDelay : IValidationDelay
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            RequestedDelays.Add(delay);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record MemoryPlan(byte[] Bytes, ReadOnlyMemoryReadResult Result);
}
