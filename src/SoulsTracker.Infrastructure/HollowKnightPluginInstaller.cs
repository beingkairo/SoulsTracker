using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoulsTracker.Infrastructure;

public sealed record HollowKnightInstallPlan(string Directory, Guid InstallationId, bool ReusesApi, IReadOnlyList<string> Files,
    IReadOnlyDictionary<string, byte[]> Contents, IReadOnlyDictionary<string, string?> BeforeHashes);

/// <summary>Bounded, hash-checked install transactions. Shared API components are never uninstalled.</summary>
public sealed class HollowKnightPluginInstaller
{
    public const string ApiTag = "1.5.78.11833-77";
    public const string ApiUrl = "https://github.com/hk-modding/api/releases/download/1.5.78.11833-77/moddingapi.v77.windows.zip";
    public const string ArchiveHash = "BC9F0DB3D0916B05CD5A2420BB602FB1B239CE3FF6C289FD84BFFB682FB8F1D6";
    public const string PluginFolder = "hollow_knight_Data/Managed/Mods/Soulstracker.DeathTracker";
    public const string PluginName = "Soulstracker.DeathTracker.dll";
    private const string Managed = "hollow_knight_Data/Managed/";
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Func<bool> running;
    private readonly Func<string, HollowKnightInstallation?> inspect;
    private readonly Action<int>? afterWrite;
    private static readonly Dictionary<string, string> ApiHashes = LoadHashes();

    public HollowKnightPluginInstaller() : this(HollowKnightInstallDiscovery.IsGameRunning, HollowKnightInstallDiscovery.Inspect) { }
    internal HollowKnightPluginInstaller(Func<bool> running, Func<string, HollowKnightInstallation?> inspect, Action<int>? afterWrite = null)
    { this.running = running; this.inspect = inspect; this.afterWrite = afterWrite; }

    public static async Task<byte[]> DownloadOfficialApiAsync(CancellationToken token)
    {
        using HttpResponseMessage response = await Downloads.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 8388608) throw new IOException("API archive is too large.");
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream();
        byte[] block = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + read > 8388608) throw new IOException("API archive is too large.");
            data.Write(block, 0, read);
        }
        byte[] archive = data.ToArray();
        if (Hash(archive) != ArchiveHash) throw new IOException("Official API archive verification failed.");
        return archive;
    }

    public static bool HasCompatibleApi(string root) => ApiHashes.All(pair => ExistingHash(Target(root, Managed + pair.Key)) == pair.Value);

    public static Guid? GetInstalledIdentity(string root)
    {
        if (!System.IO.Directory.Exists(Target(root, PluginFolder))) return null;
        Ownership owner = ReadOwnership(root);
        VerifyOwnedFiles(root, owner);
        return HasCompatibleApi(root) ? owner.InstallationId : null;
    }

    public HollowKnightInstallPlan Prepare(string root, byte[] plugin, byte[]? apiArchive, Guid? installationId = null)
    {
        root = Path.GetFullPath(root);
        CheckClosed(root);
        HollowKnightInstallation game = inspect(root) ?? throw new IOException("Invalid Hollow Knight game folder.");
        if (!game.Supported) throw new IOException("Unsupported version. Official API v77 supports Hollow Knight 1.5.78.11833 only.");
        bool reuse = HasCompatibleApi(root);
        if (game.HasApi && !reuse) throw new IOException("The installed Modding API is incompatible; it will not be replaced.");
        if (File.Exists(Path.Combine(root, "doorstop_config.ini")) || System.IO.Directory.Exists(Path.Combine(root, "BepInEx"))) throw new IOException("An incompatible loader is installed.");
        if (Pending(root) is { Completed: false }) throw new IOException("A previous install needs rollback before continuing.");
        var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!reuse)
        {
            if (apiArchive is null || Hash(apiArchive) != ArchiveHash) throw new IOException("A verified official API package is required.");
            using var zip = new ZipArchive(new MemoryStream(apiArchive), ZipArchiveMode.Read);
            foreach (var item in ApiHashes)
            {
                ZipArchiveEntry entry = zip.GetEntry(item.Key) ?? throw new IOException("Incomplete API archive.");
                using Stream stream = entry.Open();
                using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                byte[] contents = bytes.ToArray();
                if (Hash(contents) != item.Value) throw new IOException("API payload verification failed.");
                string relative = Managed + item.Key;
                if (item.Key is not ("Assembly-CSharp.dll" or "mscorlib.dll") && ExistingHash(Target(root, relative)) is string old && old != item.Value)
                    throw new IOException("An existing shared component would be overwritten.");
                writes.Add(relative, contents);
            }
        }
        string folder = Target(root, PluginFolder);
        Guid id = installationId is { } supplied && supplied != Guid.Empty ? supplied : Guid.NewGuid();
        if (System.IO.Directory.Exists(folder) && System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Ownership owner = ReadOwnership(root);
            VerifyOwnedFiles(root, owner);
            id = owner.InstallationId;
        }
        writes.Add(PluginFolder + "/" + PluginName, plugin.ToArray());
        writes.Add(PluginFolder + "/installation.id", Encoding.UTF8.GetBytes(id.ToString("D")));
        writes.Add(PluginFolder + "/ownership.json", JsonSerializer.SerializeToUtf8Bytes(new Ownership(id, Hash(plugin))));
        var before = writes.Keys.ToDictionary(path => path, path => ExistingHash(Target(root, path)), StringComparer.OrdinalIgnoreCase);
        return new(root, id, reuse, writes.Keys.ToArray(), writes, before);
    }

    public void Apply(HollowKnightInstallPlan plan)
    {
        CheckClosed(plan.Directory);
        if (inspect(plan.Directory) is not { Supported: true } || (plan.ReusesApi && !HasCompatibleApi(plan.Directory)))
            throw new IOException("Game compatibility changed after confirmation; nothing was written.");
        foreach (string file in plan.Files)
            if (ExistingHash(Target(plan.Directory, file)) != plan.BeforeHashes[file]) throw new IOException("Installation changed after confirmation; nothing was written.");
        string transaction = Path.Combine(plan.Directory, ".soulstracker-death-tracking", Guid.NewGuid().ToString("N"));
        HollowKnightInstallDiscovery.EnsureSafePath(transaction);
        System.IO.Directory.CreateDirectory(transaction);
        var entries = new List<JournalEntry>();
        for (int index = 0; index < plan.Files.Count; index++)
        {
            string relative = plan.Files[index];
            string path = Target(plan.Directory, relative);
            string backup = index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".backup";
            if (File.Exists(path)) File.Copy(path, Path.Combine(transaction, backup));
            entries.Add(new(relative, backup, plan.BeforeHashes[relative], Hash(plan.Contents[relative])));
        }
        var journal = new Journal(transaction, false, entries);
        WriteJournal(plan.Directory, journal);
        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                CheckClosed(plan.Directory);
                AtomicWrite(Target(plan.Directory, entries[index].RelativePath), plan.Contents[entries[index].RelativePath]);
                afterWrite?.Invoke(index);
            }
            if (!HasCompatibleApi(plan.Directory)) throw new IOException("Installed API verification failed.");
            VerifyOwnedFiles(plan.Directory, ReadOwnership(plan.Directory));
            WriteJournal(plan.Directory, journal with { Completed = true });
        }
        catch { RollbackPending(plan.Directory); throw; }
    }

    public void RollbackPending(string root)
    {
        CheckClosed(root);
        Journal journal = Pending(root) ?? throw new IOException("No installation journal found.");
        if (journal.Completed) return;
        string transactionRoot = Path.GetFullPath(Path.Combine(root, ".soulstracker-death-tracking")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(journal.BackupDirectory).StartsWith(transactionRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid recovery location.");
        if (journal.Files.Count is < 3 or > 12 || journal.Files.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
            throw new IOException("Invalid recovery entries.");
        // Validate the whole transaction before restoring any target.
        foreach (JournalEntry entry in journal.Files)
        {
            if (!(ApiHashes.Keys.Select(name => Managed + name).Concat(new[] { PluginFolder + "/" + PluginName, PluginFolder + "/installation.id", PluginFolder + "/ownership.json" })).Contains(entry.RelativePath, StringComparer.Ordinal) ||
                entry.BackupName != Path.GetFileName(entry.BackupName)) throw new IOException("Invalid recovery target.");
            string? current = ExistingHash(Target(root, entry.RelativePath));
            if (current != entry.BeforeHash && current != entry.AfterHash) throw new IOException("A recovery target changed; backups were preserved.");
            if (entry.BeforeHash is not null)
            {
                string backup = Path.Combine(journal.BackupDirectory, entry.BackupName);
                HollowKnightInstallDiscovery.EnsureSafePath(backup);
                if (ExistingHash(backup) != entry.BeforeHash) throw new IOException("Recovery backup verification failed.");
            }
        }
        foreach (JournalEntry entry in journal.Files.AsEnumerable().Reverse())
        {
            string path = Target(root, entry.RelativePath);
            string? current = ExistingHash(path);
            if (current == entry.BeforeHash) continue;
            if (current != entry.AfterHash) throw new IOException("A recovery target changed; backups were preserved.");
            if (entry.BeforeHash is null) File.Delete(path);
            else
            {
                string backup = Path.Combine(journal.BackupDirectory, entry.BackupName);
                HollowKnightInstallDiscovery.EnsureSafePath(backup);
                byte[] bytes = File.ReadAllBytes(backup);
                if (Hash(bytes) != entry.BeforeHash) throw new IOException("Recovery backup verification failed.");
                AtomicWrite(path, bytes);
            }
        }
        WriteJournal(root, journal with { Completed = true });
    }

    public void Uninstall(string root)
    {
        CheckClosed(root);
        Ownership owner = ReadOwnership(root);
        VerifyOwnedFiles(root, owner);
        string[] names = [PluginName, "installation.id", "ownership.json"];
        string backup = Target(root, ".soulstracker-death-tracking/uninstall-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(backup);
        var originals = names.ToDictionary(name => name, name => File.ReadAllBytes(Target(root, PluginFolder + "/" + name)), StringComparer.Ordinal);
        foreach (string name in names) File.WriteAllBytes(Path.Combine(backup, name), originals[name]);
        try
        {
            foreach (string name in names)
            {
                CheckClosed(root);
                string path = Target(root, PluginFolder + "/" + name);
                if (ExistingHash(path) != Hash(originals[name])) throw new IOException("Plugin files changed during uninstall.");
                File.Delete(path);
            }
            // No recursive deletion; data, API files, backups and all other mods remain.
            System.IO.Directory.Delete(Target(root, PluginFolder), false);
        }
        catch
        {
            foreach (string name in names)
                if (!File.Exists(Target(root, PluginFolder + "/" + name))) AtomicWrite(Target(root, PluginFolder + "/" + name), originals[name]);
            throw;
        }
    }

    private void CheckClosed(string root)
    {
        HollowKnightInstallDiscovery.EnsureSafePath(root);
        if (running()) throw new IOException("Close Hollow Knight before changing death tracking.");
    }
    private static string Target(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || relative.Contains("..", StringComparison.Ordinal)) throw new IOException("Invalid installation path.");
        HollowKnightInstallDiscovery.EnsureSafePath(result);
        return result;
    }
    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string? ExistingHash(string path)
    {
        HollowKnightInstallDiscovery.EnsureSafePath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16777216) throw new IOException("Unexpectedly large installation file.");
        return Hash(File.ReadAllBytes(path));
    }
    private static void AtomicWrite(string path, byte[] contents)
    {
        HollowKnightInstallDiscovery.EnsureSafePath(path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(contents); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static Ownership ReadOwnership(string root)
    {
        try
        {
            string file = Target(root, PluginFolder + "/ownership.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 2048) throw new IOException("Unknown plugin ownership; existing files were preserved.");
            return JsonSerializer.Deserialize<Ownership>(File.ReadAllBytes(file)) ?? throw new IOException("Unknown plugin ownership.");
        }
        catch (JsonException exception) { throw new IOException("Invalid plugin ownership; existing files were preserved.", exception); }
    }
    private static void VerifyOwnedFiles(string root, Ownership owner)
    {
        string folder = Target(root, PluginFolder);
        if (owner.InstallationId == Guid.Empty || ExistingHash(Path.Combine(folder, PluginName)) != owner.PluginHash ||
            File.ReadAllText(Path.Combine(folder, "installation.id")) != owner.InstallationId.ToString("D") ||
            System.IO.Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Except([PluginName, "installation.id", "ownership.json"], StringComparer.OrdinalIgnoreCase).Any())
            throw new IOException("Plugin files were changed or are not owned by this installer.");
    }
    private static Journal? Pending(string root)
    {
        string file = Target(root, ".soulstracker-death-tracking/pending.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(file)) ?? throw new IOException("Invalid install journal.") : null;
    }
    private static void WriteJournal(string root, Journal journal) => AtomicWrite(Target(root, ".soulstracker-death-tracking/pending.json"), JsonSerializer.SerializeToUtf8Bytes(journal));
    private static Dictionary<string, string> LoadHashes()
    {
        using Stream stream = typeof(HollowKnightPluginInstaller).Assembly.GetManifestResourceStream("SoulsTracker.Infrastructure.HollowKnightApiV77.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    private sealed record Ownership(Guid InstallationId, string PluginHash);
    private sealed record JournalEntry(string RelativePath, string BackupName, string? BeforeHash, string AfterHash);
    private sealed record Journal(string BackupDirectory, bool Completed, List<JournalEntry> Files);
}
