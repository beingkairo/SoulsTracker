using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class EldenRingProcessDiscoveryTests
{
    private static readonly ProcessModuleFileIdentity ExactInstalledIdentity = new(
        "eldenring.exe",
        "2.6.2.0",
        "2.6.2.0",
        "34102B1C08BB5F769A724427A6F70FE29B3B732C31CF73693F861C48D3492DDB",
        "ELDEN RING™");

    [Fact]
    public async Task ExpectedProductIdentityAcceptsChangedVersionAndHashWithoutReadingVirtualMemory()
    {
        ProcessModuleFileIdentity harmlessUpdate = ExactInstalledIdentity with
        {
            FileVersion = "2.7.0.0",
            ProductVersion = "2.7.0.0",
            Sha256 = new string('A', 64),
        };
        var attachment = new FakeAttachment(harmlessUpdate);
        var validator = new EldenRingProductIdentityValidator();

        bool matched = await validator.ValidateAttachedAsync(attachment, default);

        Assert.True(matched);
        Assert.Equal(1, attachment.IdentityQueries);
        Assert.Equal(0, attachment.MemoryReadCalls);
        Assert.Equal(0, attachment.MainModuleQueries);
    }

    [Fact]
    public async Task AnyExecutableOrProductIdentityMismatchFailsClosedWithoutReadingVirtualMemory()
    {
        ProcessModuleFileIdentity[] mismatches =
        [
            ExactInstalledIdentity with { ExecutableFileName = "other.exe" },
            ExactInstalledIdentity with { ProductName = "OTHER GAME" },
        ];

        foreach (ProcessModuleFileIdentity mismatch in mismatches)
        {
            var attachment = new FakeAttachment(mismatch);

            bool matched = await new EldenRingProductIdentityValidator().ValidateAttachedAsync(attachment, default);

            Assert.False(matched);
            Assert.Equal(1, attachment.IdentityQueries);
            Assert.Equal(0, attachment.MemoryReadCalls);
            Assert.Equal(0, attachment.MainModuleQueries);
        }
    }

    [Fact]
    public async Task UnverifiedValidatorAlwaysFailsClosedWithoutAnyAttachmentAccess()
    {
        var attachment = new FakeAttachment(ExactInstalledIdentity);

        bool matched = await new UnverifiedEldenRingIdentityValidator().ValidateAttachedAsync(attachment, default);

        Assert.False(matched);
        Assert.Equal(0, attachment.IdentityQueries);
        Assert.Equal(0, attachment.MemoryReadCalls);
        Assert.Equal(0, attachment.MainModuleQueries);
    }

    private sealed class FakeAttachment(ProcessModuleFileIdentity identity) : IReadOnlyProcessAttachment
    {
        public int IdentityQueries { get; private set; }

        public int MemoryReadCalls { get; private set; }

        public int MainModuleQueries { get; private set; }

        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken)
        {
            IdentityQueries++;
            return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Available(identity));
        }

        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(nuint address, byte[] destination, CancellationToken cancellationToken)
        {
            MemoryReadCalls++;
            throw new InvalidOperationException("Discovery validation must not read virtual memory.");
        }

        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken)
        {
            MainModuleQueries++;
            throw new InvalidOperationException("Discovery validation must not query a module base.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
