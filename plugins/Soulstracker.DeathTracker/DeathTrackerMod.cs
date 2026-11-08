using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Modding;

namespace Soulstracker.DeathTracking;

public sealed class SaveIdentity
{
    public string RunId = "";
}

/// <summary>Official API adapter. All counting originates in the game's Die hook.</summary>
public sealed class DeathTrackerMod : Mod, ILocalSettings<SaveIdentity>
{
    private SaveIdentity identity = new SaveIdentity();
    private DeathSession? session;
    private RecordStore? store;
    private Guid installation;
    private DateTime nextFlush;
    private DateTime nextSample;
    private static readonly PropertyInfo HeroInstance = typeof(HeroController).GetProperty("instance", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly FieldInfo HeroState = typeof(HeroController).GetField("cState")!;
    private static readonly FieldInfo DeadState = HeroState.FieldType.GetField("dead")!;
    private bool newGameSeen;
    private bool failed;
    private readonly int processId;
    private readonly long startedTicks;

    public DeathTrackerMod() : base("Soulstracker.DeathTracker")
    {
        using (Process process = Process.GetCurrentProcess()) { processId = process.Id; startedTicks = process.StartTime.ToUniversalTime().Ticks; }
    }
    public override string GetVersion() => "2.2.0";
    public void OnLoadLocal(SaveIdentity settings) { Guard(() => { session?.Close(); store?.WriteStatus(installation, processId, startedTicks, false); }); identity = settings ?? new SaveIdentity(); }
    public SaveIdentity OnSaveLocal() => identity;

    public override void Initialize()
    {
        if (!HollowKnightCompatibility.SupportsRuntime(typeof(Constants).GetField("GAME_VERSION")?.GetRawConstantValue() as string, ModHooks.ModVersion))
            throw new NotSupportedException("Unsupported Hollow Knight and Modding API combination.");
        string directory = Path.GetDirectoryName(typeof(DeathTrackerMod).Assembly.Location)!;
        installation = Guid.Parse(File.ReadAllText(Path.Combine(directory, "installation.id")));
        store = new RecordStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulsTracker", "HollowKnightRecords", installation.ToString("D")));
        session = new DeathSession(store, installation);
        // The API does not invoke OnLoadLocal when a save has no settings for this mod.
        // Clear the previous run before the API loads a different save's sidecar.
        On.GameManager.LoadGame += (original, manager, slot, callback) =>
        {
            Guard(() => { session.Close(); store.WriteStatus(installation, processId, startedTicks, false); });
            identity = new SaveIdentity();
            newGameSeen = false;
            original(manager, slot, callback);
        };
        ModHooks.SavegameLoadHook += slot => Guard(() => Open(slot, false));
        ModHooks.NewGameHook += () => Guard(() => { if (!newGameSeen) { newGameSeen = true; identity = new SaveIdentity(); Open(CurrentSlot(), true); } });
        ModHooks.BeforeSceneLoadHook += scene =>
        {
            if (scene == "Menu_Title" || scene == "Quit_To_Menu") { Guard(() => { session.Close(); store.WriteStatus(installation, processId, startedTicks, false); }); newGameSeen = false; }
            return scene;
        };
        On.HeroController.Die += Die;
        ModHooks.HeroUpdateHook += Update;
        ModHooks.ApplicationQuitHook += () => Guard(() => { session.Flush(); store.WriteStatus(installation, processId, startedTicks, false); });
        store.WriteStatus(installation, processId, startedTicks, false);
    }

    private void Open(int slot, bool newGame)
    {
        failed = false;
        if (!newGame) newGameSeen = false;
        bool create = !Guid.TryParse(identity.RunId, out Guid run);
        if (create)
        {
            if (!newGame && store!.HasSlot(slot)) throw new IOException("Tracking identity was lost; existing records were preserved.");
            run = Guid.NewGuid(); identity.RunId = run.ToString("D");
        }
        session!.Open(run, slot, create);
        store!.WriteStatus(installation, processId, startedTicks, true);
    }

    private IEnumerator Die(On.HeroController.orig_Die original, HeroController hero)
    {
        Guard(() => { if (!IsDead(hero)) session!.Die(); });
        return original(hero);
    }

    private void Update()
    {
        if (DateTime.UtcNow < nextSample) return;
        nextSample = DateTime.UtcNow.AddMilliseconds(100);
        // Positive health only rearms the hook latch. It never creates a death event.
        object? hero = HeroInstance.GetValue(null, null);
        if (hero != null && PlayerData.instance.GetInt("health") > 0 && !IsDead(hero)) session!.Healthy();
        if (DateTime.UtcNow < nextFlush) return;
        nextFlush = DateTime.UtcNow.AddSeconds(2);
        Guard(() => { session!.Flush(); failed = false; });
        try { store!.WriteStatus(installation, processId, startedTicks, session!.Available && !failed); }
        catch (IOException) { failed = true; }
        catch (UnauthorizedAccessException) { failed = true; }
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is OverflowException ||
            exception is InvalidOperationException || exception is TargetException || exception is TargetInvocationException || exception is NullReferenceException)
        {
            failed = true;
            try { store?.WriteStatus(installation, processId, startedTicks, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            LogError("Death tracking unavailable: " + exception.Message);
        }
    }

    private static int CurrentSlot()
    {
        // Reflection avoids a dependency on Unity assemblies in the one-DLL plugin build.
        object manager = typeof(GameManager).GetProperty("instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null, null)!;
        return (int)typeof(GameManager).GetField("profileID")!.GetValue(manager)!;
    }

    private static bool IsDead(object hero)
    {
        object states = HeroState.GetValue(hero)!;
        return (bool)DeadState.GetValue(states)!;
    }
}
