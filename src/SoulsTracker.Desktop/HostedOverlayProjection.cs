using System.Globalization;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop;

/// <summary>
/// Uncomposed projection seam. Call runtime projection only from
/// RuntimePublicationSession.publishOutputs, never from a reader completion.
/// Startup eligibility is local; hosted values are never imported.
/// </summary>
internal sealed class HostedOverlayProjection
{
    private bool mayPublishUnavailable;

    internal HostedOverlayEnvelope Initialize(PersistentTrackerState state) => Map(state, null);

    internal HostedOverlayEnvelope FromAcceptedPublication(PersistentTrackerState state, RuntimeGameReadResult? accepted)
    {
        return Map(state, accepted?.Observation, accepted is { Status: RuntimeGameReaderStatus.Synced, Observation: not null });
    }

    // The caller identifies an explicit committed source transition, separately
    // from process startup. Source/generation filtering stays in the session.
    internal HostedOverlayEnvelope FromExplicitSourceChange(PersistentTrackerState state)
    {
        mayPublishUnavailable = true;
        return Map(state, null);
    }

    private HostedOverlayEnvelope Map(PersistentTrackerState state, RuntimeGameObservation? observation, bool fresh = false)
    {
        // Keep the established manual fallback, effective adjustment and local
        // unavailable presentation in their existing owner. No arithmetic here.
        OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, observation, 0);
        if (fresh && snapshot.TotalDeaths.Source == TotalDeathsDisplaySource.GameLifetimeReader)
            mayPublishUnavailable = true;
        bool includeDeath = mayPublishUnavailable || state.SelectedGameId == GameId.DemonsSouls;
        HostedDeath? death = includeDeath ? new()
        {
            Revision = "0",
            Value = snapshot.TotalDeaths.Value?.ToString(CultureInfo.InvariantCulture),
            Availability = snapshot.TotalDeaths.Source == TotalDeathsDisplaySource.Unavailable ? "unavailable" : "available",
        } : null;
        // Zero revisions are fixture metadata, not allocated publication order.
        return new("update", includeDeath, death, HostedAppearance.From(snapshot.Presentation, "0"));
    }
}
