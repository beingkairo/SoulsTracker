using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class BloodborneWindowProcessEnumeratorTests
{
    [Theory]
    [InlineData("Bloodborne", true)]
    [InlineData("Bloodborne™", true)]
    [InlineData(" BLOODBORNE | 60 FPS", true)]
    [InlineData("Bloodborne — Enter name", true)]
    [InlineData("NotBloodborne", false)]
    [InlineData("BloodborneTracker", false)]
    [InlineData("SoulsTracker - Bloodborne", false)]
    [InlineData("Some unrelated application", false)]
    public void OnlyCanonicalGameTitlePrefixesAreDiscoveryHints(string title, bool expected) =>
        Assert.Equal(expected, BloodborneWindowProcessEnumerator.IsEligible(new(1, 1001, title)));

    [Fact]
    public async Task HiddenOwnedOverlongAndUnrelatedWindowsAreExcludedAndMatchingPidsAreDeduplicated()
    {
        var source = new Source(new(1, 1001, "Bloodborne", Visible: false), new(2, 1002, "Bloodborne", Owned: true),
            new(3, 1003, "Bloodborne" + new string(' ', 512)), new(4, 1004, "Another game"),
            new(5, 1005, "Bloodborne™"), new(6, 1005, "Bloodborne"), new(7, Environment.ProcessId, "Bloodborne"));
        var enumerator = new BloodborneWindowProcessEnumerator(source);
        IReadOnlyList<IBloodborneProcessCandidate> candidates = await enumerator.EnumerateExactCandidatesAsync(default);
        IBloodborneProcessCandidate candidate = Assert.Single(candidates);
        Assert.Equal(1005, candidate.ProcessId);
        Assert.True(await candidate.IsExpectedGuestActiveAsync(default));
        await candidate.DisposeAsync();
    }

    [Fact]
    public async Task ClosedWindowChangedTitleAndReusedWindowHandleRejectBeforeAttachment()
    {
        var source = new Source(new BloodborneWindow(1, 1001, "Bloodborne"));
        IBloodborneProcessCandidate candidate = Assert.Single(await new BloodborneWindowProcessEnumerator(source).EnumerateExactCandidatesAsync(default));
        source.Current = null;
        Assert.False(await candidate.IsExpectedGuestActiveAsync(default));
        source.Current = new(1, 1001, "Another game");
        Assert.False(await candidate.IsExpectedGuestActiveAsync(default));
        source.Current = new(1, 1002, "Bloodborne");
        Assert.False(await candidate.IsExpectedGuestActiveAsync(default));
        source.Current = new(1, 1001, "Bloodborne");
        Assert.False(await candidate.IsExpectedGuestActiveAsync(new CancellationToken(true)));
        await candidate.DisposeAsync();
    }

    [Fact]
    public async Task CandidateAndWindowBudgetsRejectIncompleteDiscoveryInsteadOfTruncating()
    {
        foreach (int count in new[] { BloodborneWindowProcessEnumerator.MaximumCandidates + 1, BloodborneWindowProcessEnumerator.MaximumWindows + 1 })
        {
            var windows = Enumerable.Range(1, count).Select(index => new BloodborneWindow(index, 1000 + index, "Bloodborne")).ToArray();
            await Assert.ThrowsAsync<IOException>(async () => await new BloodborneWindowProcessEnumerator(new Source(windows))
                .EnumerateExactCandidatesAsync(default));
        }
    }

    [Fact]
    public async Task CancellationBeforeDiscoveryDoesNotEnumerateWindows()
    {
        var source = new Source();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new BloodborneWindowProcessEnumerator(source)
            .EnumerateExactCandidatesAsync(new CancellationToken(true)));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task NativeDiscoveryCanEnumerateThisDesktopWithoutOpeningProcessMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        IReadOnlyList<IBloodborneProcessCandidate> candidates = await new BloodborneWindowProcessEnumerator().EnumerateExactCandidatesAsync(default);
        Assert.InRange(candidates.Count, 0, BloodborneWindowProcessEnumerator.MaximumCandidates);
        Assert.Equal(candidates.Count, candidates.Select(candidate => candidate.ProcessId).Distinct().Count());
        foreach (IBloodborneProcessCandidate candidate in candidates) await candidate.DisposeAsync();
    }

    private sealed class Source(params BloodborneWindow[] windows) : IBloodborneWindowSource
    {
        public BloodborneWindow? Current { get; set; } = windows.FirstOrDefault(window => window.Visible && !window.Owned && window.Title == "Bloodborne™")
            ?? windows.FirstOrDefault();
        public int Calls { get; private set; }
        public IReadOnlyList<BloodborneWindow> Enumerate(CancellationToken cancellationToken) { Calls++; return windows; }
        public BloodborneWindow? Read(nint handle) => Current?.Handle == handle ? Current : null;
    }
}
