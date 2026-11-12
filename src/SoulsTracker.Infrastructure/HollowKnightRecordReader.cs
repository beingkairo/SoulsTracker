using System.Diagnostics;
using System.Globalization;
using Soulstracker.DeathTracking;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Reads the plugin's atomic records. Never infers or increments deaths.</summary>
public sealed class HollowKnightRecordReader : IRuntimeGameDeathReader
{
    private HollowKnightConfiguration configuration = HollowKnightConfiguration.Default;
    private readonly string recordsRoot;
    private readonly Func<bool> running;
    private readonly Func<int, long, bool> matchesProcess;
    private readonly Func<DateTimeOffset> now;
    private string? inspectedDirectory;
    private string setupStatus = "Not installed";
    public HollowKnightRecordReader() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoulsTracker", "HollowKnightRecords"), HollowKnightInstallDiscovery.IsGameRunning, MatchesProcess, () => DateTimeOffset.UtcNow)
    { }
    internal HollowKnightRecordReader(string root, Func<bool> running, Func<int, long, bool> matchesProcess, Func<DateTimeOffset> now)
    { recordsRoot = root; this.running = running; this.matchesProcess = matchesProcess; this.now = now; }
    public GameId GameId => GameId.HollowKnight;
    public void Configure(HollowKnightConfiguration value) => Volatile.Write(ref configuration, value);

    public ValueTask<RuntimeGameReadResult?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HollowKnightConfiguration config = Volatile.Read(ref configuration);
        RuntimeGameReadResult Result(PersistentPluginObservation? value, string status) => RuntimeGameReadResult.FromHollowKnight(value, status);
        try
        {
            if (config.InstallationId is not { } installation || installation == Guid.Empty || config.GameDirectory is null)
            {
                if (config.GameDirectory != inspectedDirectory)
                {
                    inspectedDirectory = config.GameDirectory;
                    setupStatus = inspectedDirectory is not null && HollowKnightInstallDiscovery.Inspect(inspectedDirectory) is { Supported: false } ? "Unsupported version" : "Not installed";
                }
                return ValueTask.FromResult<RuntimeGameReadResult?>(Result(null, setupStatus));
            }
            string root = Path.Combine(recordsRoot, installation.ToString("D"));
            string active = ReadBounded(Path.Combine(root, "active.record"));
            DeathRecord record = DeathRecord.Decode(active) ?? throw new IOException("Invalid tracking record.");
            if (record.Installation != installation) throw new IOException("Tracking installation mismatch.");
            // A crash between the per-run and active writes must never publish a stale active value.
            if (ReadBounded(Path.Combine(root, record.Run.ToString("D") + ".record")) != active) throw new IOException("Incomplete record update.");
            bool gameRunning = running();
            if (gameRunning && !IsActive(root, installation))
                return ValueTask.FromResult<RuntimeGameReadResult?>(Result(null, "Tracking unavailable"));
            // A heartbeat from another save must not validate an earlier record snapshot.
            // Recheck both files after liveness validation, including offline transitions.
            if (ReadBounded(Path.Combine(root, record.Run.ToString("D") + ".record")) != active ||
                ReadBounded(Path.Combine(root, "active.record")) != active)
                return ValueTask.FromResult<RuntimeGameReadResult?>(Result(null, "Tracking unavailable"));
            var observation = new PersistentPluginObservation(EffectiveDeathTotalResult.HollowKnightSourceIdentityFor(config), record.Run, record.Slot, record.Total, record.Revision, now());
            return ValueTask.FromResult<RuntimeGameReadResult?>(Result(observation, gameRunning ? "Tracking active" : "Saved total (game closed)"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return ValueTask.FromResult<RuntimeGameReadResult?>(Result(null, "Tracking unavailable")); }
    }

    private bool IsActive(string root, Guid installation)
    {
        string[] fields = ReadBounded(Path.Combine(root, "tracking.status")).Split('\n');
        return fields.Length == 7 && fields[0] == "SOULSTRACKER-HK-STATUS-1" && fields[1] == installation.ToString("D") &&
            int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
            long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long started) &&
            long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks &&
            now().UtcTicks - ticks is >= 0 and <= 100000000 && fields[5] == "active" && fields[6].Length == 0 && matchesProcess(pid, started);
    }

    private static string ReadBounded(string file)
    {
        HollowKnightInstallDiscovery.EnsureSafePath(file);
        if (new FileInfo(file).Length > 2048) throw new IOException("Tracking record is too large.");
        return File.ReadAllText(file);
    }
    private static bool MatchesProcess(int pid, long ticks)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited && HollowKnightInstallDiscovery.IsGameProcessName(process.ProcessName) && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
