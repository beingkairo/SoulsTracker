using System.Reflection;
using System.Text.Json;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class DarkSoulsRemasteredLiveIdentityValidationHarnessTests
{
    private static readonly ProcessModuleFileIdentity ExactCandidate = new(
        "DarkSoulsRemastered.exe",
        "1,0,0,0",
        "1",
        "A45AAA36DD2F6CC151670A639EA5547043CF38EA79FF4178B963C6ED71F98D7B");

    [Fact]
    public async Task MissingConfirmationPreventsEveryProcessInteraction()
    {
        var enumerator = new FakeProcessEnumerator();
        var attachmentFactory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var harness = CreateHarness(enumerator, attachmentFactory);

        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await harness.RunIfConfirmedAsync(null, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, enumerator.EnumerationCalls);
        Assert.Equal(0, attachmentFactory.AttachCalls);
    }

    [Fact]
    public async Task IncorrectConfirmationPreventsEveryProcessInteraction()
    {
        var enumerator = new FakeProcessEnumerator();
        var attachmentFactory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var harness = CreateHarness(enumerator, attachmentFactory);

        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await harness.RunIfConfirmedAsync("incorrect", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, enumerator.EnumerationCalls);
        Assert.Equal(0, attachmentFactory.AttachCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ZeroOrMultipleCandidatesReturnOnlyUnsupportedAndDisposeEveryCandidate(int candidateCount)
    {
        FakeProcessCandidate[] candidates = Enumerable.Range(0, candidateCount)
            .Select(_ => new FakeProcessCandidate(1234))
            .ToArray();
        var enumerator = new FakeProcessEnumerator(candidates);
        var attachmentFactory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var harness = CreateHarness(enumerator, attachmentFactory);

        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredLiveIdentityValidationHarness.RequiredConfirmation,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.Equal(1, enumerator.EnumerationCalls);
        Assert.Equal(0, attachmentFactory.AttachCalls);
        Assert.All(candidates, candidate => Assert.True(candidate.IsDisposed));
    }

    [Fact]
    public async Task CancellationBeforeConfirmationGatePreventsEveryProcessInteraction()
    {
        var enumerator = new FakeProcessEnumerator();
        var attachmentFactory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var harness = CreateHarness(enumerator, attachmentFactory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredLiveIdentityValidationHarness.RequiredConfirmation,
            cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, enumerator.EnumerationCalls);
        Assert.Equal(0, attachmentFactory.AttachCalls);
    }

    [Fact]
    public async Task UnsupportedIdentityReturnsOnlyFixedOutcomeAndDisposesEveryResource()
    {
        var candidate = new FakeProcessCandidate(1234);
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(ExactCandidate with { Sha256 = new string('0', 64) }));
        var attachmentFactory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment));
        var harness = CreateHarness(new FakeProcessEnumerator(candidate), attachmentFactory);

        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredLiveIdentityValidationHarness.RequiredConfirmation,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.True(candidate.IsDisposed);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
    }

    [Fact]
    public async Task EnumerationFailureReturnsOnlyFixedUnsupportedOutcome()
    {
        const string sensitiveDetail = "C:\\Users\\example\\DarkSoulsRemastered.exe";
        var attachmentFactory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var harness = CreateHarness(new FakeProcessEnumerator(() => throw new IOException(sensitiveDetail)), attachmentFactory);

        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredLiveIdentityValidationHarness.RequiredConfirmation,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.Equal(0, attachmentFactory.AttachCalls);
        Assert.Single(typeof(DarkSoulsRemasteredCandidateIdentityValidationResult).GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(sensitiveDetail, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactCandidateMatchesAndHarnessNeverReadsVirtualMemory()
    {
        var candidate = new FakeProcessCandidate(1234);
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(ExactCandidate))
        {
            ThrowIfMemoryReadIsRequested = true,
        };
        var harness = CreateHarness(
            new FakeProcessEnumerator(candidate),
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await harness.RunIfConfirmedAsync(
            DarkSoulsRemasteredLiveIdentityValidationHarness.RequiredConfirmation,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate, result.Outcome);
        Assert.True(candidate.IsDisposed);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
    }

    [Fact]
    public void LiveHarnessSourceHasNoMemoryReadOrProcessControlOperations()
    {
        string source = File.ReadAllText(GetHarnessSourcePath());

        Assert.Contains("Process.GetProcessesByName(ExecutableNameWithoutExtension)", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(source, "Process.GetProcessesByName("));
        Assert.DoesNotContain("ReadVirtual" + "MemoryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process." + "Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("process." + "Kill", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Close" + "MainWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Write" + "ProcessMemory", source, StringComparison.Ordinal);
    }

    private static DarkSoulsRemasteredLiveIdentityValidationHarness CreateHarness(
        FakeProcessEnumerator enumerator,
        FakeAttachmentFactory attachmentFactory) => new(
        enumerator,
        new DarkSoulsRemasteredCandidateIdentityValidator(attachmentFactory));

    private static int Count(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
        "DarkSoulsRemasteredLiveIdentityValidationHarness.cs"));

    private sealed class FakeProcessEnumerator : IDarkSoulsRemasteredProcessEnumerator
    {
        private readonly Func<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> enumerate;

        public FakeProcessEnumerator(params IDarkSoulsRemasteredProcessCandidate[] candidates)
            : this(() => candidates)
        {
        }

        public FakeProcessEnumerator(Func<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> enumerate)
        {
            this.enumerate = enumerate;
        }

        public int EnumerationCalls { get; private set; }

        public ValueTask<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> EnumerateExactCandidatesAsync(
            CancellationToken cancellationToken)
        {
            EnumerationCalls++;
            return ValueTask.FromResult(enumerate());
        }
    }

    private sealed class FakeProcessCandidate : IDarkSoulsRemasteredProcessCandidate
    {
        public FakeProcessCandidate(int processId)
        {
            ProcessId = processId;
        }

        public int ProcessId { get; }

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
        private readonly ReadOnlyModuleIdentityResult identityResult;

        public FakeAttachment(ReadOnlyModuleIdentityResult identityResult)
        {
            this.identityResult = identityResult;
        }

        public bool IsDisposed { get; private set; }

        public int MemoryReadCalls { get; private set; }

        public bool ThrowIfMemoryReadIsRequested { get; init; }

        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(identityResult);

        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());

        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(
            nuint address,
            byte[] destination,
            CancellationToken cancellationToken)
        {
            MemoryReadCalls++;
            if (ThrowIfMemoryReadIsRequested)
            {
                throw new Xunit.Sdk.XunitException("The live identity harness must not read virtual memory.");
            }

            return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
