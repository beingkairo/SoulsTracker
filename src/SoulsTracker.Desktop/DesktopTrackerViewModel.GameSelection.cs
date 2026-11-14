using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Net.Http;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{
    private bool isEldenRingNoticeVisible;
    private GameChoice? pendingEldenRingChoice;


    public ObservableCollection<GameChoice> GameChoices { get; }

    public GameChoice? SelectedGame { get; private set; }
    public string GameSelectionAutomationName => SelectedGame is null
        ? "Game selection"
        : $"Game selection: {SelectedGame.DisplayName}";


    public bool IsEldenRingSelected => state?.SelectedGameId == GameId.EldenRing;
    public bool IsEldenRingNoticeVisible { get => isEldenRingNoticeVisible; private set => SetField(ref isEldenRingNoticeVisible, value); }
    public bool IsBlackMythWukongSelected => state?.SelectedGameId == GameId.BlackMythWukong;
    public bool IsLiesOfPSelected => state?.SelectedGameId == GameId.LiesOfP;
    public bool IsManualGameSelected => state?.SelectedGameId is GameId id && IsManualGame(id);
    /// <summary>True when the shared global hotkey configuration applies to the current game view.</summary>
    public bool IsGlobalHotkeyConfigurationAvailable => IsManualGameSelected || IsEldenRingSelected;
    public string GlobalHotkeyUsageDescription => IsManualGameSelected
        ? "Use these global hotkeys to adjust the active manual death total."
        : IsEldenRingMissedDeathAdjustmentAvailable
            ? "Use these global hotkeys to add or remove missed deaths for the selected character."
            : IsEldenRingSelected
                ? "Choose a local save and character before global hotkeys can adjust missed deaths."
                : "Global hotkey adjustments are not available for this game. Select Demon's Souls or Elden Ring to configure them.";

    public async Task SelectGameAsync(GameChoice? choice, CancellationToken cancellationToken = default)
    {
        if (choice is null || !choice.IsSelectable || !ControlsEnabled)
        {
            return;
        }

        await SubmitAsync(new SelectGameCommand(choice.GameId), cancellationToken);

        if (state?.SelectedGameId == SoulsTracker.Domain.GameId.EldenRing)
        {
            await RescanEldenRingSavesAsync(cancellationToken);
        }
        else if (state?.SelectedGameId == SoulsTracker.Domain.GameId.BlackMythWukong)
        {
            await RescanBlackMythWukongSavesAsync(cancellationToken);
        }
        else if (state?.SelectedGameId == SoulsTracker.Domain.GameId.LiesOfP)
        {
            await RescanLiesOfPSavesAsync(cancellationToken);
        }
    }

    /// <summary>Starts a game selection, showing the local Elden Ring notice when needed.</summary>
    public bool RequestGameSelection(GameChoice? choice)
    {
        if (choice is null || !choice.IsSelectable || !ControlsEnabled)
        {
            return false;
        }

        if (choice.GameId == GameId.EldenRing && state?.EldenRingNoticeAcknowledged != true)
        {
            pendingEldenRingChoice = choice;
            IsEldenRingNoticeVisible = true;
            return false;
        }

        return true;
    }

    public async Task ConfirmEldenRingNoticeAsync(CancellationToken cancellationToken = default)
    {
        GameChoice? choice = pendingEldenRingChoice;
        if (choice is null || !IsEldenRingNoticeVisible)
        {
            return;
        }

        await SubmitAsync(new AcknowledgeEldenRingNoticeCommand(), cancellationToken);
        if (state?.EldenRingNoticeAcknowledged == true)
        {
            await SelectGameAsync(choice, cancellationToken);
        }

        pendingEldenRingChoice = null;
        IsEldenRingNoticeVisible = false;
    }

    public void CancelEldenRingNotice()
    {
        pendingEldenRingChoice = null;
        IsEldenRingNoticeVisible = false;
    }

    private static bool IsManualGame(GameId gameId) => gameId == GameId.DemonsSouls;


}


public sealed class GameChoice
{
    private readonly GameDefinition? definition;

    public GameChoice(GameDefinition definition) => this.definition = definition ?? throw new ArgumentNullException(nameof(definition));

    public GameId GameId => definition!.Id;
    public string DisplayName => GameId == SoulsTracker.Domain.GameId.DemonsSouls
            ? "Demon Souls [Manual]"
            : definition!.DisplayName;
    public bool IsSelectable => definition!.IsSelectable;
    public string AvailabilityLabel => IsSelectable ? string.Empty : "SOON";
}
