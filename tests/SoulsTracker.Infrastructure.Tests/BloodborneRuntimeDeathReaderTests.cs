using System.Buffers.Binary;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class BloodborneRuntimeDeathReaderTests
{
    [Fact]
    public async Task FreshCharacterZeroIsConfirmedRatherThanTreatedAsAnUnavailableRead()
    {
        var candidate = new Candidate();
        var attachment = new Attachment(ValueBytes(0), ReadOnlyMemoryReadResult.Succeeded(sizeof(uint)));
        var reader = new BloodborneActiveCharacterDeathReader(
            new Enumerator([candidate]),
            new AttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        RuntimeGameReadResult? result = await reader.ReadAsync(default);

        Assert.NotNull(result);
        Assert.Equal(GameId.Bloodborne, result.GameId);
        Assert.Equal(RuntimeGameReaderStatus.Synced, result.Status);
        Assert.True(result.HasNoRecordedDeaths);
        Assert.Equal(0, result.Observation!.TotalDeaths.Value);
        Assert.Equal(GameId.Bloodborne.Value, result.Observation.SourceIdentity);
        Assert.Equal([0x0000002080673B8UL], attachment.ReadAddresses);
        Assert.Equal([sizeof(uint)], attachment.BufferLengths);
        Assert.Equal(0, attachment.IdentityQueries);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);
    }

    [Fact]
    public async Task ValidatedUnsignedCumulativeValuePublishesTheRuntimeObservation()
    {
        var candidate = new Candidate();
        var attachment = new Attachment(ValueBytes(44), ReadOnlyMemoryReadResult.Succeeded(sizeof(uint)));
        var reader = new BloodborneActiveCharacterDeathReader(
            new Enumerator([candidate]),
            new AttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        RuntimeGameReadResult? result = await reader.ReadAsync(default);

        Assert.Equal(RuntimeGameReaderStatus.Synced, result!.Status);
        Assert.False(result.HasNoRecordedDeaths);
        Assert.Equal(44, result.Observation!.TotalDeaths.Value);
        Assert.Equal(GameId.Bloodborne.Value, result.Observation.SourceIdentity);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);
    }

    [Theory]
    [InlineData(ReadOnlyMemoryReadOutcome.Unavailable, 0, 0)]
    [InlineData(ReadOnlyMemoryReadOutcome.Succeeded, 3, 0)]
    [InlineData(ReadOnlyMemoryReadOutcome.Succeeded, 4, 1_000_001)]
    public async Task LoadingOrInvalidGuestValuesNeverBecomeZero(
        ReadOnlyMemoryReadOutcome outcome,
        int bytesRead,
        uint value)
    {
        var candidate = new Candidate();
        var attachment = new Attachment(ValueBytes(value), ToReadResult(outcome, bytesRead));
        var reader = new BloodborneActiveCharacterDeathReader(
            new Enumerator([candidate]),
            new AttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        RuntimeGameReadResult? result = await reader.ReadAsync(default);

        Assert.NotNull(result);
        Assert.Equal(RuntimeGameReaderStatus.WaitingForActiveCharacter, result.Status);
        Assert.Null(result.Observation);
        Assert.False(result.HasNoRecordedDeaths);
        Assert.True(candidate.Disposed);
        Assert.True(attachment.Disposed);
    }

    [Fact]
    public async Task MissingAmbiguousOrInaccessibleRuntimeFailsClosedAndDisposesCandidates()
    {
        foreach (int count in new[] { 0, 2 })
        {
            Candidate[] candidates = Enumerable.Range(0, count).Select(static _ => new Candidate()).ToArray();
            var factory = new AttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
            RuntimeGameReadResult? result = await new BloodborneActiveCharacterDeathReader(
                new Enumerator(candidates),
                factory).ReadAsync(default);

            Assert.Null(result);
            Assert.Equal(0, factory.Calls);
            Assert.All(candidates, static candidate => Assert.True(candidate.Disposed));
        }

        var inaccessibleCandidate = new Candidate();
        var inaccessibleFactory = new AttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        Assert.Null(await new BloodborneActiveCharacterDeathReader(
            new Enumerator([inaccessibleCandidate]),
            inaccessibleFactory).ReadAsync(default));
        Assert.Equal(1, inaccessibleFactory.Calls);
        Assert.True(inaccessibleCandidate.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestTitleGateControlsWhetherTheKnownAddressIsRead(bool titleMatches)
    {
        var candidate = new Candidate(titleMatches);
        var attachment = new Attachment(ValueBytes(4), ReadOnlyMemoryReadResult.Succeeded(sizeof(uint)));
        var factory = new AttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment));
        var reader = new BloodborneActiveCharacterDeathReader(new Enumerator([candidate]), factory);

        RuntimeGameReadResult? result = await reader.ReadAsync(default);

        Assert.Equal(titleMatches, result is not null);
        Assert.Equal(1, candidate.GuestChecks);
        Assert.Equal(titleMatches ? 1 : 0, factory.Calls);
        Assert.Equal(titleMatches ? [sizeof(uint)] : [], attachment.BufferLengths);
        Assert.True(candidate.Disposed);
        Assert.Equal(titleMatches, attachment.Disposed);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("CUSA03173 - Another Game", false)]
    [InlineData("CUSA03174 - Bloodborne", false)]
    [InlineData("XCUSA03173 - Bloodborne", false)]
    [InlineData("CUSA03173 - Bloodborne", true)]
    [InlineData("CUSA03173 - BLOODBORNE", true)]
    public void ActiveGuestTitleRequiresBothTheExactProductTokenAndBloodborneName(string? title, bool expected)
    {
        Assert.Equal(expected, ExactNameBloodborneProcessEnumerator.IsExpectedGuestTitle(title));
    }

    [Fact]
    public void PublicDesktopAssetsDoNotExposeTheRuntimeHostIdentifier()
    {
        string desktopDirectory = Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop");
        string[] publicAssetFiles = Directory.GetFiles(desktopDirectory, "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(desktopDirectory, "*.cs", SearchOption.TopDirectoryOnly))
            .ToArray();

        Assert.DoesNotContain(publicAssetFiles, static path =>
            File.ReadAllText(path).Contains("shadPS4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReaderRecoversAfterTheRuntimeClosesAndStartsAgain()
    {
        var restartedCandidate = new Candidate();
        var restartedAttachment = new Attachment(ValueBytes(1), ReadOnlyMemoryReadResult.Succeeded(sizeof(uint)));
        var enumerator = new SequencedEnumerator([], [restartedCandidate]);
        var factory = new AttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(restartedAttachment));
        var reader = new BloodborneActiveCharacterDeathReader(enumerator, factory);

        Assert.Null(await reader.ReadAsync(default));

        RuntimeGameReadResult? afterRestart = await reader.ReadAsync(default);
        Assert.Equal(RuntimeGameReaderStatus.Synced, afterRestart!.Status);
        Assert.Equal(1, afterRestart.Observation!.TotalDeaths.Value);
        Assert.Equal(1, factory.Calls);
        Assert.True(restartedCandidate.Disposed);
        Assert.True(restartedAttachment.Disposed);
    }

    private static byte[] ValueBytes(uint value)
    {
        byte[] bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static ReadOnlyMemoryReadResult ToReadResult(ReadOnlyMemoryReadOutcome outcome, int bytesRead) => outcome switch
    {
        ReadOnlyMemoryReadOutcome.Succeeded => ReadOnlyMemoryReadResult.Succeeded((nuint)bytesRead),
        ReadOnlyMemoryReadOutcome.Cancelled => ReadOnlyMemoryReadResult.Cancelled(),
        _ => ReadOnlyMemoryReadResult.Unavailable(),
    };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SoulsTracker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class Enumerator(IReadOnlyList<IBloodborneProcessCandidate> candidates) : IBloodborneProcessEnumerator
    {
        public ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(candidates);
    }

    private sealed class SequencedEnumerator(params IReadOnlyList<IBloodborneProcessCandidate>[] reads) : IBloodborneProcessEnumerator
    {
        private readonly Queue<IReadOnlyList<IBloodborneProcessCandidate>> reads = new(reads);

        public ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(reads.Dequeue());
    }

    private sealed class Candidate(bool guestActive = true) : IBloodborneProcessCandidate
    {
        public int ProcessId => 42;
        public bool Disposed { get; private set; }
        public int GuestChecks { get; private set; }

        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken)
        {
            GuestChecks++;
            return ValueTask.FromResult(guestActive && !cancellationToken.IsCancellationRequested);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AttachmentFactory(params ReadOnlyProcessAttachmentResult[] results) : IReadOnlyProcessAttachmentFactory
    {
        private readonly Queue<ReadOnlyProcessAttachmentResult> results = new(results);
        public int Calls { get; private set; }

        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(results.Dequeue());
        }
    }

    private sealed class Attachment(byte[] valueBytes, ReadOnlyMemoryReadResult result) : IReadOnlyProcessAttachment
    {
        public List<ulong> ReadAddresses { get; } = [];
        public List<int> BufferLengths { get; } = [];
        public int IdentityQueries { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken)
        {
            IdentityQueries++;
            return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
        }

        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());

        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(
            nuint address,
            byte[] destination,
            CancellationToken cancellationToken)
        {
            ReadAddresses.Add((ulong)address);
            BufferLengths.Add(destination.Length);
            valueBytes.CopyTo(destination, 0);
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
