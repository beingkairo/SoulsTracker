using System;
using System.IO;
using System.Text;

namespace Soulstracker.DeathTracking;

/// <summary>Plugin-owned durable records. Invalid existing state never silently becomes zero.</summary>
public sealed class RecordStore
{
    private readonly string root;
    public RecordStore(string directory) { root = Path.GetFullPath(directory); }

    public DeathRecord? Load(Guid run)
    {
        string path = Path.Combine(root, run.ToString("D") + ".record");
        if (!File.Exists(path)) return null;
        SafePath(path);
        var info = new FileInfo(path);
        if (info.Length > 2048) throw new IOException("Death record is too large.");
        DeathRecord? record = DeathRecord.Decode(File.ReadAllText(path, Encoding.UTF8));
        if (record == null || record.Run != run) throw new IOException("Death record is corrupt; tracking stopped.");
        return record;
    }

    public void Commit(DeathRecord record)
    {
        AtomicWrite(Path.Combine(root, record.Run.ToString("D") + ".record"), record.Encode());
        Publish(record);
    }

    public void Publish(DeathRecord record) => AtomicWrite(Path.Combine(root, "active.record"), record.Encode());

    public bool HasSlot(int slot) => File.Exists(Path.Combine(root, "slot-" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".binding"));
    public void Bind(DeathRecord record) => AtomicWrite(Path.Combine(root, "slot-" + record.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".binding"), record.Run.ToString("D"));

    public void WriteStatus(Guid installation, int processId, long startedTicks, bool available)
    {
        AtomicWrite(Path.Combine(root, "tracking.status"), string.Join("\n", "SOULSTRACKER-HK-STATUS-1", installation.ToString("D"),
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture), startedTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), available ? "active" : "unavailable") + "\n");
    }

    private void AtomicWrite(string path, string text)
    {
        SafePath(path);
        Directory.CreateDirectory(root);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void SafePath(string path)
    {
        string? cursor = path;
        while (cursor != null)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked record locations are unsupported.");
            cursor = Path.GetDirectoryName(cursor);
        }
    }
}
