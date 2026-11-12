using System.Globalization;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop;

/// <summary>
/// Projects accepted state for hosted publication. Call runtime projection only from
/// RuntimePublicationSession.publishOutputs, never from a reader completion.
/// Startup eligibility is local; hosted values are never imported.
/// </summary>
internal sealed class HostedOverlayProjection
{
    private bool mayPublishUnavailable;

    internal HostedOverlayEnvelope Initialize(PersistentTrackerState state) => Map(state, null);

    internal HostedOverlayEnvelope FromAcceptedPublication(PersistentTrackerState state, RuntimeGameReadResult? accepted)
    {
        return Map(state, accepted?.Observation, accepted is { Status: RuntimeGameReaderStatus.Synced }, accepted?.PluginObservation);
    }

    // The caller identifies an explicit committed source transition, separately
    // from process startup. Source/generation filtering stays in the session.
    internal HostedOverlayEnvelope FromExplicitSourceChange(PersistentTrackerState state)
    {
        mayPublishUnavailable = true;
        return Map(state, null);
    }

    private HostedOverlayEnvelope Map(PersistentTrackerState state, RuntimeGameObservation? observation, bool fresh = false, PersistentPluginObservation? plugin = null)
    {
        // Keep the established manual fallback, effective adjustment and local
        // unavailable presentation in their existing owner. No arithmetic here.
        OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, observation, 0, plugin);
        if (fresh && snapshot.TotalDeaths.Source is TotalDeathsDisplaySource.GameLifetimeReader or TotalDeathsDisplaySource.PersistentPlugin)
            mayPublishUnavailable = true;
        bool includeDeath = mayPublishUnavailable || state.SelectedGameId == GameId.DemonsSouls || state.SelectedGameId == GameId.HollowKnight;
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
