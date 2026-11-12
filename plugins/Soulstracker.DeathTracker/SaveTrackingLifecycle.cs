using System;
using System.IO;

namespace Soulstracker.DeathTracking;

public sealed class SaveIdentity
{
#pragma warning disable CA1051 // Retain the API's existing serialized settings field.
    public string RunId = "";
#pragma warning restore CA1051
}

/// <summary>Owns save identity at the game's new-game, load and menu boundaries.</summary>
public sealed class SaveTrackingLifecycle
{
    private readonly RecordStore store;
    private readonly DeathSession session;
    private readonly Action<bool> publishStatus;
    public SaveTrackingLifecycle(RecordStore store, Guid installation, Action<bool> publishStatus)
    {
        this.store = store;
        session = new DeathSession(store, installation);
        this.publishStatus = publishStatus;
    }

    public SaveIdentity Identity { get; private set; } = new SaveIdentity();
    public bool Available => session.Available;

    public void LeaveGame()
    {
        // ReturnToMainMenu saves local settings after this boundary. Retain its identity.
        try { publishStatus(false); }
        finally { session.Close(); }
    }

    public void BeginLoad()
    {
        try { LeaveGame(); }
        finally { Identity = new SaveIdentity(); }
    }

    public void LoadLocal(SaveIdentity? settings)
    {
        try { LeaveGame(); }
        finally { Identity = settings ?? new SaveIdentity(); }
    }

    public void StartNewGame(int slot)
    {
        BeginLoad();
        Guid run = Guid.NewGuid();
        Identity.RunId = run.ToString("D");
        session.Open(run, slot, true);
        publishStatus(true);
    }

    public void LoadGame(int slot)
    {
        LeaveGame();
        bool create = string.IsNullOrEmpty(Identity.RunId);
        Guid run;
        if (create)
        {
            if (store.HasSlot(slot)) throw new IOException("Tracking identity was lost; existing records were preserved.");
            run = Guid.NewGuid();
            Identity.RunId = run.ToString("D");
        }
        else if (!Guid.TryParseExact(Identity.RunId, "D", out run) || run == Guid.Empty)
            throw new IOException("Tracking identity is invalid; existing records were preserved.");
        session.Open(run, slot, create);
        publishStatus(true);
    }

    public void Die() => session.Die();
    public void Healthy() => session.Healthy();
    public void Flush() => session.Flush();
    public void ReportStatus(bool healthy) => publishStatus(Available && healthy);
}
