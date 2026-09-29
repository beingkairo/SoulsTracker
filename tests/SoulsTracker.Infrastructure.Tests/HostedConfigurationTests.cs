using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedConfigurationTests
{
    [Theory]
    [InlineData("https://overlay.beingkairo.com", true)]
    [InlineData("http://overlay.beingkairo.com", false)]
    [InlineData("https://overlay.beingkairo.com/", false)]
    [InlineData("https://overlay.beingkairo.com.evil.test", false)]
    [InlineData("https://other.beingkairo.com", false)]
    [InlineData("https://OVERLAY.beingkairo.com", false)]
    [InlineData("https://overlay.beingkairo.com:443", false)]
    [InlineData("https://arbitrary.test", false)]
    public void ProductionOriginIsExact(string origin, bool accepted)
    {
        if (accepted)
            Assert.Equal(origin, HostedPublisherConfiguration.Create(origin, new('a', 32), new('b', 64), new('c', 64), HostedProductionOrigins.Approved).DisplayOrigin);
        else
            Assert.Throws<ArgumentException>(() => HostedPublisherConfiguration.Create(origin, new('a', 32), new('b', 64), new('c', 64), HostedProductionOrigins.Approved));
    }


    [Theory]
    [InlineData("http://publisher.example.test")]
    [InlineData("https://publisher.example.test/")]
    [InlineData("https://publisher.example.test/path")]
    [InlineData("https://publisher.example.test?query")]
    [InlineData("https://publisher.example.test#fragment")]
    [InlineData("https://user@publisher.example.test")]
    public void RejectsNonOriginEvenWhenAllowlisted(string origin) => Assert.Throws<ArgumentException>(() =>
        HostedPublisherConfiguration.Create(origin, new('a', 32), new('b', 64), new('c', 64), [origin]));

    [Fact]
    public void DeniesByDefaultAndRejectsNoncanonicalOrSharedCapabilities()
    {
        Assert.Throws<ArgumentException>(() => HostedPublisherConfiguration.Create(Origin, new('a', 32), new('b', 64), new('c', 64)));
        Assert.Throws<ArgumentException>(() => HostedPublisherConfiguration.Create(Origin, new('A', 32), new('b', 64), new('c', 64), [Origin]));
        Assert.Throws<ArgumentException>(() => HostedPublisherConfiguration.Create(Origin, new('a', 32), new('b', 64), new('b', 64), [Origin]));
        Assert.Throws<ArgumentException>(() => HostedPublisherConfiguration.Create(Origin, new('a', 32), new('B', 64), new('c', 64), [Origin]));
    }

    [Fact]
    public async Task FailedProtectionAndCancellationPreservePreviousFileAndCorruptionNeverFallsBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "hosted.private");
        try
        {
            var protector = new FailingProtector();
            var store = new HostedPublisherConfigurationStore(path, protector, [Origin]);
            Assert.Null(await store.LoadAsync());
            await store.SaveAsync(Configuration());
            byte[] previous = await File.ReadAllBytesAsync(path);
            protector.Fail = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Configuration()));
            Assert.DoesNotContain("sensitive", error.ToString());
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
            protector.Fail = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Configuration(), new(true)));
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(directory));
            await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Configuration()));
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(directory));
            await File.WriteAllTextAsync(path, "{\"version\":1}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
            foreach (string malformed in new[] { "{}", "null", "{\"version\":2}", "{\"version\":1,\"origin\":42}" })
            {
                await File.WriteAllBytesAsync(path, protector.Protect(Encoding.UTF8.GetBytes(malformed)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
            }
            await File.WriteAllBytesAsync(path, new byte[16385]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FailingProtector : IStateSecretProtector
    {
        public bool Fail;
        public byte[] Protect(byte[] plaintext)
        {
            if (Fail) throw new CryptographicException("sensitive");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return new CurrentUserDpapiSecretProtector().Protect(plaintext);
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (Fail) throw new CryptographicException("sensitive");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return new CurrentUserDpapiSecretProtector().Unprotect(ciphertext);
        }
    }

    private const string Origin = "https://publisher.example.test";
    private static HostedPublisherConfiguration Configuration() => HostedPublisherConfiguration.Create(
        Origin, new('a', 32), new('b', 64), new('c', 64), [Origin]);

    [Fact]
    public async Task ProtectedFileRoundTripsWithoutPublicSecretProperties()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "hosted.private");
        try
        {
            var store = new HostedPublisherConfigurationStore(path, new CurrentUserDpapiSecretProtector(), [Origin]);
            var configuration = Configuration();
            await store.SaveAsync(configuration);
            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(configuration.BuildReadUrl(), loaded.BuildReadUrl());
            Assert.Equal(Origin + "/soulstracker/#id=" + new string('a', 32) + "&read=" + new string('b', 64), loaded.BuildReadUrl());
            Assert.DoesNotContain(new string('c', 64), loaded.BuildReadUrl());
            Assert.DoesNotContain(new string('b', 64), JsonSerializer.Serialize(configuration));
            Assert.DoesNotContain(new string('c', 64), configuration.ToString());
            string bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain(Origin, bytes);
            Assert.DoesNotContain(new string('b', 64), bytes);
            Assert.DoesNotContain(new string('c', 64), bytes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
