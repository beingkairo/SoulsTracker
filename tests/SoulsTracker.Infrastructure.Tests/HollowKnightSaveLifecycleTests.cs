using Soulstracker.DeathTracking;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HollowKnightSaveLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.HkSaveLifecycle", Guid.NewGuid().ToString("N"));
    private readonly Guid installation = Guid.NewGuid();
    private readonly List<bool> statuses = [];
    private RecordStore Store => new(Path.Combine(root, installation.ToString("D")));
    private SaveTrackingLifecycle Create() => new(Store, installation, available =>
    {
        statuses.Add(available);
        Store.WriteStatus(installation, 123, 456, available);
    });
    private RuntimeGameReadResult Read()
    {
        var reader = new HollowKnightRecordReader(root, () => true, (_, _) => true, () => DateTimeOffset.UtcNow);
        reader.Configure(new(root, installation));
        return reader.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult()!;
    }
    private DeathRecord Saved(SaveIdentity identity) => Store.Load(Guid.Parse(identity.RunId))!;
    private static SaveIdentity Copy(SaveIdentity identity) => new() { RunId = identity.RunId };

    [Fact]
    public void SecondNewGameAfterMenuCreatesZeroAndKeepsTheFirstThreeDeaths()
    {
        var tracking = Create();
        tracking.StartNewGame(1);
        SaveIdentity first = Copy(tracking.Identity);
        for (int index = 0; index < 3; index++) { tracking.Healthy(); tracking.Die(); }
        Assert.Equal(3, Read().PluginObservation!.Total);

        // ReturnToMainMenu loads its scene directly, without BeforeSceneLoadHook.
        tracking.LeaveGame();
        Assert.Null(Read().PluginObservation);
        Assert.Equal(first.RunId, tracking.Identity.RunId); // Still used by the menu's ensuing save.
        tracking.LoadLocal(new SaveIdentity());
        tracking.StartNewGame(2);
        SaveIdentity second = Copy(tracking.Identity);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(0, Read().PluginObservation!.Total);
        Assert.Equal(2, Read().PluginObservation!.Slot);
        tracking.Die(); tracking.Die();
        Assert.Equal(1, Saved(second).Total);
        Assert.Equal(3, Saved(first).Total);
        Assert.Contains(false, statuses);

        tracking.BeginLoad(); tracking.LoadLocal(first); tracking.LoadGame(1);
        Assert.Equal(3, Read().PluginObservation!.Total);
        Assert.Equal(Guid.Parse(first.RunId), Read().PluginObservation!.RunId);
    }

    [Fact]
    public void NewGameBoundaryWorksWithoutAnyLocalSettingsOrSceneCallbackBetweenGames()
    {
        var tracking = Create();
        tracking.StartNewGame(1); tracking.Die();
        SaveIdentity previous = Copy(tracking.Identity);
        tracking.StartNewGame(2);
        Assert.NotEqual(previous.RunId, tracking.Identity.RunId);
        Assert.Equal(0, Saved(tracking.Identity).Total);
        Assert.Equal(1, Saved(previous).Total);
        Assert.Equal(2, Read().PluginObservation!.Slot);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ReplacingASlotCreatesNewIdentityAndDoesNotInheritItsPreviousCount(int slot)
    {
        var tracking = Create();
        tracking.StartNewGame(slot); tracking.Die();
        SaveIdentity deleted = Copy(tracking.Identity);
        tracking.LeaveGame();
        tracking.StartNewGame(slot);
        SaveIdentity replacement = Copy(tracking.Identity);
        Assert.NotEqual(deleted.RunId, replacement.RunId);
        Assert.Equal(0, Saved(replacement).Total);
        Assert.Equal(1, Saved(deleted).Total);
        tracking.Die(); tracking.Healthy(); tracking.Die();
        tracking.LeaveGame();

        var restarted = Create();
        restarted.BeginLoad(); restarted.LoadLocal(replacement); restarted.LoadGame(slot);
        Assert.Equal(2, Read().PluginObservation!.Total);
        Assert.Equal(1, Saved(deleted).Total);
    }

    [Fact]
    public void SwitchesAndRestartsPublishOnlyTheLoadedSaveAndKeepOfflineDeaths()
    {
        var tracking = Create();
        tracking.StartNewGame(1); tracking.Die();
        SaveIdentity first = Copy(tracking.Identity);
        tracking.LeaveGame(); tracking.StartNewGame(2); tracking.Die(); tracking.Healthy(); tracking.Die();
        SaveIdentity second = Copy(tracking.Identity);
        foreach (var save in new[] { (first, 1, 1L), (second, 2, 2L), (first, 1, 1L) })
        {
            tracking.BeginLoad();
            Assert.Null(Read().PluginObservation);
            tracking.LoadLocal(Copy(save.Item1));
            Assert.Null(Read().PluginObservation);
            tracking.LoadGame(save.Item2);
            var observation = Read().PluginObservation!;
            Assert.Equal(Guid.Parse(save.Item1.RunId), observation.RunId);
            Assert.Equal(save.Item2, observation.Slot);
            Assert.Equal(save.Item3, observation.Total);
        }
        tracking.LeaveGame();
        var restarted = Create();
        restarted.BeginLoad(); restarted.LoadLocal(first); restarted.LoadGame(1);
        restarted.Die(); // No desktop reader needs to be running for this write.
        restarted.LeaveGame();
        var nextRestart = Create();
        nextRestart.BeginLoad(); nextRestart.LoadLocal(first); nextRestart.LoadGame(1);
        Assert.Equal(2, Read().PluginObservation!.Total);
        Assert.Equal(2, Saved(second).Total);
    }

    [Fact]
    public void EmptyIdentityWithNoPriorRecordStartsAtZeroWithoutRecoveringUnrecordedDeaths()
    {
        var tracking = Create();
        tracking.BeginLoad(); tracking.LoadLocal(new SaveIdentity()); tracking.LoadGame(2);
        Assert.Equal(0, Saved(tracking.Identity).Total);
        SaveIdentity identity = Copy(tracking.Identity);
        tracking.Die(); tracking.LeaveGame();
        var restarted = Create();
        restarted.BeginLoad(); restarted.LoadLocal(identity); restarted.LoadGame(2);
        Assert.Equal(1, Read().PluginObservation!.Total);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData(" ")]
    public void InvalidNonemptyIdentityFailsClosedInsteadOfCreatingZero(string invalid)
    {
        var tracking = Create();
        tracking.StartNewGame(1); tracking.Die();
        SaveIdentity first = Copy(tracking.Identity);
        tracking.BeginLoad(); tracking.LoadLocal(new SaveIdentity { RunId = invalid });
        Assert.Throws<IOException>(() => tracking.LoadGame(2));
        Assert.False(tracking.Available);
        Assert.Null(Read().PluginObservation);
        Assert.Equal(1, Saved(first).Total);
        Assert.False(Store.HasSlot(2));
    }

    [Fact]
    public void MissingLocalSettingsForKnownSlotNeverReusesOrResetsTheOldRun()
    {
        var tracking = Create();
        tracking.StartNewGame(1); tracking.Die();
        SaveIdentity previous = Copy(tracking.Identity);
        tracking.BeginLoad(); // API may omit OnLoadLocal entirely for a missing sidecar.
        Assert.Throws<IOException>(() => tracking.LoadGame(1));
        Assert.Null(Read().PluginObservation);
        Assert.Equal(1, Saved(previous).Total);
        Assert.Equal("", tracking.Identity.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownIdentityWithMissingOrCorruptRecordRemainsUnavailable(bool corrupt)
    {
        var tracking = Create();
        tracking.StartNewGame(1); tracking.Die();
        SaveIdentity previous = Copy(tracking.Identity);
        tracking.LeaveGame();
        string path = Path.Combine(root, installation.ToString("D"), previous.RunId + ".record");
        if (corrupt) File.WriteAllText(path, "corrupt fixture"); else File.Delete(path);
        var restarted = Create();
        restarted.BeginLoad(); restarted.LoadLocal(previous);
        Assert.Throws<IOException>(() => restarted.LoadGame(1));
        Assert.Null(Read().PluginObservation);
        if (corrupt) Assert.Equal("corrupt fixture", File.ReadAllText(path)); else Assert.False(File.Exists(path));
        Assert.Equal(previous.RunId, restarted.Identity.RunId);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
