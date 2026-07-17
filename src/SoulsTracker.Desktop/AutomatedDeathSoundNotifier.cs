using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Converts valid reader observations into at-most-one local sound event.</summary>
public sealed class AutomatedDeathSoundNotifier(IDeathSoundPlayer player)
{
    private readonly IDeathSoundPlayer player = player ?? throw new ArgumentNullException(nameof(player));
    private GameId? baselineGame;
    private long baselineDeaths;
    private DateTimeOffset baselineObservedAtUtc;

    public void Observe(GameId? selectedGame, RuntimeGameReadResult? result, DeathSoundConfiguration configuration)
    {
        if (selectedGame is null || result is null || result.GameId != selectedGame || result.Status != RuntimeGameReaderStatus.Synced || result.Observation is null)
        {
            Reset();
            return;
        }
        RuntimeGameObservation observation = result.Observation;
        if (baselineGame != selectedGame)
        {
            baselineGame = selectedGame;
            baselineDeaths = observation.TotalDeaths.Value;
            baselineObservedAtUtc = observation.ObservedAtUtc;
            return;
        }
        if (observation.ObservedAtUtc <= baselineObservedAtUtc) { Reset(); return; }
        if (observation.TotalDeaths.Value < baselineDeaths) { Reset(); return; }
        bool increased = observation.TotalDeaths.Value > baselineDeaths;
        baselineDeaths = observation.TotalDeaths.Value;
        baselineObservedAtUtc = observation.ObservedAtUtc;
        if (increased) player.Play(configuration);
    }

    public void Reset() { baselineGame = null; baselineDeaths = 0; baselineObservedAtUtc = default; }
}
