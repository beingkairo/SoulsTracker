using System.IO;
using System.Security.Cryptography;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class DesktopTrackerViewModelTests
{
    [Fact]
    public void BossScopeChoicesMatchWhetherTheCatalogContainsDlc()
    {
        IReadOnlyList<BossListScopeChoice> baseOnly = BossListScopeChoice.For(GameCatalog.GetRequired(GameId.BlackMythWukong));
        IReadOnlyList<BossListScopeChoice> baseAndDlc = BossListScopeChoice.For(GameCatalog.GetRequired(GameId.EldenRing));

        BossListScopeChoice onlyBaseChoice = Assert.Single(baseOnly);
        Assert.Equal(BossListScope.AllBosses, onlyBaseChoice.Value);
        Assert.Equal("All bosses", onlyBaseChoice.Label);
        Assert.Equal(
            [BossListScope.AllBosses, BossListScope.MainGame, BossListScope.Dlc],
            baseAndDlc.Select(static choice => choice.Value));
        Assert.Equal(["All bosses", "Main game", "DLC"], baseAndDlc.Select(static choice => choice.Label));
    }

    [Fact]
    public async Task ChangingFromDlcGameToBaseOnlyGameResetsTheSelectedScopeToTheOnlyVisibleChoice()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        Assert.False(harness.ViewModel.RequestGameSelection(harness.Game(GameId.EldenRing)));
        await harness.ViewModel.ConfirmEldenRingNoticeAsync();
        await harness.ViewModel.SetBossListScopeAsync(harness.ViewModel.BossListScopes.Single(choice => choice.Value == BossListScope.Dlc));

        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.DemonsSouls));

        Assert.Equal(BossListScope.AllBosses, harness.Repository.State.BossListScope);
        Assert.Equal(BossListScope.AllBosses, harness.ViewModel.SelectedBossListScope.Value);
        Assert.Equal([BossListScope.AllBosses], harness.ViewModel.BossListScopes.Select(static choice => choice.Value));
    }

    [Fact]
    public void BackgroundToggleUsesExistingZeroOpacityOutputWithoutDiscardingTheDraftValue()
    {
        OverlayAppearanceDraft draft = new()
        {
            BackgroundColor = "#123456",
            BackgroundEnabled = true,
            BackgroundOpacity = "37"
        };

        draft.BackgroundEnabled = false;
        OverlayAppearance hidden = draft.ToDomain(OverlayTextAlignment.Left);

        Assert.Equal("#123456", hidden.BackgroundColor);
        Assert.Equal(0, hidden.BackgroundOpacity);

        draft.BackgroundEnabled = true;
        OverlayAppearance restored = draft.ToDomain(OverlayTextAlignment.Left);

        Assert.Equal(37, restored.BackgroundOpacity);
        draft.Load(hidden);
        Assert.False(draft.BackgroundEnabled);
    }

    [Fact]
    public async Task HotkeyRecordingCapturesAppliesAndCanCancelWithoutLeavingPendingChanges()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Bloodborne));
        harness.ViewModel.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ => Task.FromResult(GlobalHotkeyRegistrationResult.Registered));

        harness.ViewModel.BeginHotkeyRecording(increment: true);
        harness.ViewModel.CaptureRecordedHotkey(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control);

        Assert.True(harness.ViewModel.IsHotkeyRecording);
        Assert.Equal("Ctrl+A", harness.ViewModel.PendingIncrementHotkey);

        harness.ViewModel.CancelHotkeyRecording();

        Assert.False(harness.ViewModel.IsHotkeyRecording);
        Assert.Equal(GlobalHotkeyBinding.IncrementDefault.DisplayText, harness.ViewModel.PendingIncrementHotkey);

        harness.ViewModel.BeginHotkeyRecording(increment: false);
        harness.ViewModel.CaptureRecordedHotkey(System.Windows.Input.Key.B, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt);
        await harness.ViewModel.SaveRecordedHotkeyAsync();

        Assert.False(harness.ViewModel.IsHotkeyRecording);
        Assert.Equal("Ctrl+Alt+B", harness.ViewModel.ActiveDecrementHotkey);
        Assert.Equal("Ctrl+Alt+B", harness.ViewModel.PendingDecrementHotkey);
    }

    [Fact]
    public async Task EldenRingFirstSelectionRequiresAcknowledgementAndProceedPersistsIt()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds1));
        int savesBeforeNotice = harness.Repository.SaveCount;

        Assert.False(harness.ViewModel.RequestGameSelection(harness.Game(GameId.EldenRing)));
        Assert.True(harness.ViewModel.IsEldenRingNoticeVisible);
        Assert.Equal(GameId.Ds1, harness.ViewModel.CurrentState!.SelectedGameId);
        Assert.Equal(savesBeforeNotice, harness.Repository.SaveCount);

        await harness.ViewModel.ConfirmEldenRingNoticeAsync();

        Assert.False(harness.ViewModel.IsEldenRingNoticeVisible);
        Assert.True(harness.Repository.State.EldenRingNoticeAcknowledged);
        Assert.Equal(GameId.EldenRing, harness.Repository.State.SelectedGameId);
    }

    [Fact]
    public async Task EldenRingNoticeCancelLeavesCurrentGameAndAcknowledgementUnchanged()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds3));

        Assert.False(harness.ViewModel.RequestGameSelection(harness.Game(GameId.EldenRing)));
        harness.ViewModel.CancelEldenRingNotice();

        Assert.False(harness.ViewModel.IsEldenRingNoticeVisible);
        Assert.False(harness.Repository.State.EldenRingNoticeAcknowledged);
        Assert.Equal(GameId.Ds3, harness.Repository.State.SelectedGameId);
    }

    [Fact]
    public async Task EldenRingAcknowledgementSkipsFutureNoticeAndOtherGamesDoNotShowIt()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.RequestGameSelection(harness.Game(GameId.Sekiro)));
        Assert.False(harness.ViewModel.IsEldenRingNoticeVisible);
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Sekiro));

        Assert.False(harness.ViewModel.RequestGameSelection(harness.Game(GameId.EldenRing)));
        await harness.ViewModel.ConfirmEldenRingNoticeAsync();
        Assert.True(harness.ViewModel.RequestGameSelection(harness.Game(GameId.Ds2)));
        Assert.True(harness.ViewModel.RequestGameSelection(harness.Game(GameId.EldenRing)));
        Assert.False(harness.ViewModel.IsEldenRingNoticeVisible);
    }

    [Fact]
    public async Task EldenRingProfilePickerUsesOnlySafeReaderMetadata()
    {
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true,
            eldenRingSave: new EldenRingSaveConfiguration("C:\\local-only\\ER0000.sl2", 0));
        var profileReader = new FixedProfileReader([
            new EldenRingCharacterSlotMetadata(2, IsEmpty: false, null, 50),
            new EldenRingCharacterSlotMetadata(1, IsEmpty: true, null, null),
            new EldenRingCharacterSlotMetadata(0, IsEmpty: false, "Kairo", 125),
        ]);
        await using TestHarness harness = new(state, profileReader);

        await harness.ViewModel.InitializeAsync();

        Assert.Collection(
            harness.ViewModel.EldenRingProfileSlots,
            slot => Assert.Equal((2, "Character 3 \u00B7 Level 50"), (slot.Index, slot.Label)),
            slot => Assert.Equal((0, "Kairo \u00B7 Level 125"), (slot.Index, slot.Label)));
    }

    [Fact]
    public async Task EldenRingCharacterSelectorRequiresAnExistingSelectedSaveAndHonorsGlobalControlState()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoulsTracker.EldenRingProfileAvailability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string savePath = Path.Combine(directory, "ER0000.sl2");
        await File.WriteAllTextAsync(savePath, "local test file");
        try
        {
            PersistentTrackerState state = new(
                PersistentTrackerState.CurrentSchemaVersion,
                GameId.EldenRing,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
                BossProgress.Empty,
                OverlayConfiguration.Default,
                eldenRingNoticeAcknowledged: true,
                eldenRingSave: new EldenRingSaveConfiguration(savePath, 2));
            await using TestHarness harness = new(state, new FixedProfileReader([
                new EldenRingCharacterSlotMetadata(2, IsEmpty: false, "Kairo", 125),
            ]));

            Assert.False(harness.ViewModel.ControlsEnabled);
            Assert.False(harness.ViewModel.CanSelectEldenRingProfile);
            await harness.ViewModel.InitializeAsync();
            Assert.True(harness.ViewModel.CanSelectEldenRingProfile);

            File.Delete(savePath);
            Assert.False(harness.ViewModel.CanSelectEldenRingProfile);
            Assert.Equal(2, harness.Repository.State.EldenRingSave.SlotIndex);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EldenRingCharacterSelectorStaysUnavailableWithoutASaveSelection()
    {
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true);
        await using TestHarness harness = new(state);

        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.ControlsEnabled);
        Assert.False(harness.ViewModel.CanSelectEldenRingProfile);
        Assert.Empty(harness.ViewModel.EldenRingProfileSlots);
        Assert.Null(harness.ViewModel.SelectedEldenRingProfileSlot);
    }

    [Fact]
    public async Task EldenRingCharacterSelectorDoesNotCreateASelectionForEmptyOrStaleSlots()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoulsTracker.EldenRingEmptyProfiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string savePath = Path.Combine(directory, "ER0000.sl2");
        await File.WriteAllTextAsync(savePath, "local test file");
        try
        {
            PersistentTrackerState state = new(
                PersistentTrackerState.CurrentSchemaVersion,
                GameId.EldenRing,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
                BossProgress.Empty,
                OverlayConfiguration.Default,
                eldenRingNoticeAcknowledged: true,
                eldenRingSave: new EldenRingSaveConfiguration(savePath, 7));
            await using TestHarness harness = new(state, new FixedProfileReader([
                new EldenRingCharacterSlotMetadata(0, IsEmpty: true, null, null),
                new EldenRingCharacterSlotMetadata(1, IsEmpty: true, null, null),
            ]));

            await harness.ViewModel.InitializeAsync();

            Assert.Empty(harness.ViewModel.EldenRingProfileSlots);
            Assert.Null(harness.ViewModel.SelectedEldenRingProfileSlot);
            Assert.False(harness.ViewModel.CanSelectEldenRingProfile);
            await harness.ViewModel.SetEldenRingProfileSlotAsync(new EldenRingProfileSlotChoice(0));
            Assert.Equal(7, harness.Repository.State.EldenRingSave.SlotIndex);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EldenRingCharacterSelectorHandlesAStaleSavedSlotWithoutCreatingAPlaceholder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoulsTracker.EldenRingStaleProfile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string savePath = Path.Combine(directory, "ER0000.sl2");
        await File.WriteAllTextAsync(savePath, "local test file");
        try
        {
            PersistentTrackerState state = new(
                PersistentTrackerState.CurrentSchemaVersion,
                GameId.EldenRing,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
                BossProgress.Empty,
                OverlayConfiguration.Default,
                eldenRingNoticeAcknowledged: true,
                eldenRingSave: new EldenRingSaveConfiguration(savePath, 7));
            await using TestHarness harness = new(state, new FixedProfileReader([
                new EldenRingCharacterSlotMetadata(2, IsEmpty: false, "Kairo", 125),
            ]));

            await harness.ViewModel.InitializeAsync();

            EldenRingProfileSlotChoice existingSlot = Assert.Single(harness.ViewModel.EldenRingProfileSlots);
            Assert.Equal(2, existingSlot.Index);
            Assert.Null(harness.ViewModel.SelectedEldenRingProfileSlot);
            Assert.True(harness.ViewModel.CanSelectEldenRingProfile);
            await harness.ViewModel.SetEldenRingProfileSlotAsync(existingSlot);
            Assert.Equal(2, harness.Repository.State.EldenRingSave.SlotIndex);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TotalDeathsPresentationDistinguishesNumericCountersFromCompactStatuses()
    {
        await using TestHarness manual = new(WithSelectedGame(GameId.Bloodborne, 12));
        await manual.ViewModel.InitializeAsync();
        Assert.True(manual.ViewModel.IsTotalDeathsValueNumeric);

        await using TestHarness automatic = new(PersistentTrackerState.Default);
        await automatic.ViewModel.InitializeAsync();
        await automatic.ViewModel.SelectGameAsync(automatic.Game(GameId.Sekiro));
        Assert.False(automatic.ViewModel.IsTotalDeathsValueNumeric);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, automatic.ViewModel.TotalDeathsText);
    }

    [Fact]
    public async Task CenterAlignmentClearsAndHidesBossMarkersWithoutApply()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        var notifications = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        harness.ViewModel.BossListAppearanceDraft.Alignment = OverlayTextAlignment.Left;
        Assert.True(harness.ViewModel.AreBossMarkerControlsVisible);
        harness.ViewModel.DraftBossMarker = harness.ViewModel.BossMarkers.Single(marker => marker.Value == "Skull");
        harness.ViewModel.BossListAppearanceDraft.Alignment = OverlayTextAlignment.Center;

        Assert.False(harness.ViewModel.AreBossMarkerControlsVisible);
        Assert.False(harness.ViewModel.IsBossMarkerSelected);
        Assert.Equal("None", harness.ViewModel.DraftBossMarker.Value);
        Assert.Contains(nameof(DesktopTrackerViewModel.AreBossMarkerControlsVisible), notifications);
        harness.ViewModel.BossListAppearanceDraft.Alignment = OverlayTextAlignment.Left;
        Assert.True(harness.ViewModel.AreBossMarkerControlsVisible);
        Assert.Equal("Skull", harness.ViewModel.DraftBossMarker.Value);
        Assert.Equal(0, harness.Repository.SaveCount);
    }

    [Fact]
    public async Task ApplyingCenteredBossListNeverPersistsAMarker()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.DraftBossMarker = harness.ViewModel.BossMarkers.Single(marker => marker.Value == "Skull");
        harness.ViewModel.BossListAppearanceDraft.Alignment = OverlayTextAlignment.Center;

        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);

        BossListOverlayOptions saved = harness.Repository.State.OverlayConfiguration.BossList;
        Assert.Equal(OverlayTextAlignment.Center, saved.Appearance.Alignment);
        Assert.False(saved.ShowCheckmark);
        Assert.False(saved.ShowDefeatedSkull);
    }

    [Fact]
    public async Task AppearanceDraftsUseTheDesktopCommandPathAndPersistTypedValues()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.BossListAppearanceDraft.Title = "Stream Bosses";
        harness.ViewModel.BossListAppearanceDraft.FontFamily = "Verdana";
        harness.ViewModel.BossListAppearanceDraft.FontSize = "33";
        harness.ViewModel.BossListAppearanceDraft.TextColor = "#112233";
        harness.ViewModel.BossListAppearanceDraft.AccentColor = "#445566";
        harness.ViewModel.BossListAppearanceDraft.BackgroundColor = "#778899";
        harness.ViewModel.BossListAppearanceDraft.BackgroundEnabled = true;
        harness.ViewModel.BossListAppearanceDraft.BackgroundOpacity = "44";
        harness.ViewModel.BossListAppearanceDraft.Padding = "12";
        harness.ViewModel.BossListAppearanceDraft.CornerRadius = "5";
        harness.ViewModel.BossListAppearanceDraft.Alignment = OverlayTextAlignment.Right;
        harness.ViewModel.BossListAppearanceDraft.OutlineEnabled = true;
        harness.ViewModel.BossListAppearanceDraft.OutlineColor = "#010203";
        harness.ViewModel.BossListAppearanceDraft.OutlineWidth = "3";
        harness.ViewModel.BossListAppearanceDraft.ShadowEnabled = true;
        harness.ViewModel.BossListAppearanceDraft.ShadowColor = "#040506";
        harness.ViewModel.BossListAppearanceDraft.ShadowOffsetX = "-4";
        harness.ViewModel.BossListAppearanceDraft.ShadowOffsetY = "5";
        harness.ViewModel.BossListAppearanceDraft.ShadowBlur = "6";
        harness.ViewModel.DraftBossListMode = BossListVisibilityMode.Defeated;
        harness.ViewModel.DraftDefeatedColor = "#AABBCC";
        harness.ViewModel.DraftDefeatedTreatment = DefeatedBossTreatment.Nothing;
        harness.ViewModel.DraftBossMarker = harness.ViewModel.BossMarkers.Single(marker => marker.Value == "None");
        harness.ViewModel.DraftCheckmarkAccent = "#DDEEFF";
        harness.ViewModel.DraftMaximumVisibleCount = "7";

        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);

        BossListOverlayOptions saved = harness.Repository.State.OverlayConfiguration.BossList;
        Assert.Equal("Stream Bosses", saved.Appearance.Title);
        Assert.Equal("Verdana", saved.Appearance.FontFamily);
        Assert.Equal(33, saved.Appearance.FontSize);
        Assert.Equal("#112233", saved.Appearance.TextColor);
        Assert.Equal("#445566", saved.Appearance.AccentColor);
        Assert.Equal("#778899", saved.Appearance.BackgroundColor);
        Assert.Equal(44, saved.Appearance.BackgroundOpacity);
        Assert.Equal(12, saved.Appearance.Padding);
        Assert.Equal(5, saved.Appearance.CornerRadius);
        Assert.Equal(OverlayTextAlignment.Right, saved.Appearance.Alignment);
        Assert.True(saved.Appearance.OutlineEnabled);
        Assert.Equal("#010203", saved.Appearance.OutlineColor);
        Assert.Equal(3, saved.Appearance.OutlineWidth);
        Assert.True(saved.Appearance.ShadowEnabled);
        Assert.Equal("#040506", saved.Appearance.ShadowColor);
        Assert.Equal(-4, saved.Appearance.ShadowOffsetX);
        Assert.Equal(5, saved.Appearance.ShadowOffsetY);
        Assert.Equal(6, saved.Appearance.ShadowBlur);
        Assert.Equal(BossListVisibilityMode.Defeated, saved.VisibilityMode);
        Assert.Equal(DefeatedBossTreatment.Nothing, saved.DefeatedTreatment);
        Assert.False(saved.ShowCheckmark);
        Assert.Equal(7, saved.MaximumVisibleCount);
    }

    [Fact]
    public async Task TotalDeathsDraftUsesTheDesktopCommandPathAndPersistsTitleTreatment()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.TotalDeathsAppearanceDraft.Title = "Deaths";
        harness.ViewModel.TotalDeathsAppearanceDraft.FontFamily = "Arial";
        harness.ViewModel.TotalDeathsAppearanceDraft.FontSize = "30";
        harness.ViewModel.TotalDeathsAppearanceDraft.TextColor = "#010203";
        harness.ViewModel.TotalDeathsAppearanceDraft.AccentColor = "#040506";
        harness.ViewModel.TotalDeathsAppearanceDraft.BackgroundColor = "#070809";
        harness.ViewModel.TotalDeathsAppearanceDraft.BackgroundEnabled = true;
        harness.ViewModel.TotalDeathsAppearanceDraft.BackgroundOpacity = "20";
        harness.ViewModel.TotalDeathsAppearanceDraft.Padding = "6";
        harness.ViewModel.TotalDeathsAppearanceDraft.CornerRadius = "3";
        harness.ViewModel.DraftShowGameName = false;
        harness.ViewModel.DraftCompactTitle = true;
        harness.ViewModel.DraftTitleIconModeChoice = harness.ViewModel.TitleIconModes.Single(choice => choice.Value == OverlayTitleIconMode.PrefixSkull);

        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);

        TotalDeathsOverlayOptions saved = harness.Repository.State.OverlayConfiguration.TotalDeaths;
        Assert.Equal("Deaths", saved.Appearance.Title);
        Assert.Equal("Arial", saved.Appearance.FontFamily);
        Assert.Equal(30, saved.Appearance.FontSize);
        Assert.Equal("#010203", saved.Appearance.TextColor);
        Assert.Equal("#040506", saved.Appearance.AccentColor);
        Assert.Equal("#070809", saved.Appearance.BackgroundColor);
        Assert.Equal(20, saved.Appearance.BackgroundOpacity);
        Assert.Equal(6, saved.Appearance.Padding);
        Assert.Equal(3, saved.Appearance.CornerRadius);
        Assert.Equal(OverlayTextAlignment.Left, saved.Appearance.Alignment);
        Assert.False(saved.ShowGameName);
        Assert.True(saved.CompactTitle);
        Assert.Equal(OverlayTitleIconMode.PrefixSkull, saved.TitleIconMode);
        Assert.Equal(["Off", "Prefix skull", "Skull only"], harness.ViewModel.TitleIconModes.Select(choice => choice.Label).ToArray());
    }

    [Fact]
    public async Task BlankTitlesAreAppliedAndInvalidOutlineKeepsThePreviousAppliedStyle()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetOverlayUrls("http://127.0.0.1:54288/overlay/total_deaths?token=test", "http://127.0.0.1:54288/overlay/boss_list?token=test");

        harness.ViewModel.TotalDeathsAppearanceDraft.Title = "";
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);
        string appliedUrl = Assert.IsType<string>(harness.ViewModel.TotalDeathsSceneUrl);
        Assert.Equal("", harness.Repository.State.OverlayConfiguration.TotalDeaths.Appearance.Title);

        harness.ViewModel.BossListAppearanceDraft.Title = "";
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);
        Assert.Equal("", harness.Repository.State.OverlayConfiguration.BossList.Appearance.Title);

        harness.ViewModel.TotalDeathsAppearanceDraft.OutlineWidth = "23";
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);
        Assert.Equal(appliedUrl, harness.ViewModel.TotalDeathsSceneUrl);
        Assert.True(harness.ViewModel.TotalDeathsAppearanceDraft.IsOutlineWidthInvalid);
        Assert.Contains("between 0 and 8", harness.ViewModel.TotalDeathsAppearanceStatus);
        Assert.Null(harness.ViewModel.ErrorMessage);

        harness.ViewModel.TotalDeathsAppearanceDraft.OutlineWidth = "2";
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);
        Assert.False(harness.ViewModel.TotalDeathsAppearanceDraft.IsOutlineWidthInvalid);
        Assert.Equal("Total Deaths appearance applied.", harness.ViewModel.TotalDeathsAppearanceStatus);
    }

    [Fact]
    public async Task AppearanceDraftEditsDoNotChangeTheAppliedSceneUrlOrPersistUntilApply()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetOverlayUrls("http://127.0.0.1:54288/overlay/total_deaths?token=test", "http://127.0.0.1:54288/overlay/boss_list?token=test");

        string appliedUrl = Assert.IsType<string>(harness.ViewModel.TotalDeathsSceneUrl);
        int saveCountBeforeDraftEdit = harness.Repository.SaveCount;
        harness.ViewModel.TotalDeathsAppearanceDraft.Title = "Unsaved title";
        harness.ViewModel.TotalDeathsAppearanceDraft.TextColor = "#112233";
        harness.ViewModel.TotalDeathsAppearanceDraft.OutlineEnabled = true;
        harness.ViewModel.TotalDeathsAppearanceDraft.OutlineWidth = "2";
        harness.ViewModel.DraftCompactTitle = !harness.ViewModel.DraftCompactTitle;
        harness.ViewModel.DraftShowGameName = !harness.ViewModel.DraftShowGameName;

        Assert.Equal(appliedUrl, harness.ViewModel.TotalDeathsSceneUrl);
        Assert.Equal(saveCountBeforeDraftEdit, harness.Repository.SaveCount);

        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);

        Assert.NotEqual(appliedUrl, harness.ViewModel.TotalDeathsSceneUrl);
        Assert.Equal("Unsaved title", harness.Repository.State.OverlayConfiguration.TotalDeaths.Appearance.Title);
        Assert.Equal(saveCountBeforeDraftEdit + 1, harness.Repository.SaveCount);
    }

    [Fact]
    public async Task AppliedOverlayUrlsAreReadableAndStrictlyIndependentPerOverlay()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetOverlayUrls("http://127.0.0.1:54288/overlay/total_deaths?token=test", "http://127.0.0.1:54288/overlay/boss_list?token=test");
        string originalBossUrl = Assert.IsType<string>(harness.ViewModel.BossListSceneUrl);
        harness.ViewModel.TotalDeathsAppearanceDraft.Title = "Vertical deaths";
        harness.ViewModel.TotalDeathsAppearanceDraft.FontSize = "24";
        harness.ViewModel.TotalDeathsAppearanceDraft.TextColor = "#112233";
        harness.ViewModel.TotalDeathsAppearanceDraft.OutlineEnabled = true;
        harness.ViewModel.TotalDeathsAppearanceDraft.OutlineWidth = "2";
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);

        string totalUrl = Assert.IsType<string>(harness.ViewModel.TotalDeathsSceneUrl);
        Assert.Contains("title=Vertical%20deaths", totalUrl, StringComparison.Ordinal);
        Assert.Contains("font=", totalUrl, StringComparison.Ordinal);
        Assert.Contains("size=24", totalUrl, StringComparison.Ordinal);
        Assert.Contains("textColor=%23112233", totalUrl, StringComparison.Ordinal);
        Assert.Contains("outline=true", totalUrl, StringComparison.Ordinal);
        Assert.Contains("outlineWidth=2", totalUrl, StringComparison.Ordinal);
        Assert.Equal(originalBossUrl, harness.ViewModel.BossListSceneUrl);

        harness.ViewModel.BossListAppearanceDraft.Title = "Bosses only";
        harness.ViewModel.BossListAppearanceDraft.Padding = "4";
        harness.ViewModel.BossListAppearanceDraft.ShadowEnabled = true;
        harness.ViewModel.BossListAppearanceDraft.ShadowOffsetX = "-3";
        harness.ViewModel.BossListAppearanceDraft.ShadowOffsetY = "4";
        harness.ViewModel.BossListAppearanceDraft.ShadowBlur = "5";
        harness.ViewModel.DraftDefeatedTreatment = DefeatedBossTreatment.Dimmed;
        harness.ViewModel.DraftBossMarker = harness.ViewModel.BossMarkers.Single(marker => marker.Value == "Skull");
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);

        string bossUrl = Assert.IsType<string>(harness.ViewModel.BossListSceneUrl);
        Assert.Contains("title=Bosses%20only", bossUrl, StringComparison.Ordinal);
        Assert.Contains("treatment=Dimmed", bossUrl, StringComparison.Ordinal);
        Assert.Contains("marker=Skull", bossUrl, StringComparison.Ordinal);
        Assert.Contains("bossRowSpacing=4", bossUrl, StringComparison.Ordinal);
        Assert.Contains("shadow=true", bossUrl, StringComparison.Ordinal);
        Assert.Contains("shadowX=-3", bossUrl, StringComparison.Ordinal);
        Assert.Contains("shadowY=4", bossUrl, StringComparison.Ordinal);
        Assert.Equal(totalUrl, harness.ViewModel.TotalDeathsSceneUrl);
    }

    [Fact]
    public async Task ApplyingOutlineChangesNeverResetsTheSelectedBossMarker()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();

        harness.ViewModel.DraftBossMarker = harness.ViewModel.BossMarkers.Single(marker => marker.Value == "Skull");
        harness.ViewModel.BossListAppearanceDraft.OutlineEnabled = true;
        harness.ViewModel.BossListAppearanceDraft.OutlineWidth = "2";
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);

        harness.ViewModel.BossListAppearanceDraft.OutlineEnabled = false;
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);

        BossListOverlayOptions skull = harness.Repository.State.OverlayConfiguration.BossList;
        Assert.True(skull.ShowDefeatedSkull);
        Assert.False(skull.ShowCheckmark);
        Assert.Equal("Skull", harness.ViewModel.DraftBossMarker.Value);
        Assert.False(skull.Appearance.OutlineEnabled);

        harness.ViewModel.DraftBossMarker = harness.ViewModel.BossMarkers.Single(marker => marker.Value == "Checkmark");
        harness.ViewModel.BossListAppearanceDraft.OutlineEnabled = true;
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);
        harness.ViewModel.BossListAppearanceDraft.OutlineEnabled = false;
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);

        BossListOverlayOptions checkmark = harness.Repository.State.OverlayConfiguration.BossList;
        Assert.False(checkmark.ShowDefeatedSkull);
        Assert.True(checkmark.ShowCheckmark);
        Assert.Equal("Checkmark", harness.ViewModel.DraftBossMarker.Value);
        Assert.False(checkmark.Appearance.OutlineEnabled);
    }

    [Fact]
    public async Task UrlDisplayHidesTheAuthenticatedBaseButKeepsAppliedSettingsReadable()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetOverlayUrls("http://127.0.0.1:54288/overlay/total_deaths?token=secret", "http://127.0.0.1:54288/overlay/boss_list?token=secret");

        string full = Assert.IsType<string>(harness.ViewModel.TotalDeathsSceneUrl);
        string display = Assert.IsType<string>(harness.ViewModel.TotalDeathsSceneUrlDisplay);

        Assert.Contains("token=secret", full, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", display, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("…&styleVersion=1", display, StringComparison.Ordinal);
        Assert.Contains("font=", display, StringComparison.Ordinal);
        Assert.Contains("textColor=", display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeathSoundVolumeTextValidatesAndReportsTheSavedPercentage()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        List<string?> changedProperties = [];
        harness.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        await harness.ViewModel.SetDeathSoundVolumeTextAsync("37");
        Assert.Equal(37, harness.Repository.State.DeathSound.Volume);
        Assert.Equal("Volume changed to 37%", harness.ViewModel.DeathSoundStatus);
        Assert.True(harness.ViewModel.IsDeathSoundVolumeUpdateSuccessful);
        Assert.False(harness.ViewModel.IsDeathSoundVolumeValidationError);

        await harness.ViewModel.SetDeathSoundVolumeTextAsync("101");
        Assert.Equal(37, harness.Repository.State.DeathSound.Volume);
        Assert.Equal(DesktopTrackerViewModel.DeathSoundVolumeValidationMessage, harness.ViewModel.DeathSoundStatus);
        Assert.False(harness.ViewModel.IsDeathSoundVolumeUpdateSuccessful);
        Assert.True(harness.ViewModel.IsDeathSoundVolumeValidationError);
        Assert.Contains(nameof(DesktopTrackerViewModel.IsDeathSoundVolumeUpdateSuccessful), changedProperties);
        Assert.Contains(nameof(DesktopTrackerViewModel.IsDeathSoundVolumeValidationError), changedProperties);
    }

    [Fact]
    public async Task ApplyAppearanceReportsSuccessWithoutPersistingTheOtherOverlay()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.BossListAppearanceDraft.Title = "Applied Bosses";
        await harness.ViewModel.ApplyOverlayAppearanceAsync(totalDeaths: false);
        Assert.Equal("Boss List appearance applied.", harness.ViewModel.BossListAppearanceStatus);
        Assert.Null(harness.ViewModel.TotalDeathsAppearanceStatus);
        Assert.Equal("Total Deaths", harness.Repository.State.OverlayConfiguration.TotalDeaths.Appearance.Title);
    }

    [Fact]
    public async Task InitializeLoadsStateBeforeControlsAndProjectsSelectedBloodborne()
    {
        PersistentTrackerState state = WithSelectedGame(GameId.Bloodborne, 4);
        await using TestHarness harness = new(state);

        Assert.True(harness.ViewModel.IsLoading);
        Assert.False(harness.ViewModel.ControlsEnabled);
        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.ControlsEnabled);
        Assert.Equal(GameId.Bloodborne, harness.ViewModel.SelectedGame!.GameId);
        Assert.Equal(4, harness.ViewModel.ManualDeaths);
        Assert.Equal("4", harness.ViewModel.TotalDeathsText);
        Assert.NotEmpty(harness.ViewModel.Bosses);
        Assert.Equal(DesktopTrackerViewModel.LocalTrackerStateReadyMessage, harness.ViewModel.LocalTrackerStateStatus);
    }

    [Fact]
    public async Task BloodborneIncrementDecrementAndZeroFloorUseCoordinatorCommands()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Bloodborne));

        Assert.False(harness.ViewModel.CanDecrementManualDeaths);
        await harness.ViewModel.DecrementManualDeathsAsync();
        await harness.ViewModel.IncrementManualDeathsAsync();
        await harness.ViewModel.DecrementManualDeathsAsync();
        await harness.ViewModel.DecrementManualDeathsAsync();

        Assert.Equal(0, harness.ViewModel.ManualDeaths);
        Assert.False(harness.ViewModel.CanDecrementManualDeaths);
        Assert.Equal(3, harness.Repository.SaveCount); // select + increment + decrement; zero decrement is a no-op.
    }

    [Fact]
    public async Task ManualGameSelectionsKeepIndependentDeathTotalsAndUseManualLabels()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        GameChoice bloodborne = harness.Game(GameId.Bloodborne);
        GameChoice demonsSouls = harness.Game(GameId.DemonsSouls);

        Assert.Equal("Bloodborne [Manual]", bloodborne.DisplayName);
        Assert.Equal("Demon Souls [Manual]", demonsSouls.DisplayName);
        await harness.ViewModel.SelectGameAsync(bloodborne);
        await harness.ViewModel.IncrementManualDeathsAsync();
        await harness.ViewModel.SelectGameAsync(demonsSouls);
        await harness.ViewModel.IncrementManualDeathsAsync();
        await harness.ViewModel.IncrementManualDeathsAsync();

        Assert.Equal(2, harness.ViewModel.ManualDeaths);
        Assert.Equal("2", harness.ViewModel.TotalDeathsText);
        await harness.ViewModel.SelectGameAsync(bloodborne);
        Assert.Equal(1, harness.ViewModel.ManualDeaths);
        Assert.Equal("1", harness.ViewModel.TotalDeathsText);
    }

    [Fact]
    public async Task BossToggleUsesSelectedGameAndUpdatesProjection()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds1));
        BossChoice boss = harness.ViewModel.Bosses[0];

        await harness.ViewModel.SetBossDefeatedAsync(boss, true);

        Assert.True(harness.Repository.State.BossProgress.IsDefeated(GameId.Ds1, boss.BossId));
        Assert.True(harness.ViewModel.Bosses[0].IsDefeated);
    }

    [Fact]
    public async Task BossSearchFiltersTheDesktopProjectionWithoutPersistingOrChangingOverlayOutput()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        Assert.False(harness.ViewModel.RequestGameSelection(harness.Game(GameId.EldenRing)));
        await harness.ViewModel.ConfirmEldenRingNoticeAsync();
        int savesBeforeSearch = harness.Repository.SaveCount;
        string? overlayBeforeSearch = harness.ViewModel.BossListSceneUrl;
        EldenRingSaveConfiguration savedFilter = harness.Repository.State.EldenRingSave;
        BossProgress progressBeforeSearch = harness.Repository.State.BossProgress;

        Assert.Equal(harness.ViewModel.Bosses.Select(boss => boss.BossId), harness.ViewModel.FilteredBosses.Select(boss => boss.BossId));

        harness.ViewModel.BossSearchQuery = "gOdRiCk";
        BossChoice godrick = Assert.Single(harness.ViewModel.FilteredBosses, boss => boss.DisplayName == "Godrick");
        Assert.Equal("Godrick", godrick.DisplayName);
        Assert.False(harness.ViewModel.IsBossSearchNoResults);

        harness.ViewModel.BossSearchQuery = "sHaDoW oF tHe ErDtReE";
        Assert.NotEmpty(harness.ViewModel.FilteredBosses);
        Assert.All(harness.ViewModel.FilteredBosses, boss => Assert.Equal("Shadow of the Erdtree", boss.DlcLabel));

        harness.ViewModel.BossSearchQuery = "does not exist";
        Assert.Empty(harness.ViewModel.FilteredBosses);
        Assert.True(harness.ViewModel.IsBossSearchNoResults);

        harness.ViewModel.BossSearchQuery = string.Empty;
        Assert.Equal(harness.ViewModel.Bosses.Select(boss => boss.BossId), harness.ViewModel.FilteredBosses.Select(boss => boss.BossId));
        Assert.False(harness.ViewModel.IsBossSearchNoResults);
        Assert.Equal(savesBeforeSearch, harness.Repository.SaveCount);
        Assert.Equal(overlayBeforeSearch, harness.ViewModel.BossListSceneUrl);
        Assert.Equal(savedFilter, harness.Repository.State.EldenRingSave);
        Assert.Same(progressBeforeSearch, harness.Repository.State.BossProgress);

        harness.ViewModel.BossSearchQuery = "gOdRiCk";
        await harness.ViewModel.SetBossDefeatedAsync(Assert.Single(harness.ViewModel.FilteredBosses, boss => boss.DisplayName == "Godrick"), true);
        Assert.True(Assert.Single(harness.ViewModel.FilteredBosses, boss => boss.DisplayName == "Godrick").IsDefeated);
        Assert.Equal(savesBeforeSearch + 1, harness.Repository.SaveCount);
    }

    [Fact]
    public async Task BossSearchClearsOnlyAfterTheSelectedGameChanges()
    {
        PersistentTrackerState persistedState;
        await using (TestHarness harness = new(PersistentTrackerState.Default))
        {
            await harness.ViewModel.InitializeAsync();
            await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds1));
            harness.ViewModel.BossSearchQuery = "asylum";

            await harness.ViewModel.SetBossDefeatedAsync(Assert.Single(harness.ViewModel.FilteredBosses), true);
            Assert.Equal("asylum", harness.ViewModel.BossSearchQuery);

            Assert.False(harness.ViewModel.RequestGameSelection(harness.Game(GameId.EldenRing)));
            harness.ViewModel.CancelEldenRingNotice();
            Assert.Equal("asylum", harness.ViewModel.BossSearchQuery);

            await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds1));
            Assert.Equal("asylum", harness.ViewModel.BossSearchQuery);

            await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Bloodborne));
            Assert.Equal(string.Empty, harness.ViewModel.BossSearchQuery);
            Assert.Equal(harness.ViewModel.Bosses.Select(boss => boss.BossId), harness.ViewModel.FilteredBosses.Select(boss => boss.BossId));

            harness.ViewModel.BossSearchQuery = "   ";
            Assert.Equal(harness.ViewModel.Bosses.Select(boss => boss.BossId), harness.ViewModel.FilteredBosses.Select(boss => boss.BossId));
            Assert.False(harness.ViewModel.IsBossSearchNoResults);
            persistedState = harness.Repository.State;
        }

        await using TestHarness nextSession = new(persistedState);
        await nextSession.ViewModel.InitializeAsync();
        Assert.Equal(string.Empty, nextSession.ViewModel.BossSearchQuery);
    }

    [Fact]
    public async Task BlackMythWukongDiscoversAndSelectsOneSaveWithoutManualControls()
    {
        DiscoveredLocalSave save = new(@"C:\\Tracker\\ArchiveSaveFile.1.sav", "Save slot 1");
        await using TestHarness harness = new(PersistentTrackerState.Default, saveDiscovery: new FixedSaveDiscovery(save));
        await harness.ViewModel.InitializeAsync();

        GameChoice wukong = harness.Game(GameId.BlackMythWukong);
        Assert.True(wukong.IsSelectable);
        Assert.Equal("Black Myth: Wukong", wukong.DisplayName);

        await harness.ViewModel.SelectGameAsync(wukong);

        Assert.Equal(GameId.BlackMythWukong, harness.Repository.State.SelectedGameId);
        Assert.Equal("Black Myth: Wukong", harness.ViewModel.SelectedGame!.DisplayName);
        Assert.False(harness.ViewModel.IsManualGameSelected);
        Assert.Equal(save.LocalPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal(save, harness.ViewModel.SelectedBlackMythWukongSaveChoice);
        Assert.Equal("Tracking Save slot 1", harness.ViewModel.BlackMythWukongSaveDiscoveryStatus);
        Assert.Equal(WukongSaveSourceState.AutomaticallySelected, harness.ViewModel.WukongSaveSourceState);
        Assert.True(harness.ViewModel.IsWukongChangeVisible);
        Assert.False(harness.ViewModel.IsWukongBrowseVisible);
        harness.ViewModel.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.BlackMythWukong, 12, DateTimeOffset.UtcNow)));
        Assert.Equal("12", harness.ViewModel.TotalDeathsText);
        Assert.Equal(96, harness.ViewModel.Bosses.Count);
        Assert.Equal("Track boss progress for Black Myth: Wukong.", harness.ViewModel.BossDescription);
    }

    [Fact]
    public async Task BlackMythWukongRequiresAnExplicitChoiceWhenDiscoveryFindsMultipleSaves()
    {
        DiscoveredLocalSave first = new(@"C:\\Tracker\\ArchiveSaveFile.1.sav", "Save slot 1");
        DiscoveredLocalSave second = new(@"C:\\Tracker\\ArchiveSaveFile.2.sav", "Save slot 2");
        await using TestHarness harness = new(PersistentTrackerState.Default, saveDiscovery: new FixedSaveDiscovery(first, second));
        await harness.ViewModel.InitializeAsync();

        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.BlackMythWukong));

        Assert.Null(harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal(2, harness.ViewModel.BlackMythWukongSaveChoices.Count);
        Assert.Equal("Choose the save slot you’re streaming.", harness.ViewModel.BlackMythWukongSaveDiscoveryStatus);
        Assert.Equal(WukongSaveSourceState.MultipleCandidates, harness.ViewModel.WukongSaveSourceState);
        Assert.True(harness.ViewModel.IsWukongSaveSelectorVisible);
        Assert.True(harness.ViewModel.IsWukongBrowseVisible);

        await harness.ViewModel.SelectBlackMythWukongSaveChoiceAsync(second);

        Assert.Equal(second.LocalPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal(second, harness.ViewModel.SelectedBlackMythWukongSaveChoice);
        Assert.False(harness.ViewModel.IsBlackMythWukongChangeMode);
    }

    [Fact]
    public async Task BlackMythWukongRescanPreservesAnUnavailableSavedChoice()
    {
        string savedPath = @"C:\\Tracker\\ArchiveSaveFile.7.sav";
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.BlackMythWukong,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            blackMythWukongSave: new BlackMythWukongSaveConfiguration(savedPath));
        await using TestHarness harness = new(state, saveDiscovery: new FixedSaveDiscovery(new DiscoveredLocalSave(@"C:\\Tracker\\ArchiveSaveFile.1.sav", "Save slot 1")));

        await harness.ViewModel.InitializeAsync();

        Assert.Equal(savedPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal("Selected save is unavailable.", harness.ViewModel.BlackMythWukongSaveDiscoveryStatus);
        Assert.Null(harness.ViewModel.SelectedBlackMythWukongSaveChoice);
        Assert.Equal(WukongSaveSourceState.UnavailableSelection, harness.ViewModel.WukongSaveSourceState);

        harness.ViewModel.BeginBlackMythWukongChange();
        Assert.True(harness.ViewModel.IsBlackMythWukongChangeMode);
        Assert.True(harness.ViewModel.IsWukongSaveSelectorVisible);
        Assert.True(harness.ViewModel.IsWukongSaveSelectorEnabled);
        harness.ViewModel.CancelBlackMythWukongChange();
        Assert.False(harness.ViewModel.IsBlackMythWukongChangeMode);
    }

    [Fact]
    public async Task BlackMythWukongChangeModeScanDisablesSelectorAndCancellationRestoresAllVisibleState()
    {
        using var files = new TemporaryWukongSaves();
        string path = files.Add(1, 4);
        var save = new DiscoveredLocalSave(path, "Save slot 1");
        var discovery = new MutableSaveDiscovery(_ => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([save]));
        await using TestHarness harness = new(WukongState(path), saveDiscovery: discovery);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.ApplyRuntimeReaderResult(null);
        harness.ViewModel.BeginBlackMythWukongChange();
        WukongSaveSourceState priorState = harness.ViewModel.WukongSaveSourceState;
        string? priorStatus = harness.ViewModel.BlackMythWukongSaveDiscoveryStatus;
        string? priorIdentity = harness.ViewModel.WukongSaveSourceIdentity;
        string? priorReaderStatus = harness.ViewModel.RuntimeReaderStatusText;
        using var cancellation = new CancellationTokenSource();
        discovery.Handler = async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        };

        Task scan = harness.ViewModel.RescanBlackMythWukongSavesAsync(cancellation.Token);

        Assert.Equal(WukongSaveSourceState.Scanning, harness.ViewModel.WukongSaveSourceState);
        Assert.True(harness.ViewModel.IsBlackMythWukongChangeMode);
        Assert.True(harness.ViewModel.IsWukongSaveSelectorVisible);
        Assert.False(harness.ViewModel.IsWukongSaveSelectorEnabled);
        cancellation.Cancel();
        await scan;

        Assert.Equal(priorState, harness.ViewModel.WukongSaveSourceState);
        Assert.True(harness.ViewModel.IsBlackMythWukongChangeMode);
        Assert.Equal(priorStatus, harness.ViewModel.BlackMythWukongSaveDiscoveryStatus);
        Assert.Equal(priorIdentity, harness.ViewModel.WukongSaveSourceIdentity);
        Assert.Equal(priorReaderStatus, harness.ViewModel.RuntimeReaderStatusText);
        Assert.True(harness.ViewModel.IsWukongSaveSelectorEnabled);
    }

    [Fact]
    public async Task BlackMythWukongScanExceptionRestoresControlsAndPublishesRecoverableReaderStatus()
    {
        using var files = new TemporaryWukongSaves();
        string path = files.Add(1, 4);
        var save = new DiscoveredLocalSave(path, "Save slot 1");
        var discovery = new MutableSaveDiscovery(_ => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([save]));
        await using TestHarness harness = new(WukongState(path), saveDiscovery: discovery);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.BeginBlackMythWukongChange();
        var notifications = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        discovery.Handler = _ => ValueTask.FromException<IReadOnlyList<DiscoveredLocalSave>>(new IOException("fixture failure"));

        await harness.ViewModel.RescanBlackMythWukongSavesAsync();

        Assert.Equal(WukongSaveSourceState.PersistedDiscovered, harness.ViewModel.WukongSaveSourceState);
        Assert.True(harness.ViewModel.IsBlackMythWukongChangeMode);
        Assert.True(harness.ViewModel.IsWukongSaveSelectorEnabled);
        Assert.Equal("Could not search for local saves. Try Rescan or Browse…", harness.ViewModel.BlackMythWukongSaveDiscoveryStatus);
        Assert.Equal(harness.ViewModel.BlackMythWukongSaveDiscoveryStatus, harness.ViewModel.RuntimeReaderStatusText);
        Assert.Contains(nameof(DesktopTrackerViewModel.RuntimeReaderStatusText), notifications);
    }

    [Fact]
    public async Task BlackMythWukongBrowseValidationPreservesFailureAndCommitsValidCustomSourceWithNotifications()
    {
        using var files = new TemporaryWukongSaves();
        string originalPath = files.Add(1, 4);
        string invalidPath = files.AddInvalid(8);
        string customPath = files.Add(9, 9);
        var original = new DiscoveredLocalSave(originalPath, "Save slot 1");
        await using TestHarness harness = new(WukongState(originalPath), saveDiscovery: new FixedSaveDiscovery(original));
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.ApplyRuntimeReaderResult(null);
        harness.ViewModel.BeginBlackMythWukongChange();
        var notifications = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        await harness.ViewModel.SetBlackMythWukongSaveFileAsync(invalidPath);

        Assert.Equal(originalPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal("Save slot 1", harness.ViewModel.WukongSaveSourceIdentity);
        Assert.True(harness.ViewModel.IsBlackMythWukongChangeMode);
        Assert.Equal("Selected save is unavailable or unsupported.", harness.ViewModel.RuntimeReaderStatusText);
        Assert.Contains(nameof(DesktopTrackerViewModel.RuntimeReaderStatusText), notifications);

        notifications.Clear();
        harness.Repository.FailSaves = true;
        await harness.ViewModel.SetBlackMythWukongSaveFileAsync(customPath);

        Assert.Equal(originalPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal("Save slot 1", harness.ViewModel.WukongSaveSourceIdentity);
        Assert.True(harness.ViewModel.IsBlackMythWukongChangeMode);

        harness.Repository.FailSaves = false;
        await harness.ViewModel.SetBlackMythWukongSaveFileAsync(customPath);

        Assert.Equal(customPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal("Custom save: ArchiveSaveFile.9.sav", harness.ViewModel.WukongSaveSourceIdentity);
        Assert.False(harness.ViewModel.IsBlackMythWukongChangeMode);
        Assert.Equal(WukongSaveSourceState.CustomSelection, harness.ViewModel.WukongSaveSourceState);
        Assert.Contains(nameof(DesktopTrackerViewModel.WukongSaveSourceIdentity), notifications);
        Assert.Contains(nameof(DesktopTrackerViewModel.RuntimeReaderStatusText), notifications);
    }

    [Fact]
    public async Task BlackMythWukongDiscoveredReplacementUpdatesIdentityOnlyAfterPersistenceSucceeds()
    {
        using var files = new TemporaryWukongSaves();
        string firstPath = files.Add(1, 1);
        string secondPath = files.Add(2, 2);
        var first = new DiscoveredLocalSave(firstPath, "Save slot 1");
        var second = new DiscoveredLocalSave(secondPath, "Save slot 2");
        await using TestHarness harness = new(WukongState(firstPath), saveDiscovery: new FixedSaveDiscovery(first, second));
        await harness.ViewModel.InitializeAsync();
        var reconciliationNotifications = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, args) => reconciliationNotifications.Add(args.PropertyName);
        await harness.ViewModel.RescanBlackMythWukongSavesAsync();
        Assert.Contains(nameof(DesktopTrackerViewModel.WukongSaveSourceIdentity), reconciliationNotifications);
        reconciliationNotifications.Clear();
        harness.ViewModel.ApplyImportedCommittedState(WukongState(firstPath));
        Assert.Contains(nameof(DesktopTrackerViewModel.WukongSaveSourceIdentity), reconciliationNotifications);
        harness.ViewModel.BeginBlackMythWukongChange();
        harness.Repository.FailSaves = true;

        await harness.ViewModel.SelectBlackMythWukongSaveChoiceAsync(second);

        Assert.Equal(firstPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal(first, harness.ViewModel.SelectedBlackMythWukongSaveChoice);
        Assert.Equal("Save slot 1", harness.ViewModel.WukongSaveSourceIdentity);
        Assert.True(harness.ViewModel.IsBlackMythWukongChangeMode);

        harness.Repository.FailSaves = false;
        var notifications = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        await harness.ViewModel.SelectBlackMythWukongSaveChoiceAsync(second);

        Assert.Equal(secondPath, harness.Repository.State.BlackMythWukongSave.LocalPath);
        Assert.Equal(second, harness.ViewModel.SelectedBlackMythWukongSaveChoice);
        Assert.Equal("Save slot 2", harness.ViewModel.WukongSaveSourceIdentity);
        Assert.False(harness.ViewModel.IsBlackMythWukongChangeMode);
        Assert.Contains(nameof(DesktopTrackerViewModel.WukongSaveSourceIdentity), notifications);
        Assert.Contains(nameof(DesktopTrackerViewModel.RuntimeReaderStatusText), notifications);
    }

    [Fact]
    public async Task EldenRingIsSelectableOnlyThroughItsAcknowledgementGate()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        GameChoice eldenRing = harness.Game(GameId.EldenRing);
        GameChoice demonsSouls = harness.Game(GameId.DemonsSouls);

        Assert.True(eldenRing.IsSelectable);
        Assert.Equal(string.Empty, eldenRing.AvailabilityLabel);
        Assert.True(demonsSouls.IsSelectable);
        Assert.Equal(string.Empty, demonsSouls.AvailabilityLabel);
        Assert.False(harness.ViewModel.RequestGameSelection(eldenRing));

        Assert.Equal(GameId.DemonsSouls, harness.ViewModel.SelectedGame!.GameId);
        Assert.Equal(0, harness.Repository.SaveCount);
        Assert.True(harness.ViewModel.IsEldenRingNoticeVisible);
    }

    [Fact]
    public async Task AutomaticGameShowsNeutralUnavailableTotalWithoutManualCounter()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Sekiro));

        Assert.False(harness.ViewModel.IsBloodborneSelected);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, harness.ViewModel.TotalDeathsText);
        Assert.DoesNotContain("pending verification", harness.ViewModel.TotalDeathsText, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.ViewModel.CanDecrementManualDeaths);
    }

    [Fact]
    public async Task LoadFailureLeavesControlsDisabledAndUsesSecretFreeActionableText()
    {
        await using TestHarness harness = new(TrackerStateLoadResult.Failed(TrackerStateLoadFailureKind.Corrupt, "token=not-for-display"));
        await harness.ViewModel.InitializeAsync();

        Assert.False(harness.ViewModel.ControlsEnabled);
        Assert.NotNull(harness.ViewModel.ErrorMessage);
        Assert.Equal(DesktopTrackerViewModel.LocalTrackerStateUnavailableMessage, harness.ViewModel.LocalTrackerStateStatus);
        Assert.DoesNotContain("token", harness.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not-for-display", harness.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverlayUrlDisplayMethodsUpdateTheirReadOnlyViewModelProperties()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);

        harness.ViewModel.SetOverlayUrls("total-display-value", "boss-display-value");

        Assert.Equal("total-display-value", harness.ViewModel.TotalDeathsOverlayUrl);
        Assert.Equal("boss-display-value", harness.ViewModel.BossListOverlayUrl);

        harness.ViewModel.SetOverlayUnavailable();

        Assert.Equal(harness.ViewModel.TotalDeathsOverlayUrl, harness.ViewModel.BossListOverlayUrl);
        Assert.Contains("unavailable", harness.ViewModel.TotalDeathsOverlayUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DesktopTrackerViewModel.LocalOverlayUnavailableMessage, harness.ViewModel.LocalOverlayStatus);
    }

    [Fact]
    public async Task OverlayReadyStatusIsSeparateFromTokenBearingOverlayUrlDisplays()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();

        harness.ViewModel.SetOverlayUrls("total-display-value", "boss-display-value");
        harness.ViewModel.SetOverlayReady();

        Assert.True(harness.ViewModel.ControlsEnabled);
        Assert.Equal(DesktopTrackerViewModel.LocalOverlayReadyMessage, harness.ViewModel.LocalOverlayStatus);
        Assert.DoesNotContain("display-value", harness.ViewModel.LocalOverlayStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverlayFailureLeavesLoadedLocalTrackerControlsAvailable()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();

        harness.ViewModel.SetOverlayUnavailable();

        Assert.True(harness.ViewModel.ControlsEnabled);
        Assert.Equal(DesktopTrackerViewModel.LocalOverlayUnavailableMessage, harness.ViewModel.LocalOverlayStatus);
    }

    [Fact]
    public async Task PresentationControlsDispatchOneCommandPerDeliberateChangeAndRetainGameNameWhileTotalDeathsIsDisabled()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.PresentationControlsEnabled);
        Assert.True(harness.ViewModel.CanConfigureTotalDeathsGameName);
        await harness.ViewModel.SetShowTotalDeathsGameNameAsync(true);
        await harness.ViewModel.SetTotalDeathsOverlayEnabledAsync(false);

        Assert.Equal(1, harness.Repository.SaveCount);
        Assert.False(harness.ViewModel.IsTotalDeathsOverlayEnabled);
        Assert.False(harness.ViewModel.ShowTotalDeathsGameName);
        Assert.False(harness.ViewModel.CanConfigureTotalDeathsGameName);
        Assert.False(harness.Repository.State.OverlayConfiguration.TotalDeaths.ShowGameName);

        await harness.ViewModel.SetTotalDeathsOverlayEnabledAsync(true);

        Assert.Equal(2, harness.Repository.SaveCount);
        Assert.True(harness.ViewModel.CanConfigureTotalDeathsGameName);
        Assert.False(harness.ViewModel.ShowTotalDeathsGameName);
        Assert.False(harness.Repository.State.OverlayConfiguration.TotalDeaths.ShowGameName);
    }

    [Fact]
    public async Task PresentationProjectionAndUnavailableControlsDoNotSubmitCommands()
    {
        OverlayConfiguration configuration = new(
            OverlayConfiguration.CurrentSchemaVersion,
            OverlayEndpointConfiguration.Unassigned,
            new TotalDeathsOverlayOptions(isEnabled: false, showGameName: true),
            new BossListOverlayOptions(isEnabled: false, BossListVisibilityMode.Remaining));
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            selectedGameId: null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            configuration);
        await using TestHarness harness = new(state);
        await harness.ViewModel.InitializeAsync();

        Assert.False(harness.ViewModel.IsTotalDeathsOverlayEnabled);
        Assert.False(harness.ViewModel.ShowTotalDeathsGameName);
        Assert.False(harness.ViewModel.IsBossListOverlayEnabled);
        Assert.Equal(BossListVisibilityMode.Remaining, harness.ViewModel.BossListVisibilityMode);
        await harness.ViewModel.SetTotalDeathsOverlayEnabledAsync(false);
        await harness.ViewModel.SetShowTotalDeathsGameNameAsync(false);
        await harness.ViewModel.SetBossListOverlayEnabledAsync(false);
        await harness.ViewModel.SetBossListVisibilityModeAsync(BossListVisibilityMode.Remaining);

        Assert.Equal(0, harness.Repository.SaveCount);
        Assert.Same(state, harness.Repository.State);
    }

    [Fact]
    public async Task PresentationSaveFailureRetainsCommittedProjectionAndSafeError()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        harness.Repository.FailSaves = true;

        await harness.ViewModel.SetBossListVisibilityModeAsync(BossListVisibilityMode.Defeated);

        Assert.Equal(BossListVisibilityMode.All, harness.ViewModel.BossListVisibilityMode);
        Assert.Equal(BossListVisibilityMode.All, harness.Repository.State.OverlayConfiguration.BossList.VisibilityMode);
        Assert.NotNull(harness.ViewModel.ErrorMessage);
        Assert.DoesNotContain("token", harness.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqliteStateSurvivesCoordinatorCloseAndReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), $"SoulsTracker.Desktop.Tests.{Guid.NewGuid():N}");
        try
        {
            await using (var firstCoordinator = new SerializedTrackerCoordinator(
                new SqliteTrackerStateRepository(root, "state.db"),
                new NullPublisher()))
            {
                var firstViewModel = new DesktopTrackerViewModel(firstCoordinator);
                await firstViewModel.InitializeAsync();
                await firstViewModel.SelectGameAsync(firstViewModel.GameChoices.Single(choice => choice.GameId == GameId.Bloodborne));
                await firstViewModel.IncrementManualDeathsAsync();
                await firstViewModel.SetBossDefeatedAsync(firstViewModel.Bosses[0], true);
            }

            await using var secondCoordinator = new SerializedTrackerCoordinator(
                new SqliteTrackerStateRepository(root, "state.db"),
                new NullPublisher());
            var secondViewModel = new DesktopTrackerViewModel(secondCoordinator);
            await secondViewModel.InitializeAsync();

            Assert.Equal(GameId.Bloodborne, secondViewModel.SelectedGame!.GameId);
            Assert.Equal(1, secondViewModel.ManualDeaths);
            Assert.True(secondViewModel.Bosses[0].IsDefeated);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeReaderObservationProjectsOnlyForSelectedGameAndIsNeverPersisted()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds1));
        int savesBeforeObservation = harness.Repository.SaveCount;

        harness.ViewModel.ApplyRuntimeObservation(new RuntimeGameObservation(GameId.Ds1, 12, DateTimeOffset.UtcNow));
        Assert.Equal("12", harness.ViewModel.TotalDeathsText);
        Assert.Equal(savesBeforeObservation, harness.Repository.SaveCount);
        Assert.DoesNotContain("12", harness.Repository.State.ToString(), StringComparison.Ordinal);

        harness.ViewModel.ApplyRuntimeObservation(new RuntimeGameObservation(GameId.Ds2, 99, DateTimeOffset.UtcNow));
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, harness.ViewModel.TotalDeathsText);
    }

    [Fact]
    public async Task RuntimeReaderStatusUsesSafeTransitionsAndKeepsZeroSyncedWithoutPersistence()
    {
        await using TestHarness harness = new(PersistentTrackerState.Default);
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.SelectGameAsync(harness.Game(GameId.Ds2));
        int savesBeforeRuntimeStatus = harness.Repository.SaveCount;

        Assert.Equal(DesktopTrackerViewModel.GameUnavailableMessage, harness.ViewModel.RuntimeReaderStatusText);

        harness.ViewModel.ApplyRuntimeReaderResult(RuntimeGameReadResult.WaitingForActiveCharacter(GameId.Ds2));
        Assert.Equal(DesktopTrackerViewModel.GameWaitingForActiveCharacterMessage, harness.ViewModel.RuntimeReaderStatusText);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsWaitingForActiveCharacterMessage, harness.ViewModel.TotalDeathsText);
        Assert.DoesNotContain("pending verification", harness.ViewModel.TotalDeathsText, StringComparison.OrdinalIgnoreCase);

        harness.ViewModel.ApplyRuntimeReaderResult(
            RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds2, 0, DateTimeOffset.UtcNow)));
        Assert.Equal(DesktopTrackerViewModel.GameSyncedMessage, harness.ViewModel.RuntimeReaderStatusText);
        Assert.Equal("0", harness.ViewModel.TotalDeathsText);

        harness.ViewModel.ApplyRuntimeReaderResult(null);
        Assert.Equal(DesktopTrackerViewModel.GameUnavailableMessage, harness.ViewModel.RuntimeReaderStatusText);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, harness.ViewModel.TotalDeathsText);
        Assert.Equal(savesBeforeRuntimeStatus, harness.Repository.SaveCount);
        Assert.DoesNotContain("process", harness.ViewModel.RuntimeReaderStatusText!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", harness.ViewModel.RuntimeReaderStatusText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EldenRingSyncedZeroUsesTheNumericDisplayWithoutMaskingUnavailableReaderStates()
    {
        PersistentTrackerState state = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.EldenRing,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true);
        await using TestHarness harness = new(state);
        await harness.ViewModel.InitializeAsync();

        harness.ViewModel.ApplyRuntimeReaderResult(
            RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.EldenRing, 0, DateTimeOffset.UtcNow)));
        Assert.Equal("0", harness.ViewModel.TotalDeathsText);
        Assert.True(harness.ViewModel.IsTotalDeathsValueNumeric);

        harness.ViewModel.ApplyRuntimeReaderResult(RuntimeGameReadResult.WaitingForSaveFile(GameId.EldenRing));
        Assert.Equal("Choose an Elden Ring save file to begin tracking.", harness.ViewModel.TotalDeathsText);

        harness.ViewModel.ApplyRuntimeReaderResult(null);
        Assert.Equal(DesktopTrackerViewModel.GameTotalDeathsUnavailableMessage, harness.ViewModel.TotalDeathsText);
    }

    private static PersistentTrackerState WithSelectedGame(GameId gameId, long manualDeaths) => new(
        PersistentTrackerState.CurrentSchemaVersion,
        gameId,
        ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, manualDeaths),
        BossProgress.Empty,
        OverlayConfiguration.Default);

    private static PersistentTrackerState WukongState(string localPath) => new(
        PersistentTrackerState.CurrentSchemaVersion,
        GameId.BlackMythWukong,
        ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
        BossProgress.Empty,
        OverlayConfiguration.Default,
        blackMythWukongSave: new BlackMythWukongSaveConfiguration(localPath));

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly SerializedTrackerCoordinator coordinator;

        public TestHarness(PersistentTrackerState state, IEldenRingSaveProfileReader? profileReader = null, ILocalSaveDiscovery? saveDiscovery = null)
            : this(TrackerStateLoadResult.Loaded(state), profileReader, saveDiscovery) { }

        public TestHarness(TrackerStateLoadResult loadResult, IEldenRingSaveProfileReader? profileReader = null, ILocalSaveDiscovery? saveDiscovery = null)
        {
            Repository = new FakeRepository(loadResult);
            coordinator = new SerializedTrackerCoordinator(Repository, new NullPublisher());
            ViewModel = new DesktopTrackerViewModel(coordinator, profileReader, saveDiscovery);
        }

        public FakeRepository Repository { get; }
        public DesktopTrackerViewModel ViewModel { get; }
        public GameChoice Game(GameId id) => ViewModel.GameChoices.Single(choice => choice.GameId == id);
        public ValueTask DisposeAsync() => coordinator.DisposeAsync();
    }

    private sealed class FakeRepository(TrackerStateLoadResult loadResult) : ITrackerStateRepository
    {
        private TrackerStateLoadResult loadResult = loadResult;
        public PersistentTrackerState State { get; private set; } = PersistentTrackerState.Default;
        public int SaveCount { get; private set; }
        public bool FailSaves { get; set; }
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (loadResult.IsSuccess) State = loadResult.State!;
            return Task.FromResult(loadResult);
        }
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            if (FailSaves) throw new IOException("token=not-for-display");
            State = state;
            SaveCount++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedProfileReader(IReadOnlyList<EldenRingCharacterSlotMetadata> slots) : IEldenRingSaveProfileReader
    {
        public ValueTask<IReadOnlyList<EldenRingCharacterSlotMetadata>> ReadAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) => ValueTask.FromResult(slots);
    }

    private sealed class FixedSaveDiscovery(params DiscoveredLocalSave[] saves) : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>(saves);
    }

    private sealed class MutableSaveDiscovery(Func<CancellationToken, ValueTask<IReadOnlyList<DiscoveredLocalSave>>> handler) : ILocalSaveDiscovery
    {
        public Func<CancellationToken, ValueTask<IReadOnlyList<DiscoveredLocalSave>>> Handler { get; set; } = handler;
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) => Handler(cancellationToken);
    }

    private sealed class TemporaryWukongSaves : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.Desktop.Wukong", Guid.NewGuid().ToString("N"));

        public string Add(int slot, int deaths)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, $"ArchiveSaveFile.{slot}.sav");
            File.WriteAllBytes(path, CreateArchive(deaths));
            return path;
        }

        public string AddInvalid(int slot)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, $"ArchiveSaveFile.{slot}.sav");
            File.WriteAllBytes(path, [1]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static byte[] CreateArchive(int deaths)
        {
            byte[] death = FieldVarint(1, (ulong)deaths);
            byte[] decoded = FieldBytes(6, FieldBytes(1, FieldBytes(5, death)));
            byte[] encrypted = decoded.ToArray();
            byte[] key = [0x7B, 0x5C, 0xDA, 0x91, 0x3E, 0xFC, 0xDA, 0x37];
            for (int index = 0; index < encrypted.Length; index++) encrypted[index] ^= key[index % key.Length];
#pragma warning disable CA5351
            byte[] checksum = Convert.ToHexStringLower(MD5.HashData(encrypted.Concat("lhx2tkh6lj1wj8jmrgs3k1xb2brusehx"u8.ToArray()).ToArray())).Select(static value => (byte)value).ToArray();
#pragma warning restore CA5351
            byte[] metadata = Concat(FieldBytes(1, checksum), FieldVarint(7, 14), FieldVarint(8, 1), FieldVarint(10, 23831), FieldVarint(11, 23831));
            return Concat(FieldBytes(1, metadata), FieldBytes(2, encrypted));
        }

        private static byte[] FieldBytes(ulong field, byte[] value) { var result = new List<byte>(); Write(result, field << 3 | 2); Write(result, (ulong)value.Length); result.AddRange(value); return result.ToArray(); }
        private static byte[] FieldVarint(ulong field, ulong value) { var result = new List<byte>(); Write(result, field << 3); Write(result, value); return result.ToArray(); }
        private static byte[] Concat(params byte[][] values) => values.SelectMany(static value => value).ToArray();
        private static void Write(List<byte> target, ulong value) { do { byte next = (byte)(value & 0x7F); value >>= 7; target.Add(value == 0 ? next : (byte)(next | 0x80)); } while (value != 0); }
    }
}
