using System.Security;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using SoulsTracker.Application;

namespace SoulsTracker.Infrastructure;

/// <summary>
/// Creates a verified, same-directory backup for one explicitly supplied legacy
/// candidate. It does not discover candidates or apply an import.
/// </summary>
public sealed class LegacyImportPreflight
{
    private const string BackupFilePrefix = "soulstracker-legacy-backup-";
    private const int MaximumBackupNameAttempts = 1_000;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Action? afterBackupCreated;

    /// <summary>
    /// Creates a preflight service. Callers cannot control backup paths or names.
    /// </summary>
    public LegacyImportPreflight(Func<DateTimeOffset>? utcNow = null)
        : this(utcNow, afterBackupCreated: null)
    {
    }

    // This is intentionally internal: deterministic verification-race tests need
    // to mutate only their disposable source after backup creation.
    internal LegacyImportPreflight(Func<DateTimeOffset>? utcNow, Action? afterBackupCreated)
    {
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.afterBackupCreated = afterBackupCreated;
    }

    /// <summary>
    /// Reads one explicitly supplied file, analyzes it before every write, and
    /// creates a verified immutable backup only for analyzable input.
    /// </summary>
    public LegacyImportPreflightResult Prepare(string candidateSourcePath)
    {
        if (string.IsNullOrWhiteSpace(candidateSourcePath) || !File.Exists(candidateSourcePath))
        {
            return LegacyImportPreflightResult.Unavailable();
        }

        byte[] sourceBytes;
        try
        {
            sourceBytes = File.ReadAllBytes(candidateSourcePath);
        }
        catch (IOException)
        {
            return LegacyImportPreflightResult.Unavailable();
        }
        catch (UnauthorizedAccessException)
        {
            return LegacyImportPreflightResult.Unavailable();
        }
        catch (ArgumentException)
        {
            return LegacyImportPreflightResult.Unavailable();
        }
        catch (NotSupportedException)
        {
            return LegacyImportPreflightResult.Unavailable();
        }
        catch (SecurityException)
        {
            return LegacyImportPreflightResult.Unavailable();
        }

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(sourceBytes);
        string sourceFingerprint = Fingerprint(sourceBytes);
        if (analysis.IsRejected)
        {
            return LegacyImportPreflightResult.Rejected(analysis, sourceFingerprint);
        }

        string? backupPath = TryCreateBackup(candidateSourcePath, sourceBytes);
        if (backupPath is null)
        {
            return LegacyImportPreflightResult.Unavailable();
        }

        try
        {
            try
            {
                afterBackupCreated?.Invoke();
            }
            catch (Exception)
            {
                return LegacyImportPreflightResult.VerificationFailed(analysis, sourceFingerprint, null);
            }

            byte[] verificationSourceBytes = File.ReadAllBytes(candidateSourcePath);
            byte[] backupBytes = File.ReadAllBytes(backupPath);
            string backupFingerprint = Fingerprint(backupBytes);
            if (!verificationSourceBytes.AsSpan().SequenceEqual(sourceBytes) ||
                !backupBytes.AsSpan().SequenceEqual(sourceBytes))
            {
                return LegacyImportPreflightResult.VerificationFailed(analysis, sourceFingerprint, backupFingerprint);
            }

            return LegacyImportPreflightResult.Prepared(analysis, sourceFingerprint, backupFingerprint);
        }
        catch (IOException)
        {
            return LegacyImportPreflightResult.VerificationFailed(analysis, sourceFingerprint, null);
        }
        catch (UnauthorizedAccessException)
        {
            return LegacyImportPreflightResult.VerificationFailed(analysis, sourceFingerprint, null);
        }
        catch (ArgumentException)
        {
            return LegacyImportPreflightResult.VerificationFailed(analysis, sourceFingerprint, null);
        }
        catch (NotSupportedException)
        {
            return LegacyImportPreflightResult.VerificationFailed(analysis, sourceFingerprint, null);
        }
        catch (SecurityException)
        {
            return LegacyImportPreflightResult.VerificationFailed(analysis, sourceFingerprint, null);
        }
    }

    private string? TryCreateBackup(string candidateSourcePath, byte[] sourceBytes)
    {
        string directory;
        string timestamp;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(candidateSourcePath)) ?? string.Empty;
            timestamp = utcNow().ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }
        for (int attempt = 0; attempt < MaximumBackupNameAttempts; attempt++)
        {
            string backupPath = Path.Combine(directory, $"{BackupFilePrefix}{timestamp}-{attempt:D4}.json");
            try
            {
                using FileStream backup = new(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                backup.Write(sourceBytes);
                backup.Flush(flushToDisk: true);
                return backupPath;
            }
            catch (IOException) when (File.Exists(backupPath))
            {
                continue;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private static string Fingerprint(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
}

/// <summary>
/// Contains a secrets-safe preflight outcome. Source and backup locations are
/// intentionally not retained or exposed.
/// </summary>
public sealed class LegacyImportPreflightResult
{
    private LegacyImportPreflightResult(
        LegacyImportPreflightOutcome outcome,
        LegacyStateAnalysis? analysis,
        string? sourceFingerprint,
        string? backupFingerprint)
    {
        Outcome = outcome;
        Analysis = analysis;
        SourceFingerprint = sourceFingerprint;
        BackupFingerprint = backupFingerprint;
    }

    public LegacyImportPreflightOutcome Outcome { get; }

    [JsonIgnore]
    public LegacyStateAnalysis? Analysis { get; }

    public string? SourceFingerprint { get; }

    public string? BackupFingerprint { get; }

    internal static LegacyImportPreflightResult Prepared(
        LegacyStateAnalysis analysis,
        string sourceFingerprint,
        string backupFingerprint) =>
        new(LegacyImportPreflightOutcome.Prepared, analysis, sourceFingerprint, backupFingerprint);

    internal static LegacyImportPreflightResult Rejected(LegacyStateAnalysis analysis, string sourceFingerprint) =>
        new(LegacyImportPreflightOutcome.Rejected, analysis, sourceFingerprint, null);

    internal static LegacyImportPreflightResult Unavailable() =>
        new(LegacyImportPreflightOutcome.Unavailable, null, null, null);

    internal static LegacyImportPreflightResult VerificationFailed(
        LegacyStateAnalysis analysis,
        string sourceFingerprint,
        string? backupFingerprint) =>
        new(LegacyImportPreflightOutcome.VerificationFailed, analysis, sourceFingerprint, backupFingerprint);
}

/// <summary>
/// A small safe status set that intentionally does not encode filesystem or
/// exception details.
/// </summary>
public enum LegacyImportPreflightOutcome
{
    Prepared,
    Rejected,
    Unavailable,
    VerificationFailed,
}
