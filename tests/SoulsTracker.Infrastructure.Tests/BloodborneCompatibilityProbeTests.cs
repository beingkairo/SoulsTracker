using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class BloodborneCompatibilityProbeTests
{
    [Theory]
    [InlineData("bbport.exe")]
    [InlineData("bb-probe.exe")]
    [InlineData("Yharnam-Renamed.exe")]
    public async Task DesktopReaderRecognizesCompatibleGameWithoutDependingOnExecutableName(string executableName)
    {
        var windows = new Windows(new BloodborneWindow(1, 1001, "Bloodborne™"));
        var memory = new Memory(0x900004000) { ExecutableName = executableName };
        // A bogus adjacent candidate must not affect normal tracking: only deaths is read.
        memory.Counters(uint.MaxValue, 1);
        var reader = new BloodborneRuntimeDeathReader(new Enumerator(Array.Empty<IBloodborneProcessCandidate>()), new BloodborneWindowProcessEnumerator(windows), new Factory(memory));
        RuntimeGameReadResult? result = await reader.ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.Synced, result!.Status);
        Assert.Equal(1, result.Observation!.TotalDeaths.Value);
        Assert.Equal("bloodborne", result.Observation.SourceIdentity);
        Assert.Contains(memory.Reads, read => read.At == memory.Manager + 0x88 && read.Length == 4);
        Assert.DoesNotContain(memory.Reads, read => read.At == memory.Manager + 0x84 ||
            read.At == unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress));
        Assert.Equal(0, memory.IdentityQueries);
        Assert.True(memory.Disposed);
    }

    [Fact]
    public async Task SharedReaderPreservesShadFixedAddressWithoutRequiringStructuralFingerprints()
    {
        var candidate = new ProcessCandidate(1001);
        var memory = new Memory(0x400000);
        memory.Data.Clear();
        memory.U32(unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress), 4);
        var windows = new Windows(new BloodborneWindow(1, 1001, "shadPS4 | CUSA03173 - Bloodborne"));
        var reader = new BloodborneRuntimeDeathReader(new Enumerator([candidate]), new BloodborneWindowProcessEnumerator(windows), new Factory(memory));
        RuntimeGameReadResult? result = await reader.ReadAsync(default);
        Assert.Equal(4, result!.Observation!.TotalDeaths.Value);
        Assert.Equal(1, candidate.DisposeCalls);
        Assert.Equal([(unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress), 4)], memory.Reads);
        Assert.Equal(0, memory.QueryCalls);
        Assert.True(memory.Disposed);
    }

    [Fact]
    public async Task DuplicateWindowsDoNotDoubleCountAndUnmatchedLauncherIsIgnored()
    {
        var windows = new Windows(new BloodborneWindow(1, 1001, "Bloodborne Launcher"),
            new BloodborneWindow(2, 1002, "Bloodborne™"), new BloodborneWindow(3, 1002, "Bloodborne"));
        var launcher = new Memory(0x800000000);
        launcher.Data.Clear();
        var game = new Memory(0x900000000);
        game.Counters(0, 8);
        var factory = new Factory(launcher, game);
        var reader = new BloodborneRuntimeDeathReader(new Enumerator(Array.Empty<IBloodborneProcessCandidate>()), new BloodborneWindowProcessEnumerator(windows), factory);
        Assert.Equal(8, (await reader.ReadAsync(default))!.Observation!.TotalDeaths.Value);
        Assert.Equal(2, factory.Calls);
        Assert.True(launcher.Disposed && game.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleIdentifiedRuntimesIncludingLegacyAreRejected(bool includeLegacy)
    {
        var legacy = new ProcessCandidate(1001);
        var first = new Memory(0x800000000);
        var second = new Memory(0x900000000);
        var reader = new BloodborneRuntimeDeathReader(new Enumerator(includeLegacy ? new IBloodborneProcessCandidate[] { legacy } : Array.Empty<IBloodborneProcessCandidate>()),
            new BloodborneWindowProcessEnumerator(new Windows(new BloodborneWindow(1, 1002, "Bloodborne"),
                new BloodborneWindow(2, 1003, "Bloodborne"))), new Factory(first, second));
        Assert.Null(await reader.ReadAsync(default));
        Assert.True(first.Disposed);
        if (!includeLegacy) Assert.True(second.Disposed);
        if (includeLegacy) Assert.Equal(1, legacy.DisposeCalls);
    }

    [Fact]
    public async Task MultipleShadProcessesStillRejectBeforeAnyPcMemoryAttachment()
    {
        var first = new ProcessCandidate(1);
        var second = new ProcessCandidate(2);
        var factory = new Factory();
        var reader = new BloodborneRuntimeDeathReader(new Enumerator([first, second]),
            new BloodborneWindowProcessEnumerator(new Windows(new BloodborneWindow(1, 1002, "Bloodborne"))), factory);
        Assert.Null(await reader.ReadAsync(default));
        Assert.Equal(0, factory.Calls);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task SharedReaderHandlesLoadingRestartReusedPidZeroRepeatedAndLowerTotalsAsSnapshots()
    {
        var windows = new Windows();
        Memory Next(uint deaths, ulong image)
        {
            var memory = new Memory(image);
            memory.Counters(0, deaths);
            return memory;
        }
        var zero = Next(0, 0x800000000);
        var one = Next(1, 0x800000000);
        var repeated = Next(1, 0x800000000);
        var loading = Next(0, 0x800000000);
        loading.Data[loading.Image + 0x556286b][0] = 1;
        var restarted = Next(1, 0xb00000000);
        var lower = Next(0, 0xb00000000);
        var factory = new Factory(zero, one, repeated, loading, restarted, lower);
        var reader = new BloodborneRuntimeDeathReader(new Enumerator([], [], [], [], [], [], [], []),
            new BloodborneWindowProcessEnumerator(windows), factory);
        Assert.Null(await reader.ReadAsync(default)); // game starts after the tracker
        windows.Items = [new(1, 1001, "Bloodborne")];
        Assert.True((await reader.ReadAsync(default))!.HasNoRecordedDeaths);
        Assert.Equal(1, (await reader.ReadAsync(default))!.Observation!.TotalDeaths.Value);
        Assert.Equal(1, (await reader.ReadAsync(default))!.Observation!.TotalDeaths.Value);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, (await reader.ReadAsync(default))!.Status);
        windows.Items = [];
        Assert.Null(await reader.ReadAsync(default));
        windows.Items = [new(2, 1001, "Bloodborne")]; // reused PID, new process/window/image
        Assert.Equal(1, (await reader.ReadAsync(default))!.Observation!.TotalDeaths.Value);
        Assert.True((await reader.ReadAsync(default))!.HasNoRecordedDeaths);
        Assert.All(new[] { zero, one, repeated, loading, restarted, lower }, memory => Assert.True(memory.Disposed));
    }

    [Fact]
    public async Task SharedReaderRejectsDeniedOrIncompleteCandidatesInsteadOfSelectingAnotherInstance()
    {
        var reader = new BloodborneRuntimeDeathReader(new Enumerator(Array.Empty<IBloodborneProcessCandidate>()),
            new BloodborneWindowProcessEnumerator(new Windows(new BloodborneWindow(1, 1001, "Bloodborne"))), new DeniedFactory(5));
        Assert.Null(await reader.ReadAsync(default));
        var incomplete = new Memory(0x800000000) { Failure = "region" };
        reader = new(new Enumerator(Array.Empty<IBloodborneProcessCandidate>()), new BloodborneWindowProcessEnumerator(new Windows(new BloodborneWindow(1, 1001, "Bloodborne"))),
            new Factory(incomplete));
        Assert.Null(await reader.ReadAsync(default));
        Assert.True(incomplete.Disposed);
    }

    [Fact]
    public async Task SharedReaderRejectsCharacterChangingDuringTheVerifiedDeathRead()
    {
        var memory = new Memory(0x800000000);
        memory.AfterCounterRead = () => memory.U64(memory.Manager + 8, 0);
        var reader = new BloodborneRuntimeDeathReader(new Enumerator(Array.Empty<IBloodborneProcessCandidate>()),
            new BloodborneWindowProcessEnumerator(new Windows(new BloodborneWindow(1, 1001, "Bloodborne"))), new Factory(memory));
        RuntimeGameReadResult? result = await reader.ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, result!.Status);
        Assert.Null(result.Observation);
        Assert.True(memory.Disposed);
    }

    [Theory]
    [InlineData(5, BloodborneCompatibilityStatus.AttachmentDenied)]
    [InlineData(87, BloodborneCompatibilityStatus.AttachmentUnavailable)]
    public async Task AttachmentRejectionIsDistinguishedBeforeAnyMemoryWorkAndLegacyReaderBehaviorIsUnchanged(
        int nativeError, BloodborneCompatibilityStatus expected)
    {
        var factory = new DeniedFactory(nativeError);
        var candidate = new Candidate();
        BloodborneCompatibilitySample sample = await new BloodborneCompatibilityProbe(
            new Enumerator([candidate]), factory, BloodborneCompatibilityRuntime.ShadPs4).SampleAsync(default);
        Assert.Equal(expected, sample.Status);
        Assert.Equal(BloodborneLegacyReadStatus.NotAttempted, sample.LegacyRead);
        Assert.Null(sample.LegacyValue);
        Assert.True(candidate.Disposed);

        Assert.Null(await new BloodborneActiveCharacterDeathReader(
            new Enumerator([new Candidate()]), factory).ReadAsync(default));
        Assert.Equal(2, factory.Calls);
        Assert.Equal(ReadOnlyProcessAttachmentOutcome.Unavailable, ReadOnlyProcessAttachmentResult.Unavailable(nativeError).Outcome);
    }

    [Fact]
    public async Task ReadableOriginalAddressSurvivesAnUnmatchedGuestImageAndAgreesWithTheReleasedReader()
    {
        var memory = new Memory(0x400000);
        memory.Data[memory.Image + 0x1f1e3d0][0] ^= 1;
        memory.U32(unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress), 42);
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default, legacyGuest: true);
        Assert.Equal(BloodborneCompatibilityStatus.UnmatchedImage, sample.Status);
        Assert.Equal(BloodborneLegacyReadStatus.Readable, sample.LegacyRead);
        Assert.Equal(42u, sample.LegacyValue);
        Assert.Null(sample.Deaths);
        Assert.Equal(BloodborneLegacyCounterRelation.Neither, sample.LegacyRelation);
        Assert.Equal(unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress), memory.Reads[0].At);
        Assert.Equal(4, memory.Reads[0].Length);

        RuntimeGameReadResult? original = await new BloodborneActiveCharacterDeathReader(
            new Enumerator([new Candidate()]), new Factory(memory)).ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.Synced, original!.Status);
        Assert.Equal(42, original.Observation!.TotalDeaths.Value);
    }

    [Theory]
    [InlineData(false, 0u, BloodborneLegacyReadStatus.Readable)]
    [InlineData(false, 1_000_001u, BloodborneLegacyReadStatus.Implausible)]
    [InlineData(true, 12u, BloodborneLegacyReadStatus.Unreadable)]
    public async Task IndependentLegacyCheckDistinguishesZeroImplausibleAndPartialValues(bool partial, uint value,
        BloodborneLegacyReadStatus expected)
    {
        var memory = new Memory(0x400000) { Failure = partial ? "legacy" : null };
        memory.U32(unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress), value);
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default, legacyGuest: true);
        Assert.Equal(expected, sample.LegacyRead);
        Assert.Equal(partial ? null : (uint?)value, sample.LegacyValue);
        // Equal numbers are still not proof that the fixed address belongs to this manager.
        Assert.Equal(BloodborneLegacyCounterRelation.Neither, sample.LegacyRelation);
    }

    [Theory]
    [InlineData(BloodborneCompatibilityRuntime.Bbport, 0x800004000UL)]
    [InlineData(BloodborneCompatibilityRuntime.LanternEngine, 0x900000000UL)]
    public async Task BothTargetsUseTheSameRelocatedGuestLayoutAndReadCumulativeUpdates(
        BloodborneCompatibilityRuntime runtime, ulong image)
    {
        Assert.Contains("bb-probe", BloodbornePortProcessEnumerator.NamesFor(runtime));
        var memory = new Memory(image);
        foreach (uint value in new uint[] { 0, 21, 21, 22, 3 })
        {
            memory.Counters(2, value);
            BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default);
            Assert.Equal(BloodborneCompatibilityStatus.Sampled, sample.Status);
            Assert.Equal(value, sample.Deaths);
            Assert.Equal(2u, sample.TrueDeaths);
            Assert.Null(sample.LegacyValue);
            Assert.Equal(BloodborneLegacyCounterRelation.Neither, sample.LegacyRelation);
        }
    }

    [Theory]
    [InlineData(BloodborneCompatibilityRuntime.Bbport, 0x800004000UL)]
    [InlineData(BloodborneCompatibilityRuntime.LanternEngine, 0x900000000UL)]
    public async Task PreparedImageFingerprintsUseSourceRvasRatherThanDisassemblerAddresses(
        BloodborneCompatibilityRuntime runtime, ulong image)
    {
        var memory = new Memory(image);
        var probe = new BloodborneCompatibilityProbe(new Enumerator([new Candidate()]), new Factory(memory), runtime);
        Assert.Equal(BloodborneCompatibilityStatus.Sampled, (await probe.SampleAsync(default)).Status);
        // Independently calculated from Lantern Matches(bn): bn - 0x400000.
        Assert.Contains(memory.Reads, read => read.At == memory.Image + 0x1f1e3d0);
        Assert.Contains(memory.Reads, read => read.At == memory.Image + 0x1f1e7e0);
        Assert.Contains(memory.Reads, read => read.At == memory.Image + 0x14d9f60);
        Assert.DoesNotContain(memory.Reads, read => read.At == memory.Image + 0x231e3d0 ||
            read.At == memory.Image + 0x231e7e0 || read.At == memory.Image + 0x18d9f60);
        Assert.DoesNotContain(memory.Reads, read => read.At == unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress));
    }

    [Fact]
    public async Task BytesAtUnconvertedOffsetsCannotValidateAPreparedImage()
    {
        var memory = new Memory(0x800000000);
        foreach (nuint rva in new nuint[] { 0x1f1e3d0, 0x1f1e7e0, 0x14d9f60 })
        {
            memory.Data[memory.Image + rva + 0x400000] = memory.Data[memory.Image + rva];
            memory.Data.Remove(memory.Image + rva);
        }
        Assert.Equal(BloodborneCompatibilityStatus.UnmatchedImage,
            (await BloodborneGuestMemoryProbe.SampleAsync(memory, default)).Status);
        Assert.DoesNotContain(memory.Reads, read => read.At == memory.Manager + 0x84);
    }

    [Fact]
    public void DiscoveryIncludesActualRuntimeNamesAndExcludesLaunchersAndOtherPorts()
    {
        Assert.Equal(["bbport", "bb-probe"], BloodbornePortProcessEnumerator.NamesFor(BloodborneCompatibilityRuntime.Bbport));
        Assert.Equal(["bb-probe"], BloodbornePortProcessEnumerator.NamesFor(BloodborneCompatibilityRuntime.LanternEngine));
        Assert.Throws<ArgumentOutOfRangeException>(() => BloodbornePortProcessEnumerator.NamesFor((BloodborneCompatibilityRuntime)99));
    }

    [Fact]
    public async Task LegacyBindingChecksAddressIdentityAndAcceptsShadMappedBacking()
    {
        var memory = new Memory(0x400000, BloodborneGuestMemoryProbe.LegacyAddress - 0x88) { Failure = "mapped" };
        memory.Counters(7, 7);
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default, legacyGuest: true);
        Assert.Equal(BloodborneLegacyCounterRelation.Deaths, sample.LegacyRelation);
        Assert.Equal(7u, sample.LegacyValue);

        var unrelated = new Memory(0x400000);
        unrelated.Counters(7, 7);
        sample = await BloodborneGuestMemoryProbe.SampleAsync(unrelated, default, legacyGuest: true);
        Assert.Equal(BloodborneLegacyCounterRelation.Neither, sample.LegacyRelation);
        Assert.Null(sample.LegacyValue);
    }

    [Fact]
    public async Task MatchingSmallNumbersInUnrelatedMemoryDoNotValidateTheImage()
    {
        var memory = new Memory(0x800000000);
        memory.Data[(nuint)0x1f1e3d0 + memory.Image][0] ^= 1;
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default);
        Assert.Equal(BloodborneCompatibilityStatus.UnmatchedImage, sample.Status);
        Assert.DoesNotContain(memory.Reads, read => read.At == memory.Manager + 0x84);
        Assert.DoesNotContain(memory.Reads, read => read.At == unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress));
    }

    [Fact]
    public async Task MultipleMatchingImagesAreRejectedBeforeReadingCounters()
    {
        var memory = new Memory(0x800000000);
        memory.AddImage(0xa00000000);
        Assert.Equal(BloodborneCompatibilityStatus.AmbiguousImages,
            (await BloodborneGuestMemoryProbe.SampleAsync(memory, default)).Status);
        Assert.DoesNotContain(memory.Reads, read => read.At == memory.Manager + 0x84);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("record")]
    [InlineData("level")]
    [InlineData("loading")]
    public async Task StartupTitleLoadingAndUnloadedCharacterCannotCreateAZero(string unavailable)
    {
        var memory = new Memory(0x800000000);
        switch (unavailable)
        {
            case "manager": memory.U64(memory.Image + 0x553b130, 0); break;
            case "record": memory.U64(memory.Manager + 8, 0); break;
            case "level": memory.U32(memory.Record + 0x90, 0); break;
            case "loading": memory.Data[memory.Image + 0x556286b][0] = 1; break;
        }
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default);
        Assert.Equal(BloodborneCompatibilityStatus.WaitingForCharacter, sample.Status);
        Assert.Null(sample.Deaths);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("record")]
    [InlineData("loading")]
    [InlineData("image")]
    public async Task CharacterChangeUnloadOrImageChangeDuringReadDiscardsTheSnapshot(string changed)
    {
        var memory = new Memory(0x800000000);
        memory.AfterCounterRead = () =>
        {
            switch (changed)
            {
                case "manager": memory.U64(memory.Image + 0x553b130, 0); break;
                case "record": memory.U64(memory.Manager + 8, (ulong)memory.Record + 0x1000); break;
                case "loading": memory.Data[memory.Image + 0x556286b][0] = 1; break;
                case "image": memory.Data[memory.Image + 0x14d9f60][0] ^= 1; break;
            }
        };
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default);
        Assert.Equal(BloodborneCompatibilityStatus.UnstableRead, sample.Status);
        Assert.Null(sample.Deaths);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("counter")]
    [InlineData("region")]
    [InlineData("overflow")]
    [InlineData("guard")]
    [InlineData("mapped")]
    public async Task PartialReadsMalformedMapsAndNonPrivateOrGuardedPagesFailClosed(string failure)
    {
        var memory = new Memory(0x800000000) { Failure = failure };
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, default);
        Assert.NotEqual(BloodborneCompatibilityStatus.Sampled, sample.Status);
        Assert.Null(sample.Deaths);
    }

    [Theory]
    [InlineData(1_000_001u, 0u)]
    [InlineData(0u, uint.MaxValue)]
    public async Task ImplausibleCountersRemainUnavailable(uint deaths, uint trueDeaths)
    {
        var memory = new Memory(0x800000000);
        memory.Counters(trueDeaths, deaths);
        Assert.Equal(BloodborneCompatibilityStatus.CounterUnavailable,
            (await BloodborneGuestMemoryProbe.SampleAsync(memory, default)).Status);
    }

    [Fact]
    public async Task RegionBudgetCannotProduceASampleFromAnIncompleteMap()
    {
        var memory = new Memory(0x800000000) { Failure = "budget" };
        Assert.Equal(BloodborneCompatibilityStatus.Unavailable,
            (await BloodborneGuestMemoryProbe.SampleAsync(memory, default)).Status);
        Assert.Equal(65_536, memory.QueryCalls);
        Assert.Empty(memory.Reads);
    }

    [Fact]
    public async Task CancellationDuringTheCounterReadDiscardsTheSample()
    {
        using var stopping = new CancellationTokenSource();
        var memory = new Memory(0x800000000) { AfterCounterRead = stopping.Cancel };
        BloodborneCompatibilitySample sample = await BloodborneGuestMemoryProbe.SampleAsync(memory, stopping.Token);
        Assert.NotEqual(BloodborneCompatibilityStatus.Sampled, sample.Status);
        Assert.Null(sample.Deaths);
    }

    [Fact]
    public async Task ShutdownRestartAndReusedPidReacquireTheImageAndDisposeEveryAttachment()
    {
        var first = new Memory(0x800000000);
        var restarted = new Memory(0xb00000000);
        first.Counters(0, 9);
        restarted.Counters(0, 10);
        var candidate1 = new Candidate();
        var candidate2 = new Candidate(); // deliberately the same PID
        var enumerator = new Enumerator([candidate1], [], [candidate2]);
        var probe = new BloodborneCompatibilityProbe(enumerator, new Factory(first, restarted));
        Assert.Equal(9u, (await probe.SampleAsync(default)).Deaths);
        Assert.Equal(BloodborneCompatibilityStatus.Unavailable, (await probe.SampleAsync(default)).Status);
        Assert.Equal(10u, (await probe.SampleAsync(default)).Deaths);
        Assert.True(first.Disposed && restarted.Disposed && candidate1.Disposed && candidate2.Disposed);
    }

    [Fact]
    public async Task WrongTitleAmbiguousProcessesAndCancellationNeverAttach()
    {
        foreach (Candidate[] candidates in new[] { new[] { new Candidate(false) }, new[] { new Candidate(), new Candidate() } })
        {
            var factory = new Factory();
            await new BloodborneCompatibilityProbe(new Enumerator(candidates), factory).SampleAsync(default);
            Assert.Equal(0, factory.Calls);
            Assert.All(candidates, candidate => Assert.True(candidate.Disposed));
        }
        var cancelledFactory = new Factory();
        var probe = new BloodborneCompatibilityProbe(new Enumerator(), cancelledFactory);
        Assert.Equal(BloodborneCompatibilityStatus.Unavailable, (await probe.SampleAsync(new CancellationToken(true))).Status);
        Assert.Equal(0, cancelledFactory.Calls);
    }

    [Fact]
    public async Task NativeMemoryQueryReadsTheCurrentProcessWithoutExtraAccessRights()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return;
        using Process current = Process.GetCurrentProcess();
        ReadOnlyProcessAttachmentResult opened = await new WindowsReadOnlyProcessAttachmentFactory().AttachAsync(current.Id, default);
        await using IReadOnlyProcessAttachment memory = Assert.IsAssignableFrom<IReadOnlyProcessAttachment>(opened.Attachment);
        var map = Assert.IsAssignableFrom<IReadOnlyProcessMemoryMap>(memory);
        nint scratch = Marshal.AllocHGlobal(16);
        try
        {
            ReadOnlyProcessMemoryRegion? region = await map.QueryRegionAsync((nuint)scratch, default);
            Assert.NotNull(region);
            Assert.True(region.Value.Start <= (nuint)scratch && region.Value.Start + region.Value.Size > (nuint)scratch);
            Assert.Equal(0x1000u, region.Value.State);
            Assert.Null(await map.QueryRegionAsync((nuint)scratch, new CancellationToken(true)));
        }
        finally { Marshal.FreeHGlobal(scratch); }
        Assert.Equal(BloodborneCompatibilityStatus.UnmatchedImage,
            (await BloodborneGuestMemoryProbe.SampleAsync(memory, default)).Status);
        await memory.DisposeAsync();
        Assert.Null(await map.QueryRegionAsync(0, default));
    }

    private sealed class Enumerator(params IReadOnlyList<IBloodborneProcessCandidate>[] sequences) : IBloodborneProcessEnumerator
    {
        private readonly Queue<IReadOnlyList<IBloodborneProcessCandidate>> reads = new(sequences);
        public ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(reads.Dequeue());
    }

    private sealed class Candidate(bool expected = true) : IBloodborneProcessCandidate
    {
        public int ProcessId => 42;
        public bool Disposed { get; private set; }
        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(expected);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ProcessCandidate(int id) : IBloodborneProcessCandidate
    {
        public int ProcessId => id;
        public int DisposeCalls { get; private set; }
        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class Windows(params BloodborneWindow[] windows) : IBloodborneWindowSource
    {
        public IReadOnlyList<BloodborneWindow> Items { get; set; } = windows;
        public IReadOnlyList<BloodborneWindow> Enumerate(CancellationToken cancellationToken) => Items;
        public BloodborneWindow? Read(nint handle) => Items.FirstOrDefault(window => window.Handle == handle);
    }

    private sealed class Factory(params Memory[] memories) : IReadOnlyProcessAttachmentFactory
    {
        private readonly Queue<Memory> reads = new(memories);
        public int Calls { get; private set; }
        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Attached(reads.Dequeue()));
        }
    }

    private sealed class DeniedFactory(int nativeError) : IReadOnlyProcessAttachmentFactory
    {
        public int Calls { get; private set; }
        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Unavailable(nativeError));
        }
    }

    private sealed class Memory : IReadOnlyProcessAttachment, IReadOnlyProcessMemoryMap
    {
        public nuint Image { get; }
        public nuint Manager { get; }
        public nuint Record { get; } = unchecked((nuint)0x1000200000);
        public Dictionary<nuint, byte[]> Data { get; } = [];
        public List<(nuint At, int Length)> Reads { get; } = [];
        private readonly SortedSet<nuint> images = [];
        public Action? AfterCounterRead { get; set; }
        public string? Failure { get; init; }
        public bool Disposed { get; private set; }
        public int QueryCalls { get; private set; }
        public string ExecutableName { get; init; } = "unrecognized-filename.exe";
        public int IdentityQueries { get; private set; }

        public Memory(ulong image, ulong manager = 0x1000100000)
        {
            Image = (nuint)image;
            Manager = (nuint)manager;
            AddImage(image);
            U64(Image + 0x553b130, manager);
            U64(Manager + 8, (ulong)Record);
            U32(Record + 0x90, 12);
            Data[Image + 0x556286b] = [0];
            Counters(0, 0);
        }

        public void AddImage(ulong image)
        {
            nuint at = (nuint)image;
            images.Add(at);
            // Independent 1.09 RVAs from Lantern's Matches(bn), not its absolute BN addresses.
            Data[at + 0x1f1e3d0] = Convert.FromHexString("554889E5415741564154534189F44989FE31C94585E40F88F2000000");
            Data[at + 0x1f1e7e0] = Convert.FromHexString("554889E5534883EC28488B1DF0534C03488B03488945F0488B4F08B8630000004885C90F848C0000008B51420FB74146");
            Data[at + 0x14d9f60] = Convert.FromHexString("8B0683F8FF0F84A600000089C14869C9153FEF0348C1E920");
        }

        public void U64(nuint at, ulong value) { byte[] bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); Data[at] = bytes; }
        public void U32(nuint at, uint value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Data[at] = bytes; }
        public void Counters(uint trueDeaths, uint deaths)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, trueDeaths);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), deaths);
            Data[Manager + 0x84] = bytes;
        }

        public ValueTask<ReadOnlyProcessMemoryRegion?> QueryRegionAsync(nuint at, CancellationToken cancellationToken)
        {
            QueryCalls++;
            if (Failure == "budget") return ValueTask.FromResult<ReadOnlyProcessMemoryRegion?>(new(at, 0, 0x1000, 0, 0, 0));
            if (Failure == "region") return ValueTask.FromResult<ReadOnlyProcessMemoryRegion?>(null);
            if (Failure == "overflow") return ValueTask.FromResult<ReadOnlyProcessMemoryRegion?>(new(at, 0, nuint.MaxValue, 0, 0, 0));
            foreach (nuint image in images)
            {
                if (at < image) return ValueTask.FromResult<ReadOnlyProcessMemoryRegion?>(new(at, 0, image - at, 0, 0, 0));
                if (at == image) return ValueTask.FromResult<ReadOnlyProcessMemoryRegion?>(new(at, at, 0x6000000, 0x1000,
                    Failure == "guard" ? 0x120u : 0x20u, Failure == "mapped" ? 0x40000u : 0x20000u));
            }
            return ValueTask.FromResult<ReadOnlyProcessMemoryRegion?>(new(at, 0, unchecked((nuint)(1UL << 40)) - at, 0, 0, 0));
        }

        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(nuint address, byte[] destination, CancellationToken cancellationToken)
        {
            Reads.Add((address, destination.Length));
            if (Failure == "signature" && address == Image + 0x1f1e3d0 || Failure == "counter" && (address == Manager + 0x84 || address == Manager + 0x88) ||
                Failure == "legacy" && address == unchecked((nuint)BloodborneGuestMemoryProbe.LegacyAddress))
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Succeeded((nuint)destination.Length - 1));
            foreach ((nuint start, byte[] data) in Data)
            {
                if (address < start || address - start > (nuint)data.Length || (nuint)destination.Length > (nuint)data.Length - (address - start)) continue;
                data.AsSpan((int)(address - start), destination.Length).CopyTo(destination);
                if (address == Manager + 0x84 || address == Manager + 0x88) AfterCounterRead?.Invoke();
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Succeeded((nuint)destination.Length));
            }
            return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
        }
        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken)
        {
            IdentityQueries++;
            return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Available(new(ExecutableName, "unknown", "unknown", "unknown")));
        }
        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
