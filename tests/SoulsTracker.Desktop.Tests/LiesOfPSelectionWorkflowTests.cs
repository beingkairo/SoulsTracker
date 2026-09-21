using System.IO;
using System.Text;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class LiesOfPSelectionWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.LiesSelection.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BrowseDiscoversAndPersistsConfigurationThroughWorkflow()
    {
        string path = CreateSave(1);
        var repository = new MemoryRepository();
        var publisher = new RecordingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        var empty = new SaveDirectoryWorkflowTests.EmptyDiscovery();
        var viewModel = new DesktopTrackerViewModel(coordinator, blackMythWukongSaveDiscovery: empty, eldenRingSaveDiscovery: empty, liesOfPSaveDiscovery: empty);
        await viewModel.InitializeAsync();
        await viewModel.SetLiesOfPSaveDirectoryAsync(Path.GetDirectoryName(path)!);

        Assert.Equal(path, Assert.Single(repository.Saves).LiesOfPSave.LocalPath);
        Assert.Equal(TrackerCommandType.UpdateLiesOfPSaveConfiguration, Assert.Single(publisher.Notifications).CommandType);
        Assert.Equal(path, Assert.Single(viewModel.LiesOfPSaveChoices).LocalPath);
        Assert.Equal(path, viewModel.SelectedLiesOfPSaveChoice!.LocalPath);
        Assert.Equal(LocalSaveSourceState.CustomSelection, viewModel.LiesOfPSaveSourceState);
        Assert.Equal("Character 1", viewModel.LiesOfPSaveDiscoveryStatus);
        Assert.Equal(Path.GetDirectoryName(path), viewModel.LiesOfPDirectoryPath);
        Assert.False(viewModel.IsLiesOfPChangeMode);
        Assert.Null(viewModel.ErrorMessage);
    }

    private string CreateSave(int character)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, $"SaveData-{character}_Character_1.sav");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("GVAS"u8);
        writer.Write(new byte[60]);
        foreach (string name in new[] { "TotalReceiveDamage", "YouDieCount" })
        {
            writer.Write(name.Length + 1);
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(12);
            writer.Write("IntProperty\0"u8);
            writer.Write(sizeof(int));
            writer.Write(0);
            writer.Write((byte)0);
            writer.Write(0);
        }
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class RecordingPublisher : ITrackerStateChangePublisher
    {
        public List<TrackerStateChanged> Notifications { get; } = [];
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryRepository : ITrackerStateRepository
    {
        public List<PersistentTrackerState> Saves { get; } = [];
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TrackerStateLoadResult.Loaded(new PersistentTrackerState(1, GameId.LiesOfP, OverlayConfiguration.Default)));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            Saves.Add(state);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
