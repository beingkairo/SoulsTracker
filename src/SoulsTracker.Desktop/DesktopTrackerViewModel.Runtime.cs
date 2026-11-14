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
    private RuntimeGameObservation? runtimeObservation;
    private PersistentPluginObservation? pluginObservation;
    private string? pluginStatus;
    private RuntimeGameReaderStatus runtimeReaderStatus;
    private bool runtimeReaderHasNoRecordedDeaths;
    private bool runtimeReaderIsCurrentSaveCache;
    private GameId? runtimeReaderGameId;
    private bool isInitialDeathReadPending;
    private string totalDeathsText = "Load tracker state before using controls.";

    public string? RuntimeReaderStatusText
    {
        get
        {
            GameId? selectedGameId = state?.SelectedGameId;
            if (selectedGameId is null)
            {
                return null;
            }

            if (isInitialDeathReadPending) return "Loading.";
            if (selectedGameId == GameId.HollowKnight) return HollowKnightReaderStatusText;
            if (selectedGameId == GameId.DemonsSouls) return "Manual";

            if (runtimeReaderStatus == RuntimeGameReaderStatus.PendingLowerValue)
            {
                return IsProcessReaderGame(selectedGameId) ? GameSyncedMessage : "Confirming lower death count";
            }

            if (runtimeReaderStatus == RuntimeGameReaderStatus.SelectedSaveUnreadable)
            {
                return SelectedSaveUnreadableMessage;
            }

            if ((selectedGameId == GameId.EldenRing || selectedGameId == GameId.BlackMythWukong || selectedGameId == GameId.LiesOfP)
                && SaveReaderStatus(selectedGameId) is { } saveStatus
                && (runtimeReaderStatus is not (RuntimeGameReaderStatus.Synced or RuntimeGameReaderStatus.Cached)
                    || RequiresDirectoryAttention(saveStatus))) return saveStatus;

            if (selectedGameId == GameId.BlackMythWukong && runtimeReaderStatus is not (RuntimeGameReaderStatus.Synced or RuntimeGameReaderStatus.Cached))
            {
                return state?.BlackMythWukongSave.LocalPath is null ? WaitingForSaveFileMessage(selectedGameId) : GameUnavailableMessage;
            }
            if (selectedGameId == GameId.LiesOfP && runtimeReaderStatus is not (RuntimeGameReaderStatus.Synced or RuntimeGameReaderStatus.Cached))
            {
                return state?.LiesOfPSave.LocalPath is null ? WaitingForSaveFileMessage(selectedGameId) : GameUnavailableMessage;
            }
            if (selectedGameId == GameId.EldenRing && runtimeReaderStatus is not (RuntimeGameReaderStatus.Synced or RuntimeGameReaderStatus.Cached))
            {
                if (state?.EldenRingSave.LocalPath is null) return GameWaitingForSaveFileMessage;
                if (state.EldenRingSave.SlotIndex == EldenRingSaveConfiguration.NoSlotIndex) return EldenRingChooseCharacterMessage;
            }

            return runtimeReaderStatus switch
            {
                // The pointer readers can be operational without an active character.
                // Bloodborne uses this internal status for an unreadable/invalid value.
                RuntimeGameReaderStatus.WaitingForActiveCharacter => IsProcessReaderGame(selectedGameId)
                    ? selectedGameId == GameId.Bloodborne ? GameUnavailableMessage : GameSyncedMessage
                    : ChooseCharacterMessage,
                RuntimeGameReaderStatus.WaitingForSaveFile => WaitingForSaveFileMessage(selectedGameId),
                RuntimeGameReaderStatus.Synced => GameSyncedMessage,
                RuntimeGameReaderStatus.Cached => runtimeReaderIsCurrentSaveCache ? GameSyncedMessage : "Using last confirmed save data",
                _ => GameUnavailableMessage,
            };
        }
    }

    public long ManualDeaths => state?.SelectedGameId is GameId selectedGame && IsManualGame(selectedGame)
        ? state.GetManualDeathCounter(selectedGame).Value
        : 0;
    public string EldenRingSavedDeathsText => runtimeObservation?.GameId == GameId.EldenRing
        ? runtimeObservation.TotalDeaths.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "Unavailable";

    internal PersistentTrackerState? CurrentState => state;

    internal void ApplyRuntimeReaderResult(RuntimeGameReadResult? result)
    {
        if (IsSaveReaderSourcePending) return;
        if (state is not null && result is not null && (result.GameId != state.SelectedGameId ||
            (result.PluginObservation is { } plugin && EffectiveDeathTotalResult.Resolve(state, null, plugin).Status == EffectiveDeathTotalStatus.SourceMismatch) ||
            (result.Observation is { } observed && EffectiveDeathTotalResult.Resolve(state, observed).Status == EffectiveDeathTotalStatus.SourceMismatch))) return;
        if (result is
            {
                GameId: var resultGameId,
                BlackMythWukongSavePath: { } resultPath,
            } &&
            resultGameId == GameId.BlackMythWukong &&
            state?.SelectedGameId == GameId.BlackMythWukong &&
            !PathsEqual(resultPath, state.BlackMythWukongSave.LocalPath))
        {
            return;
        }

        bool hollowKnightUninstalled = IsHollowKnightTrackingVerifiedUninstalled;
        isInitialDeathReadPending = false;
        if (hollowKnightUninstalled && result?.GameId == GameId.HollowKnight)
            CaptureHollowKnightHistoricalTotal(result.PluginObservation);
        pluginObservation = hollowKnightUninstalled ? null : result?.GameId == state?.SelectedGameId ? result?.PluginObservation : null;
        pluginStatus = hollowKnightUninstalled ? null : result?.GameId == state?.SelectedGameId ? result?.PluginStatus : null;
        InvalidateWukongMetadataOperations();
        if (state is null)
        {
            runtimeReaderGameId = null;
            runtimeReaderStatus = RuntimeGameReaderStatus.Unavailable;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            pluginObservation = null;
            SetBlackMythWukongSaveMetadata(null);
            UpdateTotalDeathsText();
            OnPropertyChanged(nameof(RuntimeReaderStatusText));
            return;
        }

        bool blackMythWukongSaveIsUnconfigured = state.SelectedGameId == GameId.BlackMythWukong && state.BlackMythWukongSave.LocalPath is null;
        bool liesOfPSaveIsUnconfigured = state.SelectedGameId == GameId.LiesOfP && state.LiesOfPSave.LocalPath is null;
        runtimeReaderGameId = result?.GameId;
        runtimeReaderIsCurrentSaveCache = result?.IsCurrentSaveCache == true && result.GameId == state.SelectedGameId;
        if (result is not null && result.GameId == state.SelectedGameId && !blackMythWukongSaveIsUnconfigured && !liesOfPSaveIsUnconfigured)
        {
            runtimeReaderStatus = result.Status;
            runtimeReaderHasNoRecordedDeaths = result.HasNoRecordedDeaths && !IsProcessReaderGame(state.SelectedGameId);
            if (result.Status is not RuntimeGameReaderStatus.Cached)
            {
                runtimeObservation = result.Observation;
            }
            if (result.Status is not RuntimeGameReaderStatus.Cached)
            {
                SetBlackMythWukongSaveMetadata(
                    result.GameId == GameId.BlackMythWukong &&
                    result.Status is RuntimeGameReaderStatus.Synced or RuntimeGameReaderStatus.PendingLowerValue &&
                    result.BlackMythWukongSaveMetadata is not null &&
                    result.BlackMythWukongSavePath is { } metadataPath &&
                    PathsEqual(metadataPath, state.BlackMythWukongSave.LocalPath)
                        ? result.BlackMythWukongSaveMetadata
                        : null);
            }
        }
        else if (blackMythWukongSaveIsUnconfigured)
        {
            runtimeReaderGameId = GameId.BlackMythWukong;
            runtimeReaderStatus = RuntimeGameReaderStatus.WaitingForSaveFile;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            pluginObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }
        else if (liesOfPSaveIsUnconfigured)
        {
            runtimeReaderGameId = GameId.LiesOfP;
            runtimeReaderStatus = RuntimeGameReaderStatus.WaitingForSaveFile;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            pluginObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }
        else
        {
            runtimeReaderStatus = RuntimeGameReaderStatus.Unavailable;
            runtimeReaderHasNoRecordedDeaths = false;
            runtimeObservation = null;
            pluginObservation = null;
            SetBlackMythWukongSaveMetadata(null);
        }

        UpdateTotalDeathsText();
        OnPropertyChanged(nameof(EldenRingSavedDeathsText));
        NotifyHollowKnight();
        OnPropertyChanged(nameof(RuntimeReaderStatusText));
    }

    internal void ApplyRuntimeObservation(RuntimeGameObservation? observation) =>
        ApplyRuntimeReaderResult(observation is null ? null : RuntimeGameReadResult.Synced(observation));

    private void UpdateTotalDeathsText()
    {
        if (state is null)
        {
            return;
        }

        GameId selectedId = state.SelectedGameId;
        if (isInitialDeathReadPending) { TotalDeathsText = "Loading."; return; }
        if (selectedId == GameId.HollowKnight)
        {
            if (IsHollowKnightTrackingVerifiedUninstalled)
            {
                CaptureHollowKnightHistoricalTotal(pluginObservation);
                TotalDeathsText = hollowKnightHistoricalTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? GameTotalDeathsUnavailableMessage;
                return;
            }
            if (!IsHollowKnightInstalled)
            {
                TotalDeathsText = GameTotalDeathsUnavailableMessage;
                return;
            }
        }
        if (runtimeReaderHasNoRecordedDeaths)
        {
            TotalDeathsText = NoDeathsRecordedMessage;
            return;
        }

        if (runtimeReaderStatus == RuntimeGameReaderStatus.SelectedSaveUnreadable)
        {
            TotalDeathsText = GameTotalDeathsUnavailableMessage;
            return;
        }

        long? combined = EffectiveDeathTotalResult.Resolve(state, runtimeObservation?.GameId == selectedId ? runtimeObservation : null, pluginObservation).EffectiveDisplayedTotal;
        TotalDeathsText = combined.HasValue
            ? combined.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : IsManualGame(selectedId)
                ? ManualDeaths.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : GameTotalDeathsUnavailableMessage;
    }

    private static string WaitingForSaveFileMessage(GameId gameId) => gameId == GameId.BlackMythWukong
        ? BlackMythWukongWaitingForSaveFileMessage
        : gameId == GameId.LiesOfP
            ? LiesOfPWaitingForSaveFileMessage
            : GameWaitingForSaveFileMessage;

    private static bool IsProcessReaderGame(GameId gameId) =>
        gameId == GameId.Ds1 || gameId == GameId.Ds2 || gameId == GameId.Ds3 ||
        gameId == GameId.Sekiro || gameId == GameId.Bloodborne;
}
