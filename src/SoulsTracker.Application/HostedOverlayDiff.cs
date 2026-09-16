namespace SoulsTracker.Application;

/// <summary>Pure channel comparison. Does not allocate revisions, queue, send, or order updates.</summary>
public static class HostedOverlayDiff
{
    public static HostedOverlayEnvelope? Between(HostedOverlayEnvelope previous, HostedOverlayEnvelope candidate)
    {
        previous = HostedOverlayJson.Parse(HostedOverlayJson.Serialize(previous));
        candidate = HostedOverlayJson.Parse(HostedOverlayJson.Serialize(candidate));
        bool deathChanged = candidate.HasDeath && candidate.Death is not null &&
            (previous.Death is null || (previous.Death with { Revision = "0" }) != (candidate.Death with { Revision = "0" }));
        bool appearanceChanged = candidate.Appearance is not null &&
            (previous.Appearance is null || (previous.Appearance with { Revision = "0" }) != (candidate.Appearance with { Revision = "0" }));
        return deathChanged || appearanceChanged
            ? new("update", deathChanged, deathChanged ? candidate.Death : null, appearanceChanged ? candidate.Appearance : null)
            : null;
    }
}
