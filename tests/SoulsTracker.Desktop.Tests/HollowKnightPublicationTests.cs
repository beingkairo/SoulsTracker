using System.IO;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop.Tests;

public sealed class HollowKnightPublicationTests
{
    [Fact]
    public void SelectionChangesInvalidateInFlightSamplesEvenWhenReturningToHollowKnight()
    {
        var state = new PersistentTrackerState(1, GameId.HollowKnight, OverlayConfiguration.Default);
        var session = new RuntimePublicationSession();
        RuntimePublicationSession.ReadTicket read = session.BeginRead(state);
        Assert.True(session.IsCurrent(read));
        session.SelectState(PersistentTrackerState.Default);
        session.SelectState(state);
        Assert.False(session.IsCurrent(read));
        bool published = false;
        session.CompleteRead(read, state, RuntimeGameReadResult.MonitoringHollowKnight(), _ => published = true, _ => published = true);
        Assert.False(published);
    }

    [Fact]
    public async Task CommittedObservedTotalUsesExistingDesktopHostedAndTextProjection()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoulsTracker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "deaths.txt");
            var state = new PersistentTrackerState(1, GameId.HollowKnight, OverlayConfiguration.Default,
                textExports: new TextExportConfiguration(output, true), hollowKnightDeaths: new HollowKnightDeaths(7, Guid.NewGuid()));
            OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, null, 1);
            Assert.Equal(TotalDeathsDisplaySource.ObservedDeaths, snapshot.TotalDeaths.Source);
            Assert.Equal(7, snapshot.TotalDeaths.Value);
            Assert.Equal(7, EffectiveDeathTotalResult.Resolve(state, null).EffectiveDisplayedTotal);
            var projection = new HostedOverlayProjection();
            Assert.Equal("7", projection.Initialize(state).Death!.Value);
            Assert.Equal("available", projection.FromAcceptedPublication(state, RuntimeGameReadResult.MonitoringHollowKnight()).Death!.Availability);
            // Offline state retains the app-owned count; it makes no lifetime claim.
            Assert.Equal("7", projection.FromAcceptedPublication(state, RuntimeGameReadResult.Unavailable(GameId.HollowKnight)).Death!.Value);
            Assert.True(await TextExportStatePublisher.WriteAsync(state));
            Assert.Equal("Total Deaths: 7", await File.ReadAllTextAsync(output));
        }
        finally { Directory.Delete(directory, true); }
    }
}
