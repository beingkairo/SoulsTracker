using System;
using System.IO;

namespace Soulstracker.DeathTracking;

/// <summary>Counts only a game Die hook, with a latch that spans respawn and repeated hook calls.</summary>
public sealed class DeathSession
{
    private readonly RecordStore store;
    private readonly Guid installation;
    private DeathRecord? current;
    private bool dying;
    private bool dirty;
    private bool needsBinding;
    public DeathSession(RecordStore store, Guid installation) { this.store = store; this.installation = installation; }
    public bool Available => current != null;

    public void Open(Guid run, int slot, bool newRun)
    {
        Close();
        DeathRecord? saved = store.Load(run);
        if (saved == null && !newRun) throw new IOException("The saved tracking record is missing.");
        if (saved != null && (saved.Installation != installation || saved.Slot != slot)) throw new IOException("Tracking identity mismatch.");
        current = saved ?? new DeathRecord(installation, run, slot, 0, 1, DateTime.UtcNow.Ticks);
        dirty = true;
        needsBinding = true;
        Flush();
    }

    public void Die()
    {
        if (current == null || dying) return;
        dying = true;
        var next = new DeathRecord(installation, current.Run, current.Slot, checked(current.Total + 1), checked(current.Revision + 1), DateTime.UtcNow.Ticks);
        // Keep the increment pending through I/O failure; retries write the same revision, never another increment.
        current = next;
        dirty = true;
        Flush();
    }

    public void Healthy() => dying = false;
    public void Flush()
    {
        if (current == null || !dirty) return;
        if (needsBinding) { store.Bind(current); needsBinding = false; }
        store.Commit(current);
        dirty = false;
    }
    public void Close() { try { Flush(); } finally { current = null; dying = false; dirty = false; needsBinding = false; } }
}
