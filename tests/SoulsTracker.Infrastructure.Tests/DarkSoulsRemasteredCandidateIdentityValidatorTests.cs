using System.Reflection;
using System.Text.Json;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class DarkSoulsRemasteredCandidateIdentityValidatorTests
{
    private static readonly ProcessModuleFileIdentity ExactCandidate = new(
        "DarkSoulsRemastered.exe",
        "1,0,0,0",
        "1",
        "A45AAA36DD2F6CC151670A639EA5547043CF38EA79FF4178B963C6ED71F98D7B");

    [Fact]
    public async Task ExactCandidateMatchesThenDetachesWithoutReadingMemory()
    {
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(ExactCandidate));
        var factory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment));
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(factory);

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1234, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate, result.Outcome);
        Assert.Equal(1, factory.AttachCalls);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
    }

    [Fact]
    public async Task ValidatorNeverInvokesTheFutureVirtualMemoryReadPrimitive()
    {
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(ExactCandidate))
        {
            ThrowIfMemoryReadIsRequested = true,
        };
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1234, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate, result.Outcome);
        Assert.Equal(0, attachment.MemoryReadCalls);
        Assert.True(attachment.IsDisposed);
    }

    [Fact]
    public async Task AttachedIdentityValidationKeepsTheCallerOwnedAttachmentOpenAndReadFree()
    {
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(ExactCandidate))
        {
            ThrowIfMemoryReadIsRequested = true,
        };

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await DarkSoulsRemasteredCandidateIdentityValidator
            .ValidateAttachedAsync(attachment, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate, result.Outcome);
        Assert.False(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
    }

    [Fact]
    public async Task SameNameBinaryWithMismatchedHashIsUnsupportedAndDetaches()
    {
        ProcessModuleFileIdentity sameNameDifferentBinary = ExactCandidate with { Sha256 = new string('0', 64) };
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(sameNameDifferentBinary));
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1234, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
    }

    [Theory]
    [InlineData("Other.exe", "1,0,0,0", "1")]
    [InlineData("DarkSoulsRemastered.exe", "1,0,0,1", "1")]
    [InlineData("DarkSoulsRemastered.exe", "1,0,0,0", "2")]
    public async Task AnyFilenameOrVersionMismatchIsUnsupported(string executableFileName, string fileVersion, string productVersion)
    {
        ProcessModuleFileIdentity mismatch = ExactCandidate with
        {
            ExecutableFileName = executableFileName,
            FileVersion = fileVersion,
            ProductVersion = productVersion,
        };
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(mismatch));
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
    }

    [Fact]
    public async Task DottedFileVersionRepresentationIsUnsupportedEvenWhenAllOtherEvidenceMatches()
    {
        ProcessModuleFileIdentity dottedVersion = ExactCandidate with { FileVersion = "1.0.0.0" };
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Available(dottedVersion));
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
    }

    [Fact]
    public async Task MissingOrInaccessibleIdentityReturnsFixedUnsupportedResult()
    {
        var missingAttachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Unavailable());
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(missingAttachment)));

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.True(missingAttachment.IsDisposed);
        Assert.Equal(0, missingAttachment.MemoryReadCalls);
    }

    [Fact]
    public async Task HashOrModuleFailureNeverLeaksDetailsAndDetaches()
    {
        const string secretPath = "C:\\Users\\example\\game.exe";
        var attachment = new FakeAttachment(() => throw new IOException(secretPath));
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(
            new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment)));

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(777, CancellationToken.None);
        string serialized = JsonSerializer.Serialize(result);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
        Assert.DoesNotContain(secretPath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("777", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InaccessibleProcessReturnsFixedUnsupportedWithoutAnActiveAttachment()
    {
        var factory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(factory);

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported, result.Outcome);
        Assert.Equal(1, factory.AttachCalls);
        Assert.Equal(0, factory.ActiveAttachmentCount);
    }

    [Fact]
    public async Task CancellationBeforeAttachDoesNotCreateAnAttachment()
    {
        var factory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Unavailable());
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1, cancellation.Token);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, factory.AttachCalls);
        Assert.Equal(0, factory.ActiveAttachmentCount);
    }

    [Fact]
    public async Task CancellationDuringIdentityQueryDetachesTheAttachment()
    {
        var attachment = new FakeAttachment(ReadOnlyModuleIdentityResult.Cancelled());
        var factory = new FakeAttachmentFactory(ReadOnlyProcessAttachmentResult.Attached(attachment));
        var validator = new DarkSoulsRemasteredCandidateIdentityValidator(factory);

        DarkSoulsRemasteredCandidateIdentityValidationResult result = await validator.ValidateAsync(1, CancellationToken.None);

        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Cancelled, result.Outcome);
        Assert.True(attachment.IsDisposed);
        Assert.Equal(0, attachment.MemoryReadCalls);
        Assert.Equal(0, factory.ActiveAttachmentCount);
    }

    [Fact]
    public void PublicValidationResultContainsNoDiagnosticPayload()
    {
        PropertyInfo[] properties = typeof(DarkSoulsRemasteredCandidateIdentityValidationResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        PropertyInfo property = Assert.Single(properties);
        Assert.Equal(nameof(DarkSoulsRemasteredCandidateIdentityValidationResult.Outcome), property.Name);
        Assert.False(property.CanWrite);
        Assert.DoesNotContain(properties, candidate =>
            candidate.Name.Contains("Path", StringComparison.Ordinal) ||
            candidate.Name.Contains("Hash", StringComparison.Ordinal) ||
            candidate.Name.Contains("Process", StringComparison.Ordinal) ||
            candidate.PropertyType == typeof(Exception));
    }

    private sealed class FakeAttachmentFactory : IReadOnlyProcessAttachmentFactory
    {
        private readonly ReadOnlyProcessAttachmentResult result;

        public FakeAttachmentFactory(ReadOnlyProcessAttachmentResult result)
        {
            this.result = result;
            if (result.Attachment is FakeAttachment attachment)
            {
                attachment.Disposed += () => ActiveAttachmentCount--;
            }
        }

        public int AttachCalls { get; private set; }

        public int ActiveAttachmentCount { get; private set; }

        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            AttachCalls++;
            if (result.Attachment is FakeAttachment)
            {
                ActiveAttachmentCount++;
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeAttachment : IReadOnlyProcessAttachment
    {
        private readonly Func<ReadOnlyModuleIdentityResult> query;

        public FakeAttachment(ReadOnlyModuleIdentityResult result)
            : this(() => result)
        {
        }

        public FakeAttachment(Func<ReadOnlyModuleIdentityResult> query)
        {
            this.query = query;
        }

        public event Action? Disposed;

        public bool IsDisposed { get; private set; }

        public int MemoryReadCalls { get; private set; }

        public bool ThrowIfMemoryReadIsRequested { get; init; }

        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(query());

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
                throw new Xunit.Sdk.XunitException("Virtual-memory read primitive must not be called by identity validation.");
            }

            return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
        }

        public ValueTask DisposeAsync()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                Disposed?.Invoke();
            }

            return ValueTask.CompletedTask;
        }
    }
}
