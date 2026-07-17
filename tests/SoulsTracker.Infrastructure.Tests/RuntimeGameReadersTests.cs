using System.Buffers.Binary;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class RuntimeGameReadersTests
{
    private static readonly ProcessModuleFileIdentity ExactIdentity = new("DarkSoulsRemastered.exe", "1,0,0,0", "1", "A45AAA36DD2F6CC151670A639EA5547043CF38EA79FF4178B963C6ED71F98D7B");

    [Fact]
    public async Task Ds1ReaderReadsOnlyFrozenPointerAndValueAndDisposesResources()
    {
        var candidate = new Candidate();
        var attachment = new Attachment(ExactIdentity, PointerBytes(0x2000), ValueBytes(17));
        var reader = new DarkSoulsRemasteredActiveCharacterDeathReader(new Enumerator(candidate), new AttachmentFactory(attachment));

        RuntimeGameReadResult? result = await reader.ReadAsync(default);

        Assert.Equal(RuntimeGameReaderStatus.Synced, result!.Status);
        Assert.Equal(GameId.Ds1, result.Observation!.GameId);
        Assert.Equal(17, result.Observation.TotalDeaths.Value);
        Assert.Equal([8, 4], attachment.BufferLengths);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);
    }

    [Fact]
    public async Task CoordinatorClearsPriorObservationWhenSelectionChangesOrReaderIsUnavailable()
    {
        var reader = new StubReader(GameId.Ds1, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 3, DateTimeOffset.UtcNow)));
        var coordinator = new RuntimeGameReaderCoordinator([reader]);

        await coordinator.PollAsync(GameId.Ds1, default);
        Assert.NotNull(coordinator.CurrentObservation);
        await coordinator.PollAsync(GameId.Ds2, default);
        Assert.Null(coordinator.CurrentObservation);
        reader.Result = null;
        await coordinator.PollAsync(GameId.Ds1, default);
        Assert.Null(coordinator.CurrentObservation);
    }

    [Fact]
    public async Task CoordinatorPublishesTypedWaitingAndUnavailableStatusesWithoutAnObservation()
    {
        var reader = new StubReader(GameId.Ds2, RuntimeGameReadResult.WaitingForActiveCharacter(GameId.Ds2));
        var coordinator = new RuntimeGameReaderCoordinator([reader]);

        RuntimeGameReadResult? waiting = await coordinator.PollAsync(GameId.Ds2, default);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, waiting!.Status);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, coordinator.CurrentStatus);
        Assert.Null(coordinator.CurrentObservation);

        reader.Result = null;
        RuntimeGameReadResult? unavailable = await coordinator.PollAsync(GameId.Ds2, default);
        Assert.Null(unavailable);
        Assert.Equal(RuntimeGameReaderStatus.Unavailable, coordinator.CurrentStatus);
        Assert.Null(coordinator.CurrentObservation);
    }

    [Fact]
    public async Task Ds1ReaderFailsClosedForZeroOrMultipleCandidatesWithoutAttaching()
    {
        var factory = new AttachmentFactory(new Attachment(ExactIdentity, PointerBytes(0x2000), ValueBytes(1)));
        foreach (int count in new[] { 0, 2 })
        {
            var candidates = Enumerable.Range(0, count).Select(_ => new Candidate()).ToArray();
            var reader = new DarkSoulsRemasteredActiveCharacterDeathReader(new Enumerator(candidates), factory);
            Assert.Null(await reader.ReadAsync(default));
            Assert.All(candidates, candidate => Assert.True(candidate.Disposed));
        }
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task Ds1ReaderRejectsIdentityMismatchBeforeAnyMemoryReadAndDisposes()
    {
        var candidate = new Candidate();
        var attachment = new Attachment(new ProcessModuleFileIdentity("other.exe", "1,0,0,0", "1", ExactIdentity.Sha256), PointerBytes(0x2000), ValueBytes(1));
        var reader = new DarkSoulsRemasteredActiveCharacterDeathReader(new Enumerator(candidate), new AttachmentFactory(attachment));

        Assert.Null(await reader.ReadAsync(default));
        Assert.Empty(attachment.BufferLengths);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);
    }

    [Fact]
    public async Task Ds1ReaderFailsClosedForCancelledModulePartialReadOverflowAndOutOfRangeValue()
    {
        Attachment[] attachments =
        [
            new(ExactIdentity, ReadOnlyMainModuleBaseResult.Cancelled()),
            new(ExactIdentity, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(7))),
            new(ExactIdentity, ReadOnlyMainModuleBaseResult.Available(nuint.MaxValue), new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(8))),
            new(ExactIdentity, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(1_000_001), ReadOnlyMemoryReadResult.Succeeded(4))),
        ];

        foreach (Attachment attachment in attachments)
        {
            var candidate = new Candidate();
            var reader = new DarkSoulsRemasteredActiveCharacterDeathReader(new Enumerator(candidate), new AttachmentFactory(attachment));
            Assert.Null(await reader.ReadAsync(default));
            Assert.True(candidate.Disposed);
            Assert.True(attachment.Disposed);
        }

        var waitingCandidate = new Candidate();
        var waitingAttachment = new Attachment(
            ExactIdentity,
            ReadOnlyMainModuleBaseResult.Available(0x1000),
            new Plan(PointerBytes(0), ReadOnlyMemoryReadResult.Succeeded(8)));
        RuntimeGameReadResult? waiting = await new DarkSoulsRemasteredActiveCharacterDeathReader(
            new Enumerator(waitingCandidate),
            new AttachmentFactory(waitingAttachment)).ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, waiting!.Status);
        Assert.Null(waiting.Observation);
    }

    [Fact]
    public async Task Ds2ScholarReaderUsesOnlyThreePointersAndFinalValueAndFailsClosedOnMismatch()
    {
        var candidate = new Ds2Candidate();
        ProcessModuleFileIdentity ds2 = new("DarkSoulsII.exe", "", "", "");
        var attachment = new Attachment(ds2, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(0x3000), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(0x4000), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(8), ReadOnlyMemoryReadResult.Succeeded(4)));
        var reader = new DarkSoulsIIScholarActiveCharacterDeathReader(new Ds2Enumerator(candidate), new AttachmentFactory(attachment), new ExactDarkSoulsIIScholarIdentityValidator(ds2));

        RuntimeGameReadResult? result = await reader.ReadAsync(default);
        Assert.Equal(GameId.Ds2, result!.Observation!.GameId);
        Assert.Equal([8, 8, 8, 4], attachment.BufferLengths);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);

        var badCandidate = new Ds2Candidate();
        var badAttachment = new Attachment(ExactIdentity, PointerBytes(0x2000), ValueBytes(1));
        Assert.Null(await new DarkSoulsIIScholarActiveCharacterDeathReader(new Ds2Enumerator(badCandidate), new AttachmentFactory(badAttachment), new ExactDarkSoulsIIScholarIdentityValidator(ds2)).ReadAsync(default));
        Assert.Empty(badAttachment.BufferLengths);
        Assert.True(badCandidate.Disposed);
    }

    [Fact]
    public async Task Ds2ScholarReaderFailsClosedForCandidateAndEveryBoundedChainFailure()
    {
        ProcessModuleFileIdentity ds2 = new("DarkSoulsII.exe", "1", "1", "hash");
        var validator = new ExactDarkSoulsIIScholarIdentityValidator(ds2);
        var factory = new AttachmentFactory(new Attachment(ds2, PointerBytes(1), ValueBytes(1)));
        foreach (int count in new[] { 0, 2 })
        {
            Ds2Candidate[] candidates = Enumerable.Range(0, count).Select(_ => new Ds2Candidate()).ToArray();
            Assert.Null(await new DarkSoulsIIScholarActiveCharacterDeathReader(new Ds2Enumerator(candidates), factory, validator).ReadAsync(default));
            Assert.All(candidates, candidate => Assert.True(candidate.Disposed));
        }
        Assert.Equal(0, factory.Calls);

        Attachment[] failures =
        [
            new(ds2, ReadOnlyMainModuleBaseResult.Unavailable()),
            new(ds2, ReadOnlyMainModuleBaseResult.Cancelled()),
            new(ds2, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(7))),
            new(ds2, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(2), ReadOnlyMemoryReadResult.Succeeded(7))),
            new(ds2, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(2), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(3), ReadOnlyMemoryReadResult.Succeeded(7))),
            new(ds2, ReadOnlyMainModuleBaseResult.Available(nuint.MaxValue), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8))),
            new(ds2, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(2), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(3), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(1), ReadOnlyMemoryReadResult.Succeeded(3))),
            new(ds2, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(2), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(PointerBytes(3), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(1_000_001), ReadOnlyMemoryReadResult.Succeeded(4))),
        ];
        foreach (Attachment attachment in failures)
        {
            var candidate = new Ds2Candidate();
            Assert.Null(await new DarkSoulsIIScholarActiveCharacterDeathReader(new Ds2Enumerator(candidate), new AttachmentFactory(attachment), validator).ReadAsync(default));
            Assert.True(candidate.Disposed);
            Assert.True(attachment.Disposed);
        }

        var waitingCandidate = new Ds2Candidate();
        var waitingAttachment = new Attachment(
            ds2,
            ReadOnlyMainModuleBaseResult.Available(0x1000),
            new Plan(PointerBytes(0), ReadOnlyMemoryReadResult.Succeeded(8)));
        RuntimeGameReadResult? waiting = await new DarkSoulsIIScholarActiveCharacterDeathReader(
            new Ds2Enumerator(waitingCandidate),
            new AttachmentFactory(waitingAttachment),
            validator).ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, waiting!.Status);
        Assert.Null(waiting.Observation);
    }

    [Fact]
    public async Task Ds2ExactIdentityValidatorRejectsAnyVersionOrHashMismatch()
    {
        ProcessModuleFileIdentity expected = new("DarkSoulsII.exe", "1,0,3,0", "1,0,3,0", "0045931B8914504531B7864A9488D396DC50CBAF524964016E1D69C3D1173131");
        var validator = new ExactDarkSoulsIIScholarIdentityValidator(expected);
        Assert.True(await validator.ValidateAttachedAsync(new Attachment(expected, PointerBytes(1), ValueBytes(1)), default));
        ProcessModuleFileIdentity mismatch = expected with { FileVersion = "1.0.3.0" };
        Assert.False(await validator.ValidateAttachedAsync(new Attachment(mismatch, PointerBytes(1), ValueBytes(1)), default));
    }

    [Fact]
    public async Task Ds3ReaderUsesOnePointerAndValueAndUnverifiedProfileFailsBeforeReads()
    {
        ProcessModuleFileIdentity ds3 = new("DarkSoulsIII.exe", "1", "1", "hash");
        var candidate = new Ds3Candidate();
        var attachment = new Attachment(ds3, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(9), ReadOnlyMemoryReadResult.Succeeded(4)));
        RuntimeGameReadResult? result = await new DarkSoulsIIIActiveCharacterDeathReader(new Ds3Enumerator(candidate), new AttachmentFactory(attachment), new ExactDarkSoulsIIIIdentityValidator(ds3)).ReadAsync(default);

        Assert.Equal(GameId.Ds3, result!.Observation!.GameId);
        Assert.Equal([8, 4], attachment.BufferLengths);
        Assert.True(candidate.Disposed);

        var unverified = new Attachment(ds3, PointerBytes(1), ValueBytes(1));
        Assert.Null(await new DarkSoulsIIIActiveCharacterDeathReader(new Ds3Enumerator(new Ds3Candidate()), new AttachmentFactory(unverified), new UnverifiedDarkSoulsIIIIdentityValidator()).ReadAsync(default));
        Assert.Empty(unverified.BufferLengths);
    }

    [Fact]
    public async Task Ds3ExactIdentityValidatorRejectsVersionFormattingAndHashMismatches()
    {
        ProcessModuleFileIdentity expected = new(
            "DarkSoulsIII.exe",
            "1.15.2.0",
            "1.15.2.0",
            "EF5E07C55222F14FFDDECF2C724A0C2A95CEF0D4DA0E075B0DE0BB108B69498C");
        var validator = new ExactDarkSoulsIIIIdentityValidator(expected);

        Assert.True(await validator.ValidateAttachedAsync(new Attachment(expected, PointerBytes(1), ValueBytes(1)), default));
        Assert.False(await validator.ValidateAttachedAsync(
            new Attachment(expected with { FileVersion = "1,15,2,0" }, PointerBytes(1), ValueBytes(1)),
            default));
        Assert.False(await validator.ValidateAttachedAsync(
            new Attachment(expected with { Sha256 = "0F5E07C55222F14FFDDECF2C724A0C2A95CEF0D4DA0E075B0DE0BB108B69498C" }, PointerBytes(1), ValueBytes(1)),
            default));
    }

    [Fact]
    public async Task Ds3ReaderFailsClosedWhenIdentityRecheckFailsBeforeFinalValueRead()
    {
        ProcessModuleFileIdentity ds3 = new("DarkSoulsIII.exe", "1", "1", "hash");
        var candidate = new Ds3Candidate();
        var attachment = new Attachment(
            ds3,
            ReadOnlyMainModuleBaseResult.Available(0x1000),
            new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(8)));
        var validator = new SequencedDs3IdentityValidator(true, false);
        var reader = new DarkSoulsIIIActiveCharacterDeathReader(
            new Ds3Enumerator(candidate),
            new AttachmentFactory(attachment),
            validator);

        RuntimeGameReadResult? result = await reader.ReadAsync(default);

        Assert.Null(result);
        Assert.Equal(2, validator.Calls);
        Assert.Equal([8], attachment.BufferLengths);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);
    }

    [Fact]
    public async Task Ds3ReaderFailsClosedAndDisposesForCandidateAndReadFailures()
    {
        ProcessModuleFileIdentity ds3 = new("DarkSoulsIII.exe", "1", "1", "hash");
        var validator = new ExactDarkSoulsIIIIdentityValidator(ds3);
        var noAttach = new AttachmentFactory(new Attachment(ds3, PointerBytes(1), ValueBytes(1)));
        foreach (int count in new[] { 0, 2 })
        {
            Ds3Candidate[] candidates = Enumerable.Range(0, count).Select(_ => new Ds3Candidate()).ToArray();
            Assert.Null(await new DarkSoulsIIIActiveCharacterDeathReader(new Ds3Enumerator(candidates), noAttach, validator).ReadAsync(default));
            Assert.All(candidates, candidate => Assert.True(candidate.Disposed));
        }
        Assert.Equal(0, noAttach.Calls);
        Attachment[] failures = [new(ds3, ReadOnlyMainModuleBaseResult.Unavailable()), new(ds3, ReadOnlyMainModuleBaseResult.Cancelled()), new(ds3, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(7))), new(ds3, ReadOnlyMainModuleBaseResult.Available(nuint.MaxValue), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8))), new(ds3, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(1), ReadOnlyMemoryReadResult.Succeeded(3))), new(ds3, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(1_000_001), ReadOnlyMemoryReadResult.Succeeded(4)))];
        foreach (Attachment attachment in failures)
        {
            var candidate = new Ds3Candidate();
            Assert.Null(await new DarkSoulsIIIActiveCharacterDeathReader(new Ds3Enumerator(candidate), new AttachmentFactory(attachment), validator).ReadAsync(default));
            Assert.True(candidate.Disposed);
            Assert.True(attachment.Disposed);
        }

        var waitingCandidate = new Ds3Candidate();
        var waitingAttachment = new Attachment(
            ds3,
            ReadOnlyMainModuleBaseResult.Available(0x1000),
            new Plan(PointerBytes(0), ReadOnlyMemoryReadResult.Succeeded(8)));
        RuntimeGameReadResult? waiting = await new DarkSoulsIIIActiveCharacterDeathReader(
            new Ds3Enumerator(waitingCandidate),
            new AttachmentFactory(waitingAttachment),
            validator).ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, waiting!.Status);
        Assert.Null(waiting.Observation);
    }

    [Fact]
    public async Task SekiroReaderUsesOnlyFrozenPointerAndValueAndUnverifiedProfileFailsBeforeReads()
    {
        ProcessModuleFileIdentity sekiro = new("Sekiro.exe", "1", "1", "hash");
        var candidate = new SekiroCandidate();
        var attachment = new Attachment(
            sekiro,
            ReadOnlyMainModuleBaseResult.Available(0x1000),
            new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(8)),
            new Plan(ValueBytes(10), ReadOnlyMemoryReadResult.Succeeded(4)));
        var reader = new SekiroActiveCharacterDeathReader(
            new SekiroEnumerator(candidate),
            new AttachmentFactory(attachment),
            new ExactSekiroIdentityValidator(sekiro));

        RuntimeGameReadResult? result = await reader.ReadAsync(default);

        Assert.Equal(GameId.Sekiro, result!.Observation!.GameId);
        Assert.Equal(10, result.Observation.TotalDeaths.Value);
        Assert.Equal([8, 4], attachment.BufferLengths);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);

        var unverified = new Attachment(sekiro, PointerBytes(1), ValueBytes(1));
        Assert.Null(await new SekiroActiveCharacterDeathReader(
            new SekiroEnumerator(new SekiroCandidate()),
            new AttachmentFactory(unverified),
            new UnverifiedSekiroIdentityValidator()).ReadAsync(default));
        Assert.Empty(unverified.BufferLengths);
    }

    [Fact]
    public async Task SekiroCoordinatorProjectsOnlySelectedObservationAndClearsWhenUnavailable()
    {
        var reader = new StubReader(GameId.Sekiro, RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Sekiro, 10, DateTimeOffset.UtcNow)));
        var coordinator = new RuntimeGameReaderCoordinator([reader]);

        await coordinator.PollAsync(GameId.Sekiro, default);
        Assert.Equal(GameId.Sekiro, coordinator.CurrentObservation!.GameId);

        reader.Result = null;
        await coordinator.PollAsync(GameId.Sekiro, default);

        Assert.Null(coordinator.CurrentObservation);
    }

    [Fact]
    public async Task SekiroExactIdentityValidatorRejectsVersionFormattingAndHashMismatches()
    {
        ProcessModuleFileIdentity expected = new(
            "sekiro.exe",
            "1.6.0.0",
            "1.6.0.0",
            "637ACA527538C0EC6E1F136C8ED66046E95DFBDBB1F51926E134D9916398B856");
        var validator = new ExactSekiroIdentityValidator(expected);

        Assert.True(await validator.ValidateAttachedAsync(new Attachment(expected, PointerBytes(1), ValueBytes(1)), default));
        Assert.False(await validator.ValidateAttachedAsync(
            new Attachment(expected with { FileVersion = "1,6,0,0" }, PointerBytes(1), ValueBytes(1)),
            default));
        Assert.False(await validator.ValidateAttachedAsync(
            new Attachment(expected with { Sha256 = "037ACA527538C0EC6E1F136C8ED66046E95DFBDBB1F51926E134D9916398B856" }, PointerBytes(1), ValueBytes(1)),
            default));
    }

    [Fact]
    public async Task SekiroReaderFailsClosedAndDisposesForCandidateAndBoundedReadFailures()
    {
        ProcessModuleFileIdentity sekiro = new("Sekiro.exe", "1", "1", "hash");
        var validator = new ExactSekiroIdentityValidator(sekiro);
        var noAttach = new AttachmentFactory(new Attachment(sekiro, PointerBytes(1), ValueBytes(1)));
        foreach (int count in new[] { 0, 2 })
        {
            SekiroCandidate[] candidates = Enumerable.Range(0, count).Select(_ => new SekiroCandidate()).ToArray();
            Assert.Null(await new SekiroActiveCharacterDeathReader(new SekiroEnumerator(candidates), noAttach, validator).ReadAsync(default));
            Assert.All(candidates, candidate => Assert.True(candidate.Disposed));
        }

        Assert.Equal(0, noAttach.Calls);

        Attachment[] failures =
        [
            new(sekiro, ReadOnlyMainModuleBaseResult.Unavailable()),
            new(sekiro, ReadOnlyMainModuleBaseResult.Cancelled()),
            new(sekiro, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(7))),
            new(sekiro, ReadOnlyMainModuleBaseResult.Available(nuint.MaxValue), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8))),
            new(sekiro, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(1), ReadOnlyMemoryReadResult.Succeeded(3))),
            new(sekiro, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(PointerBytes(1), ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(ValueBytes(1_000_001), ReadOnlyMemoryReadResult.Succeeded(4))),
        ];
        foreach (Attachment attachment in failures)
        {
            var candidate = new SekiroCandidate();
            Assert.Null(await new SekiroActiveCharacterDeathReader(
                new SekiroEnumerator(candidate),
                new AttachmentFactory(attachment),
                validator).ReadAsync(default));
            Assert.True(candidate.Disposed);
            Assert.True(attachment.Disposed);
        }

        var waitingCandidate = new SekiroCandidate();
        var waitingAttachment = new Attachment(
            sekiro,
            ReadOnlyMainModuleBaseResult.Available(0x1000),
            new Plan(PointerBytes(0), ReadOnlyMemoryReadResult.Succeeded(8)));
        RuntimeGameReadResult? waiting = await new SekiroActiveCharacterDeathReader(
            new SekiroEnumerator(waitingCandidate),
            new AttachmentFactory(waitingAttachment),
            validator).ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, waiting!.Status);
        Assert.Null(waiting.Observation);

        var recheckCandidate = new SekiroCandidate();
        var recheckAttachment = new Attachment(
            sekiro,
            ReadOnlyMainModuleBaseResult.Available(0x1000),
            new Plan(PointerBytes(0x2000), ReadOnlyMemoryReadResult.Succeeded(8)));
        var recheckValidator = new SequencedSekiroIdentityValidator(true, false);
        Assert.Null(await new SekiroActiveCharacterDeathReader(
            new SekiroEnumerator(recheckCandidate),
            new AttachmentFactory(recheckAttachment),
            recheckValidator).ReadAsync(default));
        Assert.Equal(2, recheckValidator.Calls);
        Assert.Equal([8], recheckAttachment.BufferLengths);
        Assert.True(recheckCandidate.Disposed);
        Assert.True(recheckAttachment.Disposed);
    }

    private static byte[] PointerBytes(ulong value) { byte[] bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); return bytes; }
    private static byte[] ValueBytes(int value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }

    private sealed class StubReader(GameId gameId, RuntimeGameReadResult? result) : IRuntimeGameDeathReader
    {
        public GameId GameId => gameId;
        public RuntimeGameReadResult? Result { get; set; } = result;
        public ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result);
    }

    private sealed class Enumerator : SoulsTracker.Infrastructure.IDarkSoulsRemasteredProcessEnumerator
    {
        private readonly Candidate[] candidates;
        public Enumerator(params Candidate[] candidates) => this.candidates = candidates;
        public ValueTask<IReadOnlyList<SoulsTracker.Infrastructure.IDarkSoulsRemasteredProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SoulsTracker.Infrastructure.IDarkSoulsRemasteredProcessCandidate>>(candidates);
    }

    private sealed class Candidate : SoulsTracker.Infrastructure.IDarkSoulsRemasteredProcessCandidate
    {
        public int ProcessId => 42;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class AttachmentFactory(Attachment attachment) : IReadOnlyProcessAttachmentFactory
    {
        public int Calls { get; private set; }
        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken) { Calls++; return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Attached(attachment)); }
    }

    private sealed class Attachment : IReadOnlyProcessAttachment
    {
        private readonly ProcessModuleFileIdentity identity;
        private readonly ReadOnlyMainModuleBaseResult module;
        private readonly Queue<Plan> reads;
        public Attachment(ProcessModuleFileIdentity identity, byte[] pointerBytes, byte[] valueBytes) : this(identity, ReadOnlyMainModuleBaseResult.Available(0x1000), new Plan(pointerBytes, ReadOnlyMemoryReadResult.Succeeded(8)), new Plan(valueBytes, ReadOnlyMemoryReadResult.Succeeded(4))) { }
        public Attachment(ProcessModuleFileIdentity identity, ReadOnlyMainModuleBaseResult module, params Plan[] reads) { this.identity = identity; this.module = module; this.reads = new Queue<Plan>(reads); }
        public List<int> BufferLengths { get; } = [];
        public bool Disposed { get; private set; }
        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ReadOnlyModuleIdentityResult.Available(identity));
        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken) => ValueTask.FromResult(module);
        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(nuint address, byte[] destination, CancellationToken cancellationToken)
        {
            BufferLengths.Add(destination.Length);
            Plan plan = reads.Dequeue();
            Array.Copy(plan.Bytes, destination, plan.Bytes.Length);
            return ValueTask.FromResult(plan.Result);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed record Plan(byte[] Bytes, ReadOnlyMemoryReadResult Result);

    private sealed class Ds2Enumerator(params Ds2Candidate[] candidates) : SoulsTracker.Infrastructure.IDarkSoulsIIScholarProcessEnumerator
    {
        public ValueTask<IReadOnlyList<SoulsTracker.Infrastructure.IDarkSoulsIIScholarProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SoulsTracker.Infrastructure.IDarkSoulsIIScholarProcessCandidate>>(candidates);
    }

    private sealed class Ds2Candidate : SoulsTracker.Infrastructure.IDarkSoulsIIScholarProcessCandidate
    {
        public int ProcessId => 43;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Ds3Enumerator(params Ds3Candidate[] candidates) : SoulsTracker.Infrastructure.IDarkSoulsIIIProcessEnumerator
    {
        public ValueTask<IReadOnlyList<SoulsTracker.Infrastructure.IDarkSoulsIIIProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SoulsTracker.Infrastructure.IDarkSoulsIIIProcessCandidate>>(candidates);
    }

    private sealed class Ds3Candidate : SoulsTracker.Infrastructure.IDarkSoulsIIIProcessCandidate
    {
        public int ProcessId => 44;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequencedDs3IdentityValidator(params bool[] results) : IDarkSoulsIIIIdentityValidator
    {
        private readonly Queue<bool> results = new(results);

        public int Calls { get; private set; }

        public ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(results.Dequeue());
        }
    }

    private sealed class SekiroEnumerator(params SekiroCandidate[] candidates) : ISekiroProcessEnumerator
    {
        public ValueTask<IReadOnlyList<ISekiroProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ISekiroProcessCandidate>>(candidates);
    }

    private sealed class SekiroCandidate : ISekiroProcessCandidate
    {
        public int ProcessId => 45;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequencedSekiroIdentityValidator(params bool[] results) : ISekiroIdentityValidator
    {
        private readonly Queue<bool> results = new(results);

        public int Calls { get; private set; }

        public ValueTask<bool> ValidateAttachedAsync(IReadOnlyProcessAttachment attachment, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(results.Dequeue());
        }
    }
}
