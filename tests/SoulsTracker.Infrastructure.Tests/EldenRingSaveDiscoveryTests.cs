using System.Buffers.Binary;
using System.Text;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class EldenRingSaveDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.EldenDiscovery", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DiscoversParserValidSavesAtExactlyOneAccountLevel(int accountCount)
    {
        Directory.CreateDirectory(root);
        for (int index = 0; index < accountCount; index++) WriteSave(Path.Combine(root, $"account-{index}"), CreateProfile((index, $"Character {index}", 10 + index)));
        string nested = Path.Combine(root, "outer", "inner");
        WriteSave(nested, CreateProfile((0, "Nested", 20)));
        File.WriteAllBytes(Path.Combine(root, "ER0000.sl2"), CreateProfile((0, "Root", 20)));

        var discovery = new EldenRingSaveDiscovery(new TestRootSource(root));

        IReadOnlyList<DiscoveredLocalSave> saves = await discovery.DiscoverAsync(default);

        Assert.Equal(accountCount, saves.Count);
        Assert.Equal(Enumerable.Range(1, accountCount).Select(index => $"Save {index}"), saves.Select(static save => save.Label));
        Assert.All(saves, save =>
        {
            Assert.DoesNotContain("account", save.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, save.Label, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task RejectsMalformedWrongNamedAndOversizedCandidates()
    {
        string valid = Path.Combine(root, "valid");
        WriteSave(valid, CreateProfile((0, "Valid", 20)));
        string malformed = Path.Combine(root, "malformed");
        WriteSave(malformed, [1, 2, 3]);
        string wrong = Path.Combine(root, "wrong");
        Directory.CreateDirectory(wrong);
        File.WriteAllBytes(Path.Combine(wrong, "other.sl2"), CreateProfile((0, "Wrong", 20)));
        string oversized = Path.Combine(root, "oversized");
        Directory.CreateDirectory(oversized);
        File.WriteAllBytes(Path.Combine(oversized, "ER0000.sl2"), new byte[64 * 1024 * 1024 + 1]);

        var discovery = new EldenRingSaveDiscovery(new TestRootSource(root));

        Assert.Single(await discovery.DiscoverAsync(default));
    }

    [Fact]
    public async Task HonorsCancellationBeforeAccountInspection()
    {
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var discovery = new EldenRingSaveDiscovery(new TestRootSource(root));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await discovery.DiscoverAsync(cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static void WriteSave(string account, byte[] contents)
    {
        Directory.CreateDirectory(account);
        File.WriteAllBytes(Path.Combine(account, "ER0000.sl2"), contents);
    }

    private sealed record TestRootSource(string Root) : IEldenRingSaveRootSource
    {
        public string? GetSaveRoot() => Root;
    }

    internal static byte[] CreateProfile(params (int Index, string Name, int Level)[] profiles)
    {
        const int headerSize = 0x40;
        const int entryHeaderSize = 0x20;
        const int entryIndex = 10;
        const int dataOffset = 0x1000;
        const int entrySize = 0x5000;
        const int summaryOffset = 0x1964;
        const int profileSize = 0x24C;
        byte[] file = new byte[dataOffset + entrySize];
        "BND4"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), 11);
        int header = headerSize + entryIndex * entryHeaderSize;
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(header + 0x08), entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(header + 0x10), dataOffset);
        foreach ((int index, string name, int level) in profiles)
        {
            file[dataOffset + summaryOffset + index] = 1;
            int profile = dataOffset + summaryOffset + 10 + index * profileSize;
            Encoding.Unicode.GetBytes(name).AsSpan(0, Math.Min(32, name.Length * 2)).CopyTo(file.AsSpan(profile, 32));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(profile + 0x22), (uint)level);
        }
        return file;
    }
}
