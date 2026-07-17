using SoulsTracker.Infrastructure;
using SoulsTracker.Application;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class ApprovedLegacyImportLocationLocatorTests
{
    [Fact]
    public void DiscoveryUsesOnlyExactOrdinaryFilesAndNeverReadsContent()
    {
        var fileSystem = new FakeFileSystem("C:\\safe")
            .With("C:\\safe", FileAttributes.Directory)
            .With("C:\\safe\\SoulsTracker", FileAttributes.Directory)
            .With("C:\\safe\\SoulsTracker\\state.json", FileAttributes.Normal)
            .With("C:\\safe\\Soulslike Tracker", FileAttributes.Directory)
            .With("C:\\safe\\Soulslike Tracker\\state.json", FileAttributes.Directory)
            .With("C:\\safe\\DeathGambit", FileAttributes.Directory)
            .With("C:\\safe\\DeathGambit\\state.json", FileAttributes.ReparsePoint)
            .With("C:\\safe\\GambaDeck", FileAttributes.Directory)
            .WithUnauthorized("C:\\safe\\GambaDeck\\state.json");
        var locator = new ApprovedLegacyImportLocationLocator(fileSystem);

        LegacyImportCandidate candidate = Assert.Single(locator.Discover());

        Assert.Equal(LegacyImportSourceLabel.SoulsTrackerLegacySettings, candidate.Label);
        Assert.Equal(12, fileSystem.AttributeRequests);
        Assert.False(fileSystem.ContentRead);
    }

    [Fact]
    public void RevalidationRejectsSubstitutedOutsideRootHandleAndNewReparsePoint()
    {
        var fileSystem = ApprovedFileSystem();
        var locator = new ApprovedLegacyImportLocationLocator(fileSystem);
        LegacyImportCandidate discovered = Assert.Single(locator.Discover());

        Assert.False(locator.IsStillApproved(new LegacyImportCandidate(discovered.Label)));
        fileSystem.With("C:\\safe\\SoulsTracker\\state.json", FileAttributes.ReparsePoint);
        Assert.False(locator.IsStillApproved(discovered));
    }

    [Fact]
    public void ParentReparsePointIsExcludedDuringDiscoveryAndRejectedOnRevalidation()
    {
        var fileSystem = ApprovedFileSystem().With("C:\\safe\\SoulsTracker", FileAttributes.Directory | FileAttributes.ReparsePoint);
        var locator = new ApprovedLegacyImportLocationLocator(fileSystem);

        Assert.Empty(locator.Discover());

        fileSystem.With("C:\\safe\\SoulsTracker", FileAttributes.Directory);
        LegacyImportCandidate discovered = Assert.Single(locator.Discover());
        fileSystem.With("C:\\safe\\SoulsTracker", FileAttributes.Directory | FileAttributes.ReparsePoint);
        Assert.False(locator.IsStillApproved(discovered));
    }

    [Fact]
    public void ResolvedOutsideRootCandidateIsExcludedDuringDiscoveryAndRejectedOnRevalidation()
    {
        var fileSystem = ApprovedFileSystem().ResolveTo("C:\\safe\\SoulsTracker\\state.json", "C:\\outside\\state.json");
        var locator = new ApprovedLegacyImportLocationLocator(fileSystem);

        Assert.Empty(locator.Discover());

        fileSystem.ClearResolution("C:\\safe\\SoulsTracker\\state.json");
        LegacyImportCandidate discovered = Assert.Single(locator.Discover());
        fileSystem.ResolveTo("C:\\safe\\SoulsTracker\\state.json", "C:\\outside\\state.json");
        Assert.False(locator.IsStillApproved(discovered));
    }

    [Fact]
    public void LocatorContainsNoEnumerationOrContentReadSurface()
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SoulsTracker.Infrastructure", "ApprovedLegacyImportLocations.cs");
        string source = File.ReadAllText(Path.GetFullPath(sourcePath));
        Assert.DoesNotContain("Directory.Enumerate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.GetFiles", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAll", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparedCapabilityCannotBeConstructedFromRawAnalysisOrFingerprintValues()
    {
        Assert.Empty(typeof(ConfirmedLegacyImportRequest).GetConstructors());
        Assert.DoesNotContain(typeof(LegacyImportPreflightReview).GetProperties(), property =>
            property.Name.Contains("Path", StringComparison.Ordinal) ||
            property.Name.Contains("Fingerprint", StringComparison.Ordinal) ||
            property.Name.Contains("Analysis", StringComparison.Ordinal));
    }

    private static FakeFileSystem ApprovedFileSystem() => new FakeFileSystem("C:\\safe")
        .With("C:\\safe", FileAttributes.Directory)
        .With("C:\\safe\\SoulsTracker", FileAttributes.Directory)
        .With("C:\\safe\\SoulsTracker\\state.json", FileAttributes.Normal);

    private sealed class FakeFileSystem(string root) : IApprovedLegacyImportFileSystem
    {
        private readonly Dictionary<string, FileAttributes> attributes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> resolutions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unauthorized = new(StringComparer.OrdinalIgnoreCase);
        public string ApplicationDataRoot => root;
        public int AttributeRequests { get; private set; }
        public bool ContentRead { get; private set; }
        public FakeFileSystem With(string path, FileAttributes value) { attributes[path] = value; return this; }
        public FakeFileSystem ResolveTo(string path, string resolvedPath) { resolutions[path] = resolvedPath; return this; }
        public FakeFileSystem ClearResolution(string path) { resolutions.Remove(path); return this; }
        public FakeFileSystem WithUnauthorized(string path) { unauthorized.Add(path); return this; }
        public string GetFullPath(string path) => resolutions.TryGetValue(path, out string? resolvedPath) ? resolvedPath : Path.GetFullPath(path);
        public FileAttributes GetAttributes(string path)
        {
            AttributeRequests++;
            if (unauthorized.Contains(path)) throw new UnauthorizedAccessException();
            return attributes.TryGetValue(path, out FileAttributes value) ? value : throw new FileNotFoundException();
        }
    }
}
