using System.Buffers.Binary;
using System.Text;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HollowKnightReaderTests
{
    [Theory]
    [InlineData("hollow_knight", true)]
    [InlineData("Hollow_Knight", true)]
    [InlineData("Hollow Knight", true)]
    [InlineData("Hollow Knight Silksong", false)]
    [InlineData("hollow_knight_modded", false)]
    [InlineData("Silksong", false)]
    public void OnlyOriginalExecutableNamesMatch(string name, bool expected) =>
        Assert.Equal(expected, HollowKnightMemory.IsOriginalProcessName(name));

    [Theory]
    [InlineData("1.5.68.11808", false)]
    [InlineData("1.5.78.11833", false)]
    [InlineData("1.5.12459", true)]
    public async Task KnownLayoutsReadCoherentHealthAndCacheSingleRoot(string version, bool modern)
    {
        var memory = new Memory(version, modern);
        nuint root = await HollowKnightMemory.FindRootAsync(memory, default);
        Assert.Equal((nuint)0x20000, root);
        HollowKnightSample? sample = await HollowKnightMemory.SampleAsync(memory, root, default);
        Assert.Equal(new HollowKnightSample(0x30000, 0x40000, 0x50000, 4, 5), sample);
        int scans = memory.MapQueries;
        await HollowKnightMemory.SampleAsync(memory, root, default);
        Assert.Equal(scans, memory.MapQueries);
        Assert.InRange(memory.LargestSampleRead, 4, 64);
    }

    [Theory]
    [InlineData("1.5.99999")]
    [InlineData("1.4.3.2")]
    [InlineData("1.5.12460")]
    public async Task UnknownVersionsFailClosed(string version)
    {
        var memory = new Memory(version, true);
        Assert.Null(await HollowKnightMemory.SampleAsync(memory, 0x20000, default));
        Assert.Equal((nuint)0, await HollowKnightMemory.FindRootAsync(memory, default));
    }

    [Theory]
    [InlineData("health")]
    [InlineData("torn-health")]
    [InlineData("torn-player")]
    [InlineData("torn-hero")]
    [InlineData("destroyed-hero")]
    [InlineData("invalid-health")]
    [InlineData("invalid-state")]
    public async Task InvalidOrPartialSamplesNeverBecomeZero(string condition)
    {
        var memory = new Memory();
        switch (condition)
        {
            case "health": memory.Unreadable = 0x40198; break;
            case "torn-health": memory.Tear = 0x40198; break;
            case "torn-player": memory.Tear = 0x300d0; break;
            case "torn-hero": memory.Tear = 0x50010; break;
            case "destroyed-hero": memory.Put64(0x50010, 0); break;
            case "invalid-health": memory.Health(-1); break;
            case "invalid-state": memory.State(9); break;
        }
        Assert.Null(await HollowKnightMemory.SampleAsync(memory, 0x20000, default));
    }

    [Fact]
    public async Task AmbiguousOrAbsentSingletonNeverAttaches()
    {
        var memory = new Memory();
        memory.AddSignature(80, 0x21000);
        memory.Put64(0x21000, 0x30000);
        Assert.Equal((nuint)0, await HollowKnightMemory.FindRootAsync(memory, default));
        memory.Code.AsSpan().Clear();
        Assert.Equal((nuint)0, await HollowKnightMemory.FindRootAsync(memory, default));
    }

    [Fact]
    public async Task AlternateAbsolute32InstructionShapeResolvesSameSingleton()
    {
        var memory = new Memory();
        memory.Code.AsSpan().Clear();
        byte[] signature = Convert.FromHexString("488BCE49BB010203040506070841FFD3E963000000488B142500000200");
        signature.CopyTo(memory.Code, 0);
        Assert.Equal((nuint)0x20000, await HollowKnightMemory.FindRootAsync(memory, default));
    }

    [Fact]
    public void TerminalDeathCountsBeforeMenuWithoutRespawnAndReopeningDeadDoesNotRecount()
    {
        var detector = new HollowKnightDeathDetector();
        detector.Observe(Sample(5)); detector.Observe(Sample(5));
        detector.Observe(Sample(0));
        Guid id = Assert.IsType<Guid>(detector.PendingEvent);
        detector.Observe(Sample(0) with { GameState = 1 });
        Assert.Equal(id, detector.PendingEvent);
        detector.Acknowledge(id);
        detector.ResetContinuity();
        detector.Observe(Sample(0));
        Assert.Null(detector.PendingEvent);
    }

    [Fact]
    public async Task LaunchDeathRespawnShutdownAndPidReuseResetContinuityAndDisposeAttachments()
    {
        var clock = new Clock();
        var memory = new Memory();
        var factory = new Factory(memory);
        IReadOnlyList<HollowKnightProcessIdentity> processes = [];
        await using var reader = new HollowKnightRuntimeReader(() => processes, factory, clock);
        Assert.Equal(RuntimeGameReaderStatus.Unavailable, (await reader.ReadAsync(default)).Status);
        processes = [new(42, DateTime.UnixEpoch)];
        clock.Advance(1000);
        Assert.Equal(RuntimeGameReaderStatus.Synced, (await reader.ReadAsync(default)).Status);
        clock.Advance(100);
        await reader.ReadAsync(default);
        memory.Health(0);
        clock.Advance(100);
        await reader.ReadAsync(default);
        Guid id = Assert.IsType<Guid>(reader.PendingEvent);
        await reader.ReadAsync(default);
        Assert.Equal(id, reader.PendingEvent);
        reader.Acknowledge(id);
        await reader.ReadAsync(default);
        Assert.Null(reader.PendingEvent);
        memory.State(2);
        memory.Health(5);
        await reader.ReadAsync(default);
        memory.State(4);
        await reader.ReadAsync(default);
        clock.Advance(100);
        await reader.ReadAsync(default);
        processes = [];
        clock.Advance(1000);
        await reader.ReadAsync(default);
        Assert.True(memory.Disposed);
        processes = [new(42, DateTime.UnixEpoch.AddMinutes(1))];
        memory.Health(0);
        clock.Advance(1000);
        await reader.ReadAsync(default);
        Assert.Null(reader.PendingEvent);
        Assert.Equal(2, factory.Attached);
    }

    [Fact]
    public async Task MultipleProcessesAndDeniedAttachmentAreUnavailableAndRetried()
    {
        var clock = new Clock();
        var memory = new Memory();
        var factory = new Factory(memory) { Denied = true };
        IReadOnlyList<HollowKnightProcessIdentity> processes = [new(1, DateTime.UnixEpoch), new(2, DateTime.UnixEpoch)];
        await using var reader = new HollowKnightRuntimeReader(() => processes, factory, clock);
        Assert.Equal(RuntimeGameReaderStatus.Unavailable, (await reader.ReadAsync(default)).Status);
        Assert.Equal(0, factory.Attached);
        processes = [processes[0]];
        clock.Advance(1000);
        Assert.Equal(RuntimeGameReaderStatus.Unavailable, (await reader.ReadAsync(default)).Status);
        factory.Denied = false;
        clock.Advance(1000);
        Assert.Equal(RuntimeGameReaderStatus.Synced, (await reader.ReadAsync(default)).Status);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(canceled.Token));
    }

    [Fact]
    public void DeathRemainsLatchedUntilTwoHealthySamplesAndPendingEventSurvivesGaps()
    {
        var clock = new Clock();
        var detector = new HollowKnightDeathDetector(clock);
        detector.Observe(Sample(0)); // Attach during death.
        Assert.Null(detector.PendingEvent);
        detector.Observe(Sample(5));
        clock.Advance(100);
        detector.Observe(Sample(4)); // Ordinary damage, still alive.
        detector.Observe(Sample(0)); // Dream/Godhome zero health needs no dead flag.
        Guid first = Assert.IsType<Guid>(detector.PendingEvent);
        detector.Observe(null);
        detector.Observe(Sample(0));
        Assert.Equal(first, detector.PendingEvent);
        detector.Acknowledge(first);
        detector.Observe(Sample(0));
        detector.Observe(Sample(5)); // A single healthy respawn blip must not rearm.
        detector.Observe(Sample(0));
        Assert.Null(detector.PendingEvent);
        detector.Observe(Sample(5));
        clock.Advance(100);
        detector.Observe(Sample(5));
        detector.Observe(Sample(0));
        Assert.NotEqual(first, Assert.IsType<Guid>(detector.PendingEvent));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void NonGameplayZeroNeverCounts(int state)
    {
        var detector = new HollowKnightDeathDetector();
        detector.Observe(Sample(5)); detector.Observe(Sample(5));
        detector.Observe(Sample(0) with { GameState = state });
        detector.Observe(Sample(0));
        Assert.Null(detector.PendingEvent);
    }

    [Theory]
    [InlineData("player")]
    [InlineData("hero")]
    [InlineData("manager")]
    [InlineData("gap")]
    [InlineData("reset")]
    public void ChangedIdentityOrContinuityNeverCounts(string change)
    {
        var clock = new Clock();
        var detector = new HollowKnightDeathDetector(clock);
        detector.Observe(Sample(5)); detector.Observe(Sample(5));
        HollowKnightSample zero = Sample(0);
        switch (change)
        {
            case "player": zero = zero with { Player = 0x80000 }; break;
            case "hero": zero = zero with { Hero = 0x80000 }; break;
            case "manager": zero = zero with { Manager = 0x80000 }; break;
            case "gap": clock.Advance(501); break;
            case "reset": detector.ResetContinuity(); break;
        }
        detector.Observe(zero);
        Assert.Null(detector.PendingEvent);
    }

    private static HollowKnightSample Sample(int health) => new(0x30000, 0x40000, 0x50000, 4, health);

    private sealed class Clock : TimeProvider
    {
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds);
        internal void Advance(int value) => milliseconds += value;
    }

    private sealed class Factory(Memory memory) : IReadOnlyProcessAttachmentFactory
    {
        internal int Attached { get; private set; }
        internal bool Denied { get; set; }
        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            Attached++;
            return ValueTask.FromResult(Denied ? ReadOnlyProcessAttachmentResult.Unavailable() : ReadOnlyProcessAttachmentResult.Attached(memory));
        }
    }

    private sealed class Memory : IReadOnlyProcessAttachment, IReadOnlyProcessMemoryMap
    {
        private readonly Dictionary<nuint, byte[]> values = [];
        private readonly bool modern;
        private int tornReads;
        internal byte[] Code { get; } = new byte[128];
        internal nuint Unreadable { get; set; }
        internal nuint Tear { get; set; }
        internal bool Disposed { get; private set; }
        internal int MapQueries { get; private set; }
        internal int LargestSampleRead { get; private set; }

        internal Memory(string version = "1.5.12459", bool modern = true)
        {
            this.modern = modern;
            AddSignature(0, 0x20000);
            Put64(0x20000, 0x30000);
            Put64(0x30000 + (modern ? (nuint)0xd0 : 0xc8), 0x40000);
            Put64(0x30000 + (modern ? (nuint)0x90 : 0x88), 0x50000);
            Put64(0x50010, 0x90000);
            Put64(0x30000 + (modern ? (nuint)0x48 : 0x40), 0x60000);
            Put64(0x60000 + (modern ? (nuint)0x68 : 0x60), 0x70000);
            Put64(0x70000 + (modern ? (nuint)0x40 : 0x38), 0x80000);
            values[0x80010] = BitConverter.GetBytes(version.Length);
            values[0x80014] = Encoding.Unicode.GetBytes(version);
            values[0x40000 + (modern ? (nuint)0x19c : 0x194)] = BitConverter.GetBytes(5);
            Health(5); State(4);
        }
        internal void Health(int value) => values[0x40000 + (modern ? (nuint)0x198 : 0x190)] = BitConverter.GetBytes(value);
        internal void State(int value) => values[0x30000 + (modern ? (nuint)0x18c : 0x174)] = BitConverter.GetBytes(value);
        internal void Put64(nuint at, ulong value) => values[at] = BitConverter.GetBytes(value);
        internal void AddSignature(int at, ulong root)
        {
            byte[] code = Convert.FromHexString("41FFD3E96300000048B80000000000000000488B10488BCE488D6424009049BB");
            BinaryPrimitives.WriteUInt64LittleEndian(code.AsSpan(10), root);
            code.CopyTo(Code, at);
        }
        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(nuint address, byte[] destination, CancellationToken cancellationToken)
        {
            byte[]? data = address == 0x10000 ? Code : values.GetValueOrDefault(address);
            if (address != 0x10000) LargestSampleRead = Math.Max(LargestSampleRead, destination.Length);
            if (data is null || address == Unreadable || data.Length != destination.Length)
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
            data.CopyTo(destination, 0);
            if (address == Tear && ++tornReads > 1) destination[0] ^= 8;
            return ValueTask.FromResult(ReadOnlyMemoryReadResult.Succeeded((nuint)destination.Length));
        }
        public ValueTask<ReadOnlyProcessMemoryRegion?> QueryRegionAsync(nuint address, CancellationToken cancellationToken)
        {
            MapQueries++;
            return ValueTask.FromResult<ReadOnlyProcessMemoryRegion?>(address == 0 ? new(0x10000, 0x10000, 128, 0x1000, 0x40, 0x20000) : null);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
    }
}
