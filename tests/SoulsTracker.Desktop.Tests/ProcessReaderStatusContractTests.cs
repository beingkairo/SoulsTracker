using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class ProcessReaderStatusContractTests
{
    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    public async Task ActualReaderZeroTransitionsPreserveAcceptedDesktopHostedAndTextValues(string game)
    {
        GameId id = GameId.Parse(game);
        string path = Path.Combine(Path.GetTempPath(), $"souls-process-status-{Guid.NewGuid():N}.txt");
        try
        {
            var state = new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, id,
                OverlayConfiguration.Default, textExports: new TextExportConfiguration(path, true));
            await using var coordinator = new SerializedTrackerCoordinator(new Repository(state), new NullPublisher());
            var desktop = new DesktopTrackerViewModel(coordinator);
            await desktop.InitializeAsync();
            await using var hosted = new HostedDesktopPublisher(null, state);
            await using var text = new TextExportStatePublisher();
            var source = new ProcessSource(id);
            var readers = new RuntimeGameReaderCoordinator([source.CreateReader()]);
            var session = new RuntimePublicationSession();
            var written = Completion();
            int writes = 0;
            text.WriteCompleted += (_, success) => { Interlocked.Increment(ref writes); written.TrySetResult(success); };
            Assert.Null(Snapshot(hosted).Death);
            Assert.Equal("Game unavailable", desktop.RuntimeReaderStatusText);

            async Task Poll()
            {
                var ticket = session.BeginRead(state);
                RuntimeGameReadResult? result = await readers.PollAsync(id, default);
                session.CompleteRead(ticket, state, result, desktop.ApplyRuntimeReaderResult, publication =>
                {
                    hosted.PublishAccepted(state, publication);
                    text.PublishRuntimeObservation(state, publication);
                });
            }
            async Task AssertAccepted(int value)
            {
                Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), desktop.TotalDeathsText);
                Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
                Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Snapshot(hosted).Death!.Value);
                Assert.Equal("available", Snapshot(hosted).Death!.Availability);
                Assert.Equal($"Total Deaths: {value}", await File.ReadAllTextAsync(path));
            }

            // The real per-game reader supplies the zero observation, including Bloodborne's metadata.
            source.Value = 0;
            await Poll();
            await AssertAccepted(0);
            written = Completion();
            source.Value = 37;
            await Poll();
            await AssertAccepted(37);
            HostedOverlayEnvelope accepted = Snapshot(hosted);
            int acceptedWrites = Volatile.Read(ref writes);

            source.Value = 0;
            await Poll();
            Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
            Assert.Equal("37", desktop.TotalDeathsText);
            Assert.Same(accepted, Snapshot(hosted));
            Assert.Equal(acceptedWrites, Volatile.Read(ref writes));
            Assert.Equal("Total Deaths: 37", await File.ReadAllTextAsync(path));

            written = Completion();
            source.Value = 37;
            await Poll();
            await AssertAccepted(37);
            accepted = Snapshot(hosted);
            source.Value = 0;
            await Poll();
            Assert.Equal("37", desktop.TotalDeathsText);
            Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
            Assert.Same(accepted, Snapshot(hosted));
            written = Completion();
            await Poll(); // Existing serial, fresh second lower read confirms the genuine zero.
            await AssertAccepted(0);

            source.Running = false;
            await Poll();
            Assert.Equal("Game unavailable", desktop.RuntimeReaderStatusText);
            Assert.Equal(RuntimeGameReaderStatus.Unavailable, readers.CurrentStatus);
            Assert.Equal("0", desktop.TotalDeathsText); // Existing bounded last-confirmed retention.
            Assert.True(source.Attachments.All(attachment => attachment.Disposed));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    public async Task ActualReaderInternalStateAndReadFailureNeverFabricateAnObservation(string game)
    {
        GameId id = GameId.Parse(game);
        var state = new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, id, OverlayConfiguration.Default);
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(state), new NullPublisher());
        var desktop = new DesktopTrackerViewModel(coordinator);
        await desktop.InitializeAsync();
        var source = new ProcessSource(id) { PointerPresent = false, Readable = id != GameId.Bloodborne };
        IRuntimeGameDeathReader reader = source.CreateReader();
        var session = new RuntimePublicationSession();
        await using var hosted = new HostedDesktopPublisher(null, state);
        async Task Poll()
        {
            RuntimeGameReadResult? result = await reader.ReadAsync(default);
            Assert.Null(result?.Observation);
            session.CompleteRead(session.BeginRead(state), state, result, desktop.ApplyRuntimeReaderResult,
                publication => hosted.PublishAccepted(state, publication));
            Assert.Null(Snapshot(hosted).Death);
            Assert.False(desktop.IsTotalDeathsValueNumeric);
            Assert.DoesNotContain("waiting", desktop.TotalDeathsText, StringComparison.OrdinalIgnoreCase);
        }
        await Poll();
        Assert.Equal(id == GameId.Bloodborne ? "Game unavailable" : "Synced", desktop.RuntimeReaderStatusText);
        source.Readable = false;
        await Poll();
        Assert.Equal("Game unavailable", desktop.RuntimeReaderStatusText);
        source.Running = false;
        await Poll();
        Assert.Equal("Game unavailable", desktop.RuntimeReaderStatusText);
        Assert.True(source.Attachments.All(attachment => attachment.Disposed));
    }

    // All process/memory boundaries are synthetic. Production per-game readers and consumers run unchanged.
    private sealed class ProcessSource(GameId game) : IDarkSoulsRemasteredProcessEnumerator,
        IDarkSoulsIIScholarProcessEnumerator, IDarkSoulsIIIProcessEnumerator, ISekiroProcessEnumerator,
        IBloodborneProcessEnumerator, IReadOnlyProcessAttachmentFactory
    {
        private readonly ProcessModuleFileIdentity identity = game == GameId.Ds1
            ? new("DarkSoulsRemastered.exe", "1,0,0,0", "1", "A45AAA36DD2F6CC151670A639EA5547043CF38EA79FF4178B963C6ED71F98D7B")
            : new(game.Value + ".exe", "synthetic", "synthetic", "synthetic");
        public bool Running { get; set; } = true;
        public bool PointerPresent { get; set; } = true;
        public bool Readable { get; set; } = true;
        public int Value { get; set; }
        public List<Attachment> Attachments { get; } = [];
        public IRuntimeGameDeathReader CreateReader() => game.Value switch
        {
            "ds1" => new DarkSoulsRemasteredActiveCharacterDeathReader(this, this),
            "ds2" => new DarkSoulsIIScholarActiveCharacterDeathReader(this, this, new ExactDarkSoulsIIScholarIdentityValidator(identity)),
            "ds3" => new DarkSoulsIIIActiveCharacterDeathReader(this, this, new ExactDarkSoulsIIIIdentityValidator(identity)),
            "sekiro" => new SekiroActiveCharacterDeathReader(this, this, new ExactSekiroIdentityValidator(identity)),
            "bloodborne" => new BloodborneActiveCharacterDeathReader(this, this),
            _ => throw new InvalidOperationException(),
        };
        ValueTask<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>> IDarkSoulsRemasteredProcessEnumerator.EnumerateExactCandidatesAsync(CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<IDarkSoulsRemasteredProcessCandidate>>(Running ? [new Candidate()] : []);
        ValueTask<IReadOnlyList<IDarkSoulsIIScholarProcessCandidate>> IDarkSoulsIIScholarProcessEnumerator.EnumerateExactCandidatesAsync(CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<IDarkSoulsIIScholarProcessCandidate>>(Running ? [new Candidate()] : []);
        ValueTask<IReadOnlyList<IDarkSoulsIIIProcessCandidate>> IDarkSoulsIIIProcessEnumerator.EnumerateExactCandidatesAsync(CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<IDarkSoulsIIIProcessCandidate>>(Running ? [new Candidate()] : []);
        ValueTask<IReadOnlyList<ISekiroProcessCandidate>> ISekiroProcessEnumerator.EnumerateExactCandidatesAsync(CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<ISekiroProcessCandidate>>(Running ? [new Candidate()] : []);
        ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> IBloodborneProcessEnumerator.EnumerateExactCandidatesAsync(CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<IBloodborneProcessCandidate>>(Running ? [new Candidate()] : []);
        public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken token)
        {
            var attachment = new Attachment(identity, PointerPresent, Readable, Value);
            Attachments.Add(attachment);
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Attached(attachment));
        }
    }
    private sealed class Candidate : IDarkSoulsRemasteredProcessCandidate, IDarkSoulsIIScholarProcessCandidate,
        IDarkSoulsIIIProcessCandidate, ISekiroProcessCandidate, IBloodborneProcessCandidate
    {
        public int ProcessId => 42;
        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Attachment(ProcessModuleFileIdentity identity, bool pointerPresent, bool readable, int value) : IReadOnlyProcessAttachment
    {
        public bool Disposed { get; private set; }
        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken token) =>
            ValueTask.FromResult(ReadOnlyModuleIdentityResult.Available(identity));
        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken token) =>
            ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Available(0x1000));
        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(nuint address, byte[] destination, CancellationToken token)
        {
            if (!readable) return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
            if (destination.Length == sizeof(ulong)) BinaryPrimitives.WriteUInt64LittleEndian(destination, pointerPresent ? 0x2000UL : 0);
            else BinaryPrimitives.WriteInt32LittleEndian(destination, value);
            return ValueTask.FromResult(ReadOnlyMemoryReadResult.Succeeded((nuint)destination.Length));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static HostedOverlayEnvelope Snapshot(HostedDesktopPublisher publisher) =>
        (HostedOverlayEnvelope)typeof(HostedDesktopPublisher).GetField("latest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(publisher)!;
    private static TaskCompletionSource<bool> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Repository(PersistentTrackerState state) : ITrackerStateRepository
    {
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
        public Task SaveAsync(PersistentTrackerState value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
