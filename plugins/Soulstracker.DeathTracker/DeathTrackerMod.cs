using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Modding;

namespace Soulstracker.DeathTracking;

/// <summary>Official API adapter. All counting originates in the game's Die hook.</summary>
public sealed class DeathTrackerMod : Mod, ILocalSettings<SaveIdentity>
{
    private SaveIdentity initialIdentity = new SaveIdentity();
    private SaveTrackingLifecycle? tracking;
    private Guid installation;
    private DateTime nextFlush;
    private DateTime nextSample;
    private static readonly PropertyInfo HeroInstance = typeof(HeroController).GetProperty("instance", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly FieldInfo HeroState = typeof(HeroController).GetField("cState")!;
    private static readonly FieldInfo DeadState = HeroState.FieldType.GetField("dead")!;
    private bool failed;
    private readonly int processId;
    private readonly long startedTicks;

    public DeathTrackerMod() : base("Soulstracker.DeathTracker")
    {
        using (Process process = Process.GetCurrentProcess()) { processId = process.Id; startedTicks = process.StartTime.ToUniversalTime().Ticks; }
    }
    public override string GetVersion() => "2.2.0";
    public void OnLoadLocal(SaveIdentity settings)
    {
        initialIdentity = settings ?? new SaveIdentity();
        if (tracking != null) Guard(() => tracking.LoadLocal(initialIdentity));
    }
    public SaveIdentity OnSaveLocal() => tracking?.Identity ?? initialIdentity;

    public override void Initialize()
    {
        if (!HollowKnightCompatibility.SupportsRuntime(typeof(Constants).GetField("GAME_VERSION")?.GetRawConstantValue() as string, ModHooks.ModVersion))
            throw new NotSupportedException("Unsupported Hollow Knight and Modding API combination.");
        string directory = Path.GetDirectoryName(typeof(DeathTrackerMod).Assembly.Location)!;
        installation = Guid.Parse(File.ReadAllText(Path.Combine(directory, "installation.id")));
        var store = new RecordStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulsTracker", "HollowKnightRecords", installation.ToString("D")));
        tracking = new SaveTrackingLifecycle(store, installation, active => store.WriteStatus(installation, processId, startedTicks, active));
        tracking.LoadLocal(initialIdentity);
        // The API does not invoke OnLoadLocal when a save has no settings for this mod.
        // Clear the previous run before the API loads a different save's sidecar.
        On.GameManager.LoadGame += (original, manager, slot, callback) =>
        {
            Guard(tracking.BeginLoad);
            original(manager, slot, callback);
        };
        ModHooks.SavegameLoadHook += slot => Guard(() => tracking.LoadGame(slot));
        // NewGameHook fires twice and menu loads can bypass BeforeSceneLoadHook.
        // StartNewGame is the one boundary after save-space allocation and before gameplay.
        On.GameManager.StartNewGame += (original, manager, permaDeath, bossRush) =>
        {
            Guard(() => tracking.StartNewGame(CurrentSlot(manager)));
            original(manager, permaDeath, bossRush);
        };
        On.GameManager.ReturnToMainMenu += (original, manager, saveMode, callback) =>
        {
            Guard(tracking.LeaveGame);
            return original(manager, saveMode, callback);
        };
        On.HeroController.Die += Die;
        ModHooks.HeroUpdateHook += Update;
        ModHooks.ApplicationQuitHook += () => Guard(tracking.LeaveGame);
    }

    private IEnumerator Die(On.HeroController.orig_Die original, HeroController hero)
    {
        Guard(() => { if (!IsDead(hero)) tracking!.Die(); });
        return original(hero);
    }

    private void Update()
    {
        if (DateTime.UtcNow < nextSample) return;
        nextSample = DateTime.UtcNow.AddMilliseconds(100);
        // Positive health only rearms the hook latch. It never creates a death event.
        object? hero = HeroInstance.GetValue(null, null);
        if (hero != null && PlayerData.instance.GetInt("health") > 0 && !IsDead(hero)) tracking!.Healthy();
        if (DateTime.UtcNow < nextFlush) return;
        nextFlush = DateTime.UtcNow.AddSeconds(2);
        Guard(() => { tracking!.Flush(); failed = false; });
        try { tracking!.ReportStatus(!failed); }
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
            try { tracking?.ReportStatus(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            LogError("Death tracking unavailable: " + exception.Message);
        }
    }

    private static int CurrentSlot(object manager)
    {
        // Reflection avoids a dependency on Unity assemblies in the one-DLL plugin build.
        return (int)typeof(GameManager).GetField("profileID")!.GetValue(manager)!;
    }

    private static bool IsDead(object hero)
    {
        object states = HeroState.GetValue(hero)!;
        return (bool)DeadState.GetValue(states)!;
    }
}
