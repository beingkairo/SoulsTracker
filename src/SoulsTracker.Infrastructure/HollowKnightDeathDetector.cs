namespace SoulsTracker.Infrastructure;

/// <summary>One validated, coherent sample; addresses identify continuity only within an attachment.</summary>
internal readonly record struct HollowKnightSample(nuint Manager, nuint Player, nuint Hero, int GameState, int Health);

/// <summary>Conservative health-edge detection. Never fabricates history across a sampling gap.</summary>
internal sealed class HollowKnightDeathDetector(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private HollowKnightSample? previous;
    private long sampledAt;
    private int healthySamples;
    internal Guid? PendingEvent { get; private set; }

    internal void ResetContinuity()
    {
        previous = null;
        healthySamples = 0;
    }

    internal void Acknowledge(Guid eventId)
    {
        if (PendingEvent == eventId) PendingEvent = null;
    }

    internal void Observe(HollowKnightSample? sample)
    {
        if (sample is not { GameState: 4, Health: >= 0 and <= 99, Manager: > 0, Player: > 0, Hero: > 0 } current)
        {
            ResetContinuity();
            return;
        }
        bool continuous = previous is { } last && last.Manager == current.Manager &&
            last.Player == current.Player && last.Hero == current.Hero &&
            clock.GetElapsedTime(sampledAt) <= TimeSpan.FromMilliseconds(500);
        if (!continuous) healthySamples = 0;
        if (continuous && healthySamples >= 2 && current.Health == 0 && PendingEvent is null)
            PendingEvent = Guid.NewGuid();
        healthySamples = current.Health > 0 ? Math.Min(2, healthySamples + 1) : 0;
        previous = current;
        sampledAt = clock.GetTimestamp();
    }
}
