using System.Reflection;
using System.Text.Json;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class LegacyImportPreflightTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 12, 3, 4, 5, 678, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTrackerPreflightTests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void AcceptedRedactedFixtureCreatesByteExactSameDirectoryBackupAndPreparedAnalysis()
    {
        string sourcePath = WriteCandidate(ReadAcceptedFixture());
        byte[] before = File.ReadAllBytes(sourcePath);

        LegacyImportPreflightResult result = new LegacyImportPreflight(() => FixedTime).Prepare(sourcePath);

        Assert.Equal(LegacyImportPreflightOutcome.Prepared, result.Outcome);
        Assert.False(Assert.IsType<SoulsTracker.Application.LegacyStateAnalysis>(result.Analysis).IsRejected);
        Assert.Equal(GameId.Ds3, result.Analysis.Proposal!.SelectedGameId);
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
        string backupPath = Assert.Single(Directory.GetFiles(root, "soulstracker-legacy-backup-*.json"));
        Assert.Equal(root, Path.GetDirectoryName(backupPath));
        Assert.Equal(before, File.ReadAllBytes(backupPath));
        Assert.Equal(result.SourceFingerprint, result.BackupFingerprint);
        Assert.Matches("^[0-9A-F]{64}$", result.SourceFingerprint!);
    }

    [Fact]
    public void RejectedMalformedCandidateCreatesNoBackupAndReturnsNoFilesystemDetail()
    {
        string sourcePath = WriteCandidate("{\"unfinished\":");
        byte[] before = File.ReadAllBytes(sourcePath);

        LegacyImportPreflightResult result = new LegacyImportPreflight(() => FixedTime).Prepare(sourcePath);

        Assert.Equal(LegacyImportPreflightOutcome.Rejected, result.Outcome);
        Assert.True(Assert.IsType<SoulsTracker.Application.LegacyStateAnalysis>(result.Analysis).IsRejected);
        Assert.Null(result.BackupFingerprint);
        Assert.Empty(Directory.GetFiles(root, "soulstracker-legacy-backup-*.json"));
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
        Assert.DoesNotContain(sourcePath, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingBackupNameIsNeverOverwritten()
    {
        string sourcePath = WriteCandidate(ReadAcceptedFixture());
        string collisionPath = Path.Combine(root, "soulstracker-legacy-backup-20260712T030405678Z-0000.json");
        byte[] collisionBytes = [0x01, 0x02, 0x03];
        File.WriteAllBytes(collisionPath, collisionBytes);

        LegacyImportPreflightResult result = new LegacyImportPreflight(() => FixedTime).Prepare(sourcePath);

        Assert.Equal(LegacyImportPreflightOutcome.Prepared, result.Outcome);
        Assert.Equal(collisionBytes, File.ReadAllBytes(collisionPath));
        string[] backups = Directory.GetFiles(root, "soulstracker-legacy-backup-*.json");
        Assert.Equal(2, backups.Length);
        Assert.DoesNotContain(collisionPath, backups.Single(path => !string.Equals(path, collisionPath, StringComparison.Ordinal)));
    }

    [Fact]
    public void ChangedSourceDuringVerificationIsNeverPreparedAndCreatedBackupIsPreserved()
    {
        string sourcePath = WriteCandidate(ReadAcceptedFixture());
        byte[] original = File.ReadAllBytes(sourcePath);
        var preflight = new LegacyImportPreflight(
            () => FixedTime,
            () => File.WriteAllText(sourcePath, "{\"changed\":true}"));

        LegacyImportPreflightResult result = preflight.Prepare(sourcePath);

        Assert.Equal(LegacyImportPreflightOutcome.VerificationFailed, result.Outcome);
        Assert.NotEqual(original, File.ReadAllBytes(sourcePath));
        string backupPath = Assert.Single(Directory.GetFiles(root, "soulstracker-legacy-backup-*.json"));
        Assert.Equal(original, File.ReadAllBytes(backupPath));
    }

    [Fact]
    public void CallbackFailureIsContainedAsSafeVerificationFailure()
    {
        string sourcePath = WriteCandidate(ReadAcceptedFixture());

        LegacyImportPreflightResult result = new LegacyImportPreflight(
            () => FixedTime,
            () => throw new InvalidOperationException("test callback failure")).Prepare(sourcePath);

        Assert.Equal(LegacyImportPreflightOutcome.VerificationFailed, result.Outcome);
        Assert.Single(Directory.GetFiles(root, "soulstracker-legacy-backup-*.json"));
        Assert.DoesNotContain("test callback failure", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void PublicResultSerializesOnlySafeMetadataWithoutPathsPayloadOrExceptionDetails()
    {
        const string secretValue = "sensitive-legacy-value";
        string sourcePath = WriteCandidate($"{{\"settings\":{{\"death_sfx_path\":\"{secretValue}\"}}}}");

        LegacyImportPreflightResult result = new LegacyImportPreflight(() => FixedTime).Prepare(sourcePath);
        string serialized = JsonSerializer.Serialize(result);

        Assert.Equal(LegacyImportPreflightOutcome.Prepared, result.Outcome);
        Assert.DoesNotContain(root, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(LegacyImportPreflightResult).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name.Contains("Path", StringComparison.Ordinal) ||
                property.Name.Contains("Name", StringComparison.Ordinal) ||
                property.PropertyType == typeof(Exception));
        Assert.All(
            typeof(LegacyImportPreflightResult).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.False(property.CanWrite));
    }

    [Fact]
    public void AcceptedFixtureSerializationOmitsInMemoryAnalysisAndAllRawFixtureValues()
    {
        string sourcePath = WriteCandidate(ReadAcceptedFixture());

        LegacyImportPreflightResult result = new LegacyImportPreflight(() => FixedTime).Prepare(sourcePath);
        string serialized = JsonSerializer.Serialize(result);

        Assert.Equal(LegacyImportPreflightOutcome.Prepared, result.Outcome);
        Assert.NotNull(result.Analysis);
        Assert.DoesNotContain("Analysis", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("boss_defeats_by_game", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("death_sfx_path", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("area-0-0-0:2576", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingExplicitCandidateReturnsSafeUnavailableOutcomeWithoutWriting()
    {
        string missingPath = Path.Combine(root, "missing.json");

        LegacyImportPreflightResult result = new LegacyImportPreflight(() => FixedTime).Prepare(missingPath);

        Assert.Equal(LegacyImportPreflightOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Analysis);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void ComponentSourceHasNoDiscoveryUiOrStateApplicationWiring()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "SoulsTracker.Infrastructure",
            "LegacyImportPreflight.cs"));
        string source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("Directory.Enumerate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.GetFiles", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistentTrackerState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SoulsTracker.Desktop", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", source, StringComparison.Ordinal);
    }

    private string WriteCandidate(byte[] content)
    {
        string sourcePath = Path.Combine(root, "candidate.json");
        File.WriteAllBytes(sourcePath, content);
        return sourcePath;
    }

    private string WriteCandidate(string content) => WriteCandidate(System.Text.Encoding.UTF8.GetBytes(content));

    private static byte[] ReadAcceptedFixture()
    {
        string fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "docs",
            "fixtures",
            "legacy-state.real-redacted.json"));
        return File.ReadAllBytes(fixturePath);
    }
}
