using System.Runtime.ExceptionServices;
using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Media;
using SoulsTracker.Application;
using SoulsTracker.Desktop;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop.Tests;

public sealed class MainWindowBindingTests
{
    [Fact]
    public void WorkspaceTabsSeparateOperationalAndOverlayConfigurationSurfaces()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                TabControl tabs = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs"));
                Assert.Equal(["Main", "Overlay", "Settings"], tabs.Items.OfType<TabItem>().Select(static item => item.Header?.ToString() ?? string.Empty).ToArray());
                Assert.IsType<TabItem>(window.FindName("MainWorkspaceTab"));
                Assert.IsType<TabItem>(window.FindName("OverlayWorkspaceTab"));
                Assert.IsType<TabItem>(window.FindName("SettingsWorkspaceTab"));
                Assert.True(Assert.IsType<TextBox>(window.FindName("TotalDeathsOverlayUrlTextBox")).IsReadOnly);
                Assert.True(Assert.IsType<TextBox>(window.FindName("BossListOverlayUrlTextBox")).IsReadOnly);
                Hyperlink footer = Assert.IsType<Hyperlink>(window.FindName("BeingKairoAttributionHyperlink"));
                Assert.Equal("beingkairo.com", footer.Inlines.OfType<Run>().Single().Text);
            }
            finally { window?.Close(); }
        });
    }

    [Fact]
    public void EldenRingSetupUsesTheFriendlySaveGuidanceAndTwoAcknowledgementActions()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));

        Assert.Contains("Text=\"Set up Elden Ring\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Elden Ring keeps all of your characters in one ER0000.sl2 save file.", xaml, StringComparison.Ordinal);
        Assert.Contains("Choose the save file, then pick the character you want to track.", xaml, StringComparison.Ordinal);
        Assert.Contains("Death totals update after Elden Ring saves.", xaml, StringComparison.Ordinal);
        Assert.Contains("Usual location: %APPDATA%\\EldenRing\\&lt;your Steam ID&gt;\\ER0000.sl2", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot guarantee this is safe", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("risk", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normally looks for it automatically", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Got it\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Not now\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EldenRingSavePickerRetainsSetupHelpAfterTheModalIsDismissed()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));

        Assert.Contains("Choose your ER0000.sl2 save file, then choose the character you want to track. Death totals update after Elden Ring saves.", xaml, StringComparison.Ordinal);
        Assert.Contains("Can't find it? Most saves are here: %APPDATA%\\EldenRing\\&lt;your Steam ID&gt;\\ER0000.sl2", xaml, StringComparison.Ordinal);
        Assert.Contains("DataTrigger Binding=\"{Binding EldenRingSaveFileName}\" Value=\"{x:Null}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EldenRingChecklistFiltersLiveOnlyInTheBossesHeader()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));
        string gameSession = Between(xaml, "AutomationProperties.Name=\"Game session panel\"", "AutomationProperties.Name=\"Death counter panel\"");
        string bosses = Between(xaml, "x:Name=\"BossProgressPanel\"", "x:Name=\"OverlayWorkspaceTab\"");

        Assert.DoesNotContain("EldenRingBossListScopes", gameSession, StringComparison.Ordinal);
        Assert.DoesNotContain("RequiredEldenRingBossesOnlyCheckBox", gameSession, StringComparison.Ordinal);
        Assert.Contains("EldenRingBossListScopes", bosses, StringComparison.Ordinal);
        Assert.Contains("RequiredEldenRingBossesOnlyCheckBox", bosses, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsEldenRingSelected", bosses, StringComparison.Ordinal);
    }

    [Fact]
    public void MainTotalDeathsUsesLargeTypographyOnlyForNumericValuesAndCharacterSelectorUsesAvailabilityBinding()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));
        string counter = Between(xaml, "x:Name=\"TotalDeathsTextBlock\"", "AutomationProperties.Name=\"Increase manual deaths\"");

        Assert.Contains("Binding IsTotalDeathsValueNumeric", counter, StringComparison.Ordinal);
        Assert.Contains("FontSize\" Value=\"42\"", counter, StringComparison.Ordinal);
        Assert.Contains("MutedTextBrush", counter, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanSelectEldenRingProfile}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCheckboxStyleLimitsEveryCheckboxToItsVisibleContent()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                Style checkboxStyle = Assert.IsType<Style>(window.Resources[typeof(CheckBox)]);
                Assert.Contains(
                    checkboxStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == FrameworkElement.HorizontalAlignmentProperty && Equals(setter.Value, HorizontalAlignment.Left));
                Assert.Contains(
                    checkboxStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == Control.HorizontalContentAlignmentProperty && Equals(setter.Value, HorizontalAlignment.Left));

                Assert.Equal(HorizontalAlignment.Left, Assert.IsType<CheckBox>(window.FindName("TotalDeathsOverlayEnabledCheckBox")).HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Left, Assert.IsType<CheckBox>(window.FindName("BossListOverlayEnabledCheckBox")).HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Left, Assert.IsType<CheckBox>(window.FindName("DeathSoundEnabledCheckBox")).HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Left, Assert.IsType<CheckBox>(window.FindName("DeathsExportEnabledCheckBox")).HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Left, Assert.IsType<CheckBox>(window.FindName("BossExportEnabledCheckBox")).HorizontalAlignment);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void OverlayAppearanceEditorUsesScopedControlsWithoutPresetsOrTotalAlignment()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));

        Assert.DoesNotContain("Preset", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TotalDeathsAppearanceDraft.Alignment", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Total Deaths title icon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Defeated boss marker\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Boss List alignment\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"Label\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Boss List background opacity\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Total Deaths background\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Boss List background\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding TotalDeathsAppearanceDraft.BackgroundEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding BossListAppearanceDraft.BackgroundEnabled", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Boss List padding\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Boss List corner radius\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Show selected game name\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Inline\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PickColor_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("local:ColorField", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Text effects\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Maximum visible", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"SETTINGS\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Elden Ring is labeled SOON and cannot be selected.", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Outline size\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding TotalDeathsAppearanceDraft.OutlineEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding TotalDeathsAppearanceDraft.ShadowEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding BossListAppearanceDraft.OutlineEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding BossListAppearanceDraft.ShadowEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("OverlayAppearanceNumberBox", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Total Deaths outline\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Total Deaths shadow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Boss List outline\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Boss List shadow\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose an overlay type", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("border-left", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "web_overlay", "src", "overlay.css")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverlayEditorKeepsConditionalControlsAndNaturalFieldGroupsInTheRequiredOrder()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));
        string totalDeaths = Between(xaml, "<TabItem Header=\"Total Deaths\">", "<TabItem Header=\"Boss List\">");
        string bossList = Between(xaml, "<TabItem Header=\"Boss List\">", "</TabControl>");

        AssertOrder(totalDeaths, "Text=\"Title\"", "Text=\"Title icon\"", "Text=\"Skull color\"", "Text=\"Font\"", "Text=\"Text color\"", "Text=\"Text opacity\"", "Text=\"Background\"", "Text=\"Background color\"", "Text=\"Background opacity\"", "Text=\"Outline\"", "Text=\"Shadow\"");
        AssertOrder(bossList, "Text=\"Mode\"", "Text=\"Treatment\"", "Text=\"Defeated color\"", "Text=\"Alignment\"", "Text=\"Marker\"", "Text=\"Title\"", "Text=\"Text color\"", "Text=\"Text opacity\"", "Text=\"Boss row spacing\"", "Text=\"Background\"", "Text=\"Background color\"", "Text=\"Background opacity\"", "Text=\"Outline\"", "Text=\"Shadow\"");
        Assert.True(bossList.IndexOf("Text=\"Marker color\"", StringComparison.Ordinal) > bossList.IndexOf("Text=\"Marker\"", StringComparison.Ordinal));
        Assert.Contains("Visibility=\"{Binding ShowBossMarkerColor", bossList, StringComparison.Ordinal);
        Assert.DoesNotContain("Maximum visible", bossList, StringComparison.Ordinal);
    }

    [Fact]
    public void TabsAndColorFieldUseNeutralFocusAndTheNativeWindowsPalette()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "SoulsTracker.Desktop", "MainWindow.xaml"));
        string tabStyle = Between(xaml, "<Style x:Key=\"DarkTabItem\"", "</Style>");
        string colorField = File.ReadAllText(Path.Combine(root, "src", "SoulsTracker.Desktop", "ColorField.cs"));

        Assert.DoesNotContain("AccentBrush", tabStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("IsKeyboardFocused", tabStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0\"", tabStyle, StringComparison.Ordinal);
        Assert.Contains("Forms.ColorDialog", colorField, StringComparison.Ordinal);
        Assert.Contains("AllowFullOpen = true", colorField, StringComparison.Ordinal);
        Assert.Contains("FullOpen = true", colorField, StringComparison.Ordinal);
        Assert.Contains("SolidColorOnly = true", colorField, StringComparison.Ordinal);
        Assert.Contains("WindowInteropHelper(owner).Handle", colorField, StringComparison.Ordinal);
        Assert.Contains("dialog.ShowDialog(new NativeWindowOwner(ownerHandle))", colorField, StringComparison.Ordinal);
        Assert.Contains("Open native color palette", colorField, StringComparison.Ordinal);
        Assert.Contains("CreatePaletteButtonTemplate", colorField, StringComparison.Ordinal);
        Assert.DoesNotContain("AccentBrush", colorField, StringComparison.Ordinal);
        Assert.Contains("BorderThickness = new Thickness(0)", colorField, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWheel", colorField, StringComparison.Ordinal);
        Assert.DoesNotContain("Popup picker", colorField, StringComparison.Ordinal);
    }

    [Fact]
    public void OutlineAndShadowDetailsAreBoundToTheirOwnEnableToggles()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                AssertVisibilityBinding(window, "TotalDeathsOutlineDetails", "TotalDeathsAppearanceDraft.OutlineEnabled");
                AssertVisibilityBinding(window, "TotalDeathsShadowDetails", "TotalDeathsAppearanceDraft.ShadowEnabled");
                AssertVisibilityBinding(window, "BossListOutlineDetails", "BossListAppearanceDraft.OutlineEnabled");
                AssertVisibilityBinding(window, "BossListShadowDetails", "BossListAppearanceDraft.ShadowEnabled");
                AssertVisibilityBinding(window, "TotalDeathsBackgroundDetails", "TotalDeathsAppearanceDraft.BackgroundEnabled");
                AssertVisibilityBinding(window, "BossListBackgroundDetails", "BossListAppearanceDraft.BackgroundEnabled");
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void MainAndSettingsUseContainedScrollRegionsInsteadOfExpandingForContent()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = 1060, Height = 760 };
                window.Show();
                window.UpdateLayout();

                ScrollViewer mainColumn = Assert.IsType<ScrollViewer>(window.FindName("MainContentScrollViewer"));
                Border bossPanel = Assert.IsType<Border>(window.FindName("BossProgressPanel"));
                ScrollViewer bosses = Assert.IsType<ScrollViewer>(window.FindName("BossesScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, mainColumn.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Auto, bosses.VerticalScrollBarVisibility);
                Assert.True(bossPanel.ActualHeight > 0);
                Assert.True(bosses.ActualHeight > 0);
                Assert.True(Math.Abs(bossPanel.ActualHeight - mainColumn.ActualHeight) <= 1);

                Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")).SelectedIndex = 2;
                window.UpdateLayout();
                ScrollViewer settings = Assert.IsType<ScrollViewer>(window.FindName("SettingsContentScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, settings.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Disabled, settings.HorizontalScrollBarVisibility);
                Assert.Equal(0, settings.ScrollableHeight);
                StackPanel settingsContent = Assert.IsType<StackPanel>(window.FindName("SettingsContentStack"));
                Border[] settingsPanels = FindVisualDescendants<Border>(settingsContent)
                    .Where(panel => AutomationProperties.GetName(panel) is "Death sound settings" or "OBS text export settings")
                    .ToArray();
                Assert.Equal(["Death sound settings", "OBS text export settings"], settingsPanels.Select(AutomationProperties.GetName).ToArray());
                Assert.True(settingsPanels[0].TranslatePoint(new Point(0, 0), settingsContent).Y < settingsPanels[1].TranslatePoint(new Point(0, 0), settingsContent).Y);

                Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")).SelectedIndex = 1;
                window.UpdateLayout();
                ScrollViewer overlay = Assert.IsType<ScrollViewer>(window.FindName("OverlayConfigurationScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, overlay.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Disabled, overlay.HorizontalScrollBarVisibility);
                Grid overlayLayout = Assert.IsType<Grid>(window.FindName("OverlayWorkspaceLayout"));
                Assert.Equal(3d, overlayLayout.ColumnDefinitions[0].Width.Value);
                Assert.Equal(GridUnitType.Star, overlayLayout.ColumnDefinitions[0].Width.GridUnitType);
                Assert.Equal(2d, overlayLayout.ColumnDefinitions[2].Width.Value);
                Assert.Equal(GridUnitType.Star, overlayLayout.ColumnDefinitions[2].Width.GridUnitType);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void ClosedGameSelectorWheelInputIsSuppressedWithoutChangingNormalDropDownBehavior()
    {
        Assert.True(MainWindow.ShouldSuppressClosedGameSelectorWheel(isDropDownOpen: false));
        Assert.False(MainWindow.ShouldSuppressClosedGameSelectorWheel(isDropDownOpen: true));
    }

    [Fact]
    public void SettingsRemainScrollableAtTheMinimumSupportedWindowHeight()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = 560, Height = 400 };
                window.Show();
                Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")).SelectedIndex = 2;
                window.UpdateLayout();

                ScrollViewer settings = Assert.IsType<ScrollViewer>(window.FindName("SettingsContentScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, settings.VerticalScrollBarVisibility);
                Assert.True(settings.ScrollableHeight > 0);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void ManualHotkeyGuidanceAndInAppRecorderUseTheApprovedWording()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));
        Assert.Contains("Choose a field to record. Enter saves and returns to the main menu; Esc cancels.", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+Alt plus a key", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml.cs")), StringComparison.Ordinal);
        Assert.Contains("Currently recording input", xaml, StringComparison.Ordinal);
        Assert.Contains("Press the input that you want to use as a hotkey then click enter to save.", xaml, StringComparison.Ordinal);
        Assert.Contains("Press ESC to exit without saving", xaml, StringComparison.Ordinal);
        Assert.Contains("HotkeyRecordingOverlay", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayAppearanceControlsExposeReadableIconLabelsAndDistinctBossLayoutNames()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { DataContext = new { TitleIconModes = OverlayTitleIconModeChoice.All } };
                window.Show();
                Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")).SelectedIndex = 1;
                window.UpdateLayout();

                ComboBox titleIcon = Assert.Single(FindVisualDescendants<ComboBox>(window),
                    comboBox => AutomationProperties.GetName(comboBox) == "Total Deaths title icon");
                Assert.Equal(["Off", "Prefix skull", "Skull only"], titleIcon.Items.Cast<OverlayTitleIconModeChoice>().Select(choice => choice.Label).ToArray());
                Assert.Equal("Label", titleIcon.DisplayMemberPath);

                Assert.IsType<TabControl>(window.FindName("OverlayTypeTabs")).SelectedIndex = 1;
                window.UpdateLayout();
                string[] layoutNames = FindVisualDescendants<TextBox>(window)
                    .Select(AutomationProperties.GetName)
                    .Where(static name => name is not null && name.StartsWith("Boss List", StringComparison.Ordinal))
                    .ToArray()!;
                Assert.Contains("Boss List background opacity", layoutNames);
                Assert.DoesNotContain("Boss List padding", layoutNames);
                Assert.DoesNotContain("Boss List corner radius", layoutNames);
            }
            finally { window?.Close(); }
        });
    }

    [Fact]
    public void DeathSoundControlsAreSettingsOnlyAndUseAccessibleSafeBindings()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                Assert.IsType<Button>(window.FindName("BrowseDeathSoundButton"));
                Assert.IsType<Button>(window.FindName("ClearDeathSoundButton"));
                Assert.IsType<Button>(window.FindName("PlayDeathSoundButton"));
                AssertPropertyBinding(window, "BrowseDeathSoundButton", nameof(DesktopTrackerViewModel.CanBrowseDeathSound), Button.IsEnabledProperty);
                AssertPropertyBinding(window, "ClearDeathSoundButton", nameof(DesktopTrackerViewModel.CanClearDeathSound), Button.IsEnabledProperty);
                AssertPropertyBinding(window, "PlayDeathSoundButton", nameof(DesktopTrackerViewModel.CanPreviewDeathSound), Button.IsEnabledProperty);
                CheckBox enabled = Assert.IsType<CheckBox>(window.FindName("DeathSoundEnabledCheckBox"));
                Assert.Equal("Enable death sound", AutomationProperties.GetName(enabled));
                TextBox volume = Assert.IsType<TextBox>(window.FindName("DeathSoundVolumeTextBox"));
                AssertPropertyBinding(window, "DeathSoundVolumeTextBox", nameof(DesktopTrackerViewModel.CanEditDeathSoundVolume), TextBox.IsEnabledProperty);
                Assert.Equal("Death sound volume percentage", AutomationProperties.GetName(volume));
                Assert.Equal(72d, volume.Width);
                Assert.True(volume.Focusable);
                Assert.DoesNotContain("Slider", volume.Name, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("DeathSoundVolume_KeyDown", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml")), StringComparison.Ordinal);
                Assert.IsType<Button>(window.FindName("SaveDeathSoundVolumeButton"));
                AssertPropertyBinding(window, "SaveDeathSoundVolumeButton", nameof(DesktopTrackerViewModel.CanEditDeathSoundVolume), Button.IsEnabledProperty);
                Button clear = Assert.IsType<Button>(window.FindName("ClearDeathSoundButton"));
                Button save = Assert.IsType<Button>(window.FindName("SaveDeathSoundVolumeButton"));
                Assert.Equal(2, Grid.GetColumn(clear));
                Assert.Equal(0, Grid.GetRow(clear));
                Assert.Equal(2, Grid.GetColumn(save));
                Assert.Equal(1, Grid.GetRow(save));
                Assert.Equal(HorizontalAlignment.Right, clear.HorizontalAlignment);
                Assert.Equal(HorizontalAlignment.Right, save.HorizontalAlignment);
                Assert.Contains("Text=\"%\"", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml")), StringComparison.Ordinal);
                TextBlock status = Assert.IsType<TextBlock>(window.FindName("DeathSoundStatusTextBlock"));
                Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                DataTrigger successTrigger = Assert.Single(status.Style.Triggers.OfType<DataTrigger>(), trigger => Assert.IsType<Binding>(trigger.Binding).Path?.Path == nameof(DesktopTrackerViewModel.IsDeathSoundVolumeUpdateSuccessful) && string.Equals(trigger.Value?.ToString(), "True", StringComparison.OrdinalIgnoreCase));
                Binding successBinding = Assert.IsType<Binding>(successTrigger.Binding);
                Assert.Equal(nameof(DesktopTrackerViewModel.IsDeathSoundVolumeUpdateSuccessful), successBinding.Path?.Path);
                DataTrigger errorTrigger = Assert.Single(status.Style.Triggers.OfType<DataTrigger>(), trigger => Assert.IsType<Binding>(trigger.Binding).Path?.Path == nameof(DesktopTrackerViewModel.IsDeathSoundVolumeValidationError) && string.Equals(trigger.Value?.ToString(), "True", StringComparison.OrdinalIgnoreCase));
                Binding errorBinding = Assert.IsType<Binding>(errorTrigger.Binding);
                Assert.Equal(nameof(DesktopTrackerViewModel.IsDeathSoundVolumeValidationError), errorBinding.Path?.Path);
                Assert.IsType<Button>(window.FindName("CopyTotalDeathsOverlayUrlButton"));
                Assert.IsType<Button>(window.FindName("CopyBossListOverlayUrlButton"));
                TextBlock fileName = Assert.IsType<TextBlock>(window.FindName("DeathSoundFileNameTextBlock"));
                Binding fileBinding = Assert.IsType<Binding>(BindingOperations.GetBinding(fileName, TextBlock.TextProperty));
                Assert.Equal(nameof(DesktopTrackerViewModel.DeathSoundFileName), fileBinding.Path?.Path);
                Assert.DoesNotContain("Path", fileName.Name, StringComparison.OrdinalIgnoreCase);
            }
            finally { window?.Close(); }
        });
    }

    [Fact]
    public void RoutedVolumeSaveClickUpdatesTheVisibleStatusAfterAsyncPersistence()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            var repository = new PresentationRepository();
            var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            try
            {
                var viewModel = new DesktopTrackerViewModel(coordinator);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                window = new MainWindow { DataContext = viewModel };
                window.Show();
                Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")).SelectedIndex = 2;
                window.UpdateLayout();

                TextBox volume = Assert.IsType<TextBox>(window.FindName("DeathSoundVolumeTextBox"));
                Button save = Assert.IsType<Button>(window.FindName("SaveDeathSoundVolumeButton"));
                TextBlock status = Assert.IsType<TextBlock>(window.FindName("DeathSoundStatusTextBlock"));
                volume.Text = "37";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                WaitForDispatcher(() => string.Equals(status.Text, "Volume changed to 37%", StringComparison.Ordinal));

                Assert.Equal("Volume changed to 37%", status.Text);
                Assert.True(viewModel.IsDeathSoundVolumeUpdateSuccessful);
                Assert.Equal("#FF70D6A7", ((SolidColorBrush)status.Foreground).Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal("Volume changed to 37%", AutomationProperties.GetName(status));
                Assert.Equal(1, repository.SaveCount);

                volume.Text = "101";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForDispatcher(() => string.Equals(status.Text, DesktopTrackerViewModel.DeathSoundVolumeValidationMessage, StringComparison.Ordinal));

                Assert.Equal(DesktopTrackerViewModel.DeathSoundVolumeValidationMessage, status.Text);
                Assert.True(viewModel.IsDeathSoundVolumeValidationError);
                Assert.Equal("#FFFF8C8C", ((SolidColorBrush)status.Foreground).Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(DesktopTrackerViewModel.DeathSoundVolumeValidationMessage, AutomationProperties.GetName(status));
                Assert.Equal(1, repository.SaveCount);
            }
            finally
            {
                window?.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void TextExportSettingsUseExplicitEnablementControls()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                AssertOneWayTextBlockBinding(window, "DeathsExportFileNameTextBlock", nameof(DesktopTrackerViewModel.DeathsExportFileName));
                AssertOneWayTextBlockBinding(window, "BossExportFileNameTextBlock", nameof(DesktopTrackerViewModel.BossExportFileName));
                CheckBox deathsToggle = Assert.IsType<CheckBox>(window.FindName("DeathsExportEnabledCheckBox"));
                CheckBox bossToggle = Assert.IsType<CheckBox>(window.FindName("BossExportEnabledCheckBox"));
                Assert.Equal("Enable writing Deaths to txt file", AutomationProperties.GetName(deathsToggle));
                Assert.Equal("Enable writing Boss list to txt file", AutomationProperties.GetName(bossToggle));
                Assert.Equal("Enable writing Deaths to txt file", GetInlineText((TextBlock)deathsToggle.Content));
                Assert.Equal("Enable writing Boss list to txt file", GetInlineText((TextBlock)bossToggle.Content));
                Assert.Equal(FontWeights.Bold, ((Run)((TextBlock)deathsToggle.Content).Inlines.ElementAt(1)).FontWeight);
                Assert.Equal(FontWeights.Bold, ((Run)((TextBlock)bossToggle.Content).Inlines.ElementAt(1)).FontWeight);
                Assert.IsType<CheckBox>(LogicalTreeHelper.GetChildren(Assert.IsType<StackPanel>(window.FindName("DeathsExportToggleRow"))).Cast<object>().Single());
                Assert.IsType<CheckBox>(LogicalTreeHelper.GetChildren(Assert.IsType<StackPanel>(window.FindName("BossExportToggleRow"))).Cast<object>().Single());
                Assert.Null(window.FindName("TextExportStatusTextBlock"));

                AssertPropertyBinding(window, "ChooseDeathsExportButton", nameof(DesktopTrackerViewModel.CanChooseDeathsExport), Button.IsEnabledProperty);
                AssertPropertyBinding(window, "ClearDeathsExportButton", nameof(DesktopTrackerViewModel.CanClearDeathsExport), Button.IsEnabledProperty);
                AssertPropertyBinding(window, "ChooseBossExportButton", nameof(DesktopTrackerViewModel.CanChooseBossExport), Button.IsEnabledProperty);
                AssertPropertyBinding(window, "ClearBossExportButton", nameof(DesktopTrackerViewModel.CanClearBossExport), Button.IsEnabledProperty);
            }
            finally { window?.Close(); }
        });
    }

    [Fact]
    public void TextExportEnablementTogglesPersistRoutedCheckboxInteractions()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            var repository = new TextExportPersistenceRepository();
            var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            try
            {
                var viewModel = new DesktopTrackerViewModel(coordinator);
                viewModel.InitializeAsync().GetAwaiter().GetResult();

                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();

                CheckBox deathsToggle = Assert.IsType<CheckBox>(window.FindName("DeathsExportEnabledCheckBox"));
                CheckBox bossToggle = Assert.IsType<CheckBox>(window.FindName("BossExportEnabledCheckBox"));
                Button chooseDeaths = Assert.IsType<Button>(window.FindName("ChooseDeathsExportButton"));
                Button clearDeaths = Assert.IsType<Button>(window.FindName("ClearDeathsExportButton"));
                Button chooseBoss = Assert.IsType<Button>(window.FindName("ChooseBossExportButton"));
                Button clearBoss = Assert.IsType<Button>(window.FindName("ClearBossExportButton"));

                Assert.False(deathsToggle.IsChecked);
                Assert.False(bossToggle.IsChecked);
                Assert.False(chooseDeaths.IsEnabled);
                Assert.False(chooseBoss.IsEnabled);

                // Setting IsChecked raises the real routed Checked event handled by
                // MainWindow. This catches the original state/binding race instead
                // of only asserting XAML binding shape.
                deathsToggle.IsChecked = true;
                WaitForDispatcher(() => viewModel.IsDeathsExportEnabled && deathsToggle.IsChecked == true && repository.SaveCount >= 1);
                Assert.True(viewModel.CanChooseDeathsExport);
                chooseDeaths.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                Assert.True(chooseDeaths.IsEnabled);
                Assert.False(clearDeaths.IsEnabled);

                bossToggle.IsChecked = true;
                WaitForDispatcher(() => viewModel.IsBossExportEnabled && bossToggle.IsChecked == true && repository.SaveCount >= 2);
                Assert.True(viewModel.CanChooseBossExport);
                chooseBoss.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                Assert.True(chooseBoss.IsEnabled);
                Assert.False(clearBoss.IsEnabled);
                Assert.True(repository.State.TextExports.DeathsEnabled);
                Assert.True(repository.State.TextExports.BossListEnabled);

                deathsToggle.IsChecked = false;
                WaitForDispatcher(() => !viewModel.IsDeathsExportEnabled && deathsToggle.IsChecked == false && repository.SaveCount >= 3);
                Assert.False(viewModel.CanChooseDeathsExport);
                chooseDeaths.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                Assert.False(chooseDeaths.IsEnabled);

                bossToggle.IsChecked = false;
                WaitForDispatcher(() => !viewModel.IsBossExportEnabled && bossToggle.IsChecked == false && repository.SaveCount >= 4);
                Assert.False(viewModel.CanChooseBossExport);
                chooseBoss.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                Assert.False(chooseBoss.IsEnabled);

                window.Close();
                window = null;
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();

                var restartedCoordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
                try
                {
                    var restarted = new DesktopTrackerViewModel(restartedCoordinator);
                    restarted.InitializeAsync().GetAwaiter().GetResult();
                    Assert.False(restarted.IsDeathsExportEnabled);
                    Assert.False(restarted.IsBossExportEnabled);
                }
                finally { restartedCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally
            {
                window?.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void DeathSoundEnablementTogglesPersistAndControlFileActions()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            string soundPath = Path.Combine(Path.GetTempPath(), $"souls-tracker-test-{Guid.NewGuid():N}.wav");
            var repository = new TextExportPersistenceRepository();
            var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            try
            {
                var viewModel = new DesktopTrackerViewModel(coordinator);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();

                CheckBox enabled = Assert.IsType<CheckBox>(window.FindName("DeathSoundEnabledCheckBox"));
                Button browse = Assert.IsType<Button>(window.FindName("BrowseDeathSoundButton"));
                Button clear = Assert.IsType<Button>(window.FindName("ClearDeathSoundButton"));
                Button play = Assert.IsType<Button>(window.FindName("PlayDeathSoundButton"));
                TextBox volume = Assert.IsType<TextBox>(window.FindName("DeathSoundVolumeTextBox"));
                Button saveVolume = Assert.IsType<Button>(window.FindName("SaveDeathSoundVolumeButton"));

                Assert.False(enabled.IsChecked);
                Assert.False(browse.IsEnabled);
                Assert.False(clear.IsEnabled);
                Assert.False(play.IsEnabled);
                Assert.False(volume.IsEnabled);
                Assert.False(saveVolume.IsEnabled);

                enabled.IsChecked = true;
                WaitForDispatcher(() => viewModel.IsDeathSoundEnabled && enabled.IsChecked == true && repository.SaveCount >= 1);
                browse.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                clear.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                play.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                volume.GetBindingExpression(TextBox.IsEnabledProperty)?.UpdateTarget();
                saveVolume.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                Assert.True(viewModel.CanBrowseDeathSound);
                Assert.False(viewModel.CanClearDeathSound);
                Assert.True(browse.IsEnabled);
                Assert.False(clear.IsEnabled);
                Assert.False(play.IsEnabled);
                Assert.True(volume.IsEnabled);
                Assert.True(saveVolume.IsEnabled);
                Assert.True(repository.State.DeathSound.IsEnabled);

                File.WriteAllBytes(soundPath, []);
                viewModel.SetDeathSoundFileAsync(soundPath).GetAwaiter().GetResult();
                WaitForDispatcher(() => viewModel.DeathSoundFileName is not null && repository.SaveCount >= 2);
                browse.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                clear.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                play.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                Assert.True(viewModel.CanBrowseDeathSound);
                Assert.True(viewModel.CanClearDeathSound);
                Assert.True(browse.IsEnabled);
                Assert.True(clear.IsEnabled);
                Assert.True(play.IsEnabled);

                enabled.IsChecked = false;
                WaitForDispatcher(() => !viewModel.IsDeathSoundEnabled && enabled.IsChecked == false && repository.SaveCount >= 3);
                browse.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                clear.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                play.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                volume.GetBindingExpression(TextBox.IsEnabledProperty)?.UpdateTarget();
                saveVolume.GetBindingExpression(Button.IsEnabledProperty)?.UpdateTarget();
                Assert.False(viewModel.CanBrowseDeathSound);
                Assert.False(viewModel.CanClearDeathSound);
                Assert.False(browse.IsEnabled);
                Assert.False(clear.IsEnabled);
                Assert.False(play.IsEnabled);
                Assert.False(volume.IsEnabled);
                Assert.False(saveVolume.IsEnabled);
                Assert.False(repository.State.DeathSound.IsEnabled);

                window.Close();
                window = null;
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();

                var restartedCoordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
                try
                {
                    var restarted = new DesktopTrackerViewModel(restartedCoordinator);
                    restarted.InitializeAsync().GetAwaiter().GetResult();
                    Assert.False(restarted.IsDeathSoundEnabled);
                    Assert.False(restarted.CanBrowseDeathSound);
                    Assert.False(restarted.CanClearDeathSound);
                }
                finally { restartedCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally
            {
                window?.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                File.Delete(soundPath);
            }
        });
    }

    [Fact]
    public void RoutedClearActionsPreserveEnabledSoundAndTextExportTogglesAfterReload()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            PersistentTrackerState configuredState = new(
                PersistentTrackerState.CurrentSchemaVersion,
                selectedGameId: null,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
                BossProgress.Empty,
                OverlayConfiguration.Default,
                deathSound: new DeathSoundConfiguration("C:\\temp\\sound.wav", isEnabled: true, volume: 100),
                textExports: new TextExportConfiguration("C:\\temp\\deaths.txt", deathsEnabled: true, "C:\\temp\\bosses.txt", bossListEnabled: true));
            var repository = new TextExportPersistenceRepository(configuredState);
            var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            try
            {
                var viewModel = new DesktopTrackerViewModel(coordinator);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();

                Button clearSound = Assert.IsType<Button>(window.FindName("ClearDeathSoundButton"));
                Button clearDeaths = Assert.IsType<Button>(window.FindName("ClearDeathsExportButton"));
                Button clearBoss = Assert.IsType<Button>(window.FindName("ClearBossExportButton"));
                Assert.True(clearSound.IsEnabled);
                Assert.True(clearDeaths.IsEnabled);
                Assert.True(clearBoss.IsEnabled);

                clearSound.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForDispatcher(() => repository.SaveCount >= 1 && viewModel.DeathSoundFileName is null);
                clearDeaths.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForDispatcher(() => repository.SaveCount >= 2 && viewModel.DeathsExportFileName is null);
                clearBoss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForDispatcher(() => repository.SaveCount >= 3 && viewModel.BossExportFileName is null);

                Assert.True(viewModel.IsDeathSoundEnabled);
                Assert.True(viewModel.IsDeathsExportEnabled);
                Assert.True(viewModel.IsBossExportEnabled);
                Assert.True(repository.State.DeathSound.IsEnabled);
                Assert.True(repository.State.TextExports.DeathsEnabled);
                Assert.True(repository.State.TextExports.BossListEnabled);

                window.Close();
                window = null;
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();

                var restartedCoordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
                try
                {
                    var restarted = new DesktopTrackerViewModel(restartedCoordinator);
                    restarted.InitializeAsync().GetAwaiter().GetResult();
                    Assert.True(restarted.IsDeathSoundEnabled);
                    Assert.True(restarted.IsDeathsExportEnabled);
                    Assert.True(restarted.IsBossExportEnabled);
                    Assert.Null(restarted.DeathSoundFileName);
                    Assert.Null(restarted.DeathsExportFileName);
                    Assert.Null(restarted.BossExportFileName);
                }
                finally { restartedCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally
            {
                window?.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void OverlayUrlTextBindingsAreExplicitlyOneWay()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();

                AssertOneWayTextBinding(
                    window,
                    "TotalDeathsOverlayUrlTextBox",
                    nameof(DesktopTrackerViewModel.TotalDeathsSceneUrlDisplay));
                AssertOneWayTextBinding(
                    window,
                    "BossListOverlayUrlTextBox",
                    nameof(DesktopTrackerViewModel.BossListSceneUrlDisplay));
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void GlobalHotkeyStatusIsVisibleThroughTheViewModelBinding()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                TextBlock status = Assert.IsType<TextBlock>(window.FindName("GlobalHotkeyStatusTextBlock"));
                Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(status, TextBlock.TextProperty));
                Binding automationNameBinding = Assert.IsType<Binding>(BindingOperations.GetBinding(status, AutomationProperties.NameProperty));

                Assert.Equal(nameof(DesktopTrackerViewModel.GlobalHotkeyStatus), binding.Path?.Path);
                Assert.Equal(BindingMode.OneWay, binding.Mode);
                Assert.Equal(nameof(DesktopTrackerViewModel.GlobalHotkeyStatus), automationNameBinding.Path?.Path);
                Assert.Equal(BindingMode.OneWay, automationNameBinding.Mode);

                Binding visibilityBinding = Assert.IsType<Binding>(BindingOperations.GetBinding(status, UIElement.VisibilityProperty));
                Assert.Equal(nameof(DesktopTrackerViewModel.IsManualGameSelected), visibilityBinding.Path?.Path);
                Assert.Equal(BindingMode.OneWay, visibilityBinding.Mode);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void OverlayConnectionContainsOnlyFunctionalEnableAndUrlControls()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();

                Assert.Null(window.FindName("LocalTrackerStateStatusTextBlock"));
                Assert.Null(window.FindName("LocalOverlayStatusTextBlock"));
                Assert.IsType<CheckBox>(window.FindName("TotalDeathsOverlayEnabledCheckBox"));
                Assert.IsType<TextBox>(window.FindName("TotalDeathsOverlayUrlTextBox"));
                Assert.IsType<CheckBox>(window.FindName("BossListOverlayEnabledCheckBox"));
                Assert.IsType<TextBox>(window.FindName("BossListOverlayUrlTextBox"));
                Assert.Contains("Add these links to your streaming software as Browser Sources to show the overlays.", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml")), StringComparison.Ordinal);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void RuntimeReaderStatusIsVisibleThroughAnExplicitOneWayBinding()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                AssertOneWayStatusBinding(
                    window,
                    "RuntimeReaderStatusTextBlock",
                    nameof(DesktopTrackerViewModel.RuntimeReaderStatusText),
                    "Game reader status");
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void OverlayPresentationControlsUseOneWayProjectionAndStableAccessibleNames()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                AssertOneWayCheckBoxBinding(
                    window,
                    "TotalDeathsOverlayEnabledCheckBox",
                    nameof(DesktopTrackerViewModel.IsTotalDeathsOverlayEnabled),
                    nameof(DesktopTrackerViewModel.PresentationControlsEnabled),
                    "Show Total Deaths overlay");
                AssertOneWayCheckBoxBinding(
                    window,
                    "BossListOverlayEnabledCheckBox",
                    nameof(DesktopTrackerViewModel.IsBossListOverlayEnabled),
                    nameof(DesktopTrackerViewModel.PresentationControlsEnabled),
                    "Enable Boss List overlay");

                ComboBox modeSelector = Assert.IsType<ComboBox>(window.FindName("BossListVisibilityModeSelector"));
                Binding selectedBinding = Assert.IsType<Binding>(BindingOperations.GetBinding(modeSelector, ComboBox.SelectedItemProperty));
                Assert.Equal(nameof(DesktopTrackerViewModel.DraftBossListMode), selectedBinding.Path?.Path);
                Assert.Equal("Boss List mode", AutomationProperties.GetName(modeSelector));
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void ProgrammaticPresentationProjectionDoesNotResubmitCheckedOrSelectionChangedCommands()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            var repository = new PresentationRepository();
            var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
            try
            {
                var viewModel = new DesktopTrackerViewModel(coordinator);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();

                Assert.Equal(0, repository.SaveCount);
                viewModel.SetTotalDeathsOverlayEnabledAsync(false).GetAwaiter().GetResult();
                window.UpdateLayout();
                Assert.Equal(1, repository.SaveCount);

                viewModel.SetBossListVisibilityModeAsync(BossListVisibilityMode.Remaining).GetAwaiter().GetResult();
                window.UpdateLayout();
                Assert.Equal(2, repository.SaveCount);
            }
            finally
            {
                window?.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void BossCheckboxBindingIsExplicitlyOneWayAfterSelectedDs1TemplateMaterializes()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            var coordinator = new SerializedTrackerCoordinator(
                new SelectedDs1Repository(),
                new NullPublisher());
            try
            {
                var viewModel = new DesktopTrackerViewModel(coordinator);
                viewModel.InitializeAsync().GetAwaiter().GetResult();

                Assert.Equal(GameId.Ds1, viewModel.SelectedGame?.GameId);
                Assert.NotEmpty(viewModel.Bosses);

                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();

                List<CheckBox> bossCheckBoxes = FindVisualDescendants<CheckBox>(window)
                    .Where(checkBox => checkBox.DataContext is BossChoice)
                    .ToList();
                Assert.NotEmpty(bossCheckBoxes);
                CheckBox bossCheckBox = bossCheckBoxes[0];
                bossCheckBox.ApplyTemplate();
                window.UpdateLayout();

                Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(bossCheckBox, CheckBox.IsCheckedProperty));

                Assert.Equal(nameof(BossChoice.IsDefeated), binding.Path?.Path);
                Assert.Equal(BindingMode.OneWay, binding.Mode);
            }
            finally
            {
                window?.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void StreamingUtilityLayoutPreservesAccessibleControlsAndUsesTheConfiguredApplicationIdentity()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();

                Assert.NotNull(window.Icon);
                Image icon = Assert.IsType<Image>(window.FindName("SoulsTrackerSkullIcon"));
                Assert.Equal("SoulsTracker skull application icon", AutomationProperties.GetName(icon));
                Assert.Equal("Game selection", AutomationProperties.GetName(Assert.IsType<ComboBox>(window.FindName("GameSelector"))));
                Assert.Null(window.FindName("GameAvailabilityHintTextBlock"));
                Assert.Equal("Current total deaths", AutomationProperties.GetName(Assert.IsType<TextBlock>(window.FindName("TotalDeathsTextBlock"))));
                Assert.Equal("Death Tracker and Boss List", Assert.IsType<TextBlock>(window.FindName("HeaderSubtitleTextBlock")).Text);

                ScrollViewer bosses = Assert.IsType<ScrollViewer>(window.FindName("BossesScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, bosses.VerticalScrollBarVisibility);
                Assert.Equal("Boss checklist", AutomationProperties.GetName(bosses));

                ScrollViewer workspace = Assert.IsType<ScrollViewer>(window.FindName("MainContentScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, workspace.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Disabled, workspace.HorizontalScrollBarVisibility);

                Hyperlink attribution = Assert.IsType<Hyperlink>(window.FindName("BeingKairoAttributionHyperlink"));
                Assert.Equal(new Uri("https://www.beingkairo.com/"), attribution.NavigateUri);
                Assert.Equal("Visit beingkairo.com (opens in default browser)", AutomationProperties.GetName(attribution));

                TextBox totalUrl = Assert.IsType<TextBox>(window.FindName("TotalDeathsOverlayUrlTextBox"));
                TextBox bossUrl = Assert.IsType<TextBox>(window.FindName("BossListOverlayUrlTextBox"));
                Assert.True(totalUrl.IsReadOnly);
                Assert.True(bossUrl.IsReadOnly);

                ComboBox bossMode = Assert.IsType<ComboBox>(window.FindName("BossListVisibilityModeSelector"));
                Assert.NotNull(Assert.IsType<Style>(window.Resources[typeof(ScrollBar)]).Setters.OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty).Value);
                AssertExplicitDarkComboBoxTemplate(Assert.IsType<ComboBox>(window.FindName("GameSelector")));
                AssertExplicitDarkComboBoxTemplate(bossMode);
                AssertNoPersistentFocusVisual(Assert.IsType<TextBox>(window.FindName("TotalDeathsOverlayUrlTextBox")));
                AssertNoPersistentFocusVisual(bossMode);
                AssertComboBoxAccentIsLimitedToTheOpenDropDown(bossMode);

                Border legacyPanel = Assert.IsType<Border>(window.FindName("LegacyImportPanel"));
                Binding legacyVisibility = Assert.IsType<Binding>(BindingOperations.GetBinding(legacyPanel, UIElement.VisibilityProperty));
                Assert.Equal("DataContext.HasActiveLegacyImport", legacyVisibility.Path?.Path);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void DesktopAndInstallerUseTheCompactSkullIconResource()
    {
        string root = FindRepositoryRoot();
        string desktopProject = File.ReadAllText(Path.Combine(root, "src", "SoulsTracker.Desktop", "SoulsTracker.Desktop.csproj"));
        string installer = File.ReadAllText(Path.Combine(root, "installer", "SoulsTracker.iss"));

        Assert.Contains("<ApplicationIcon>../../assets/branding/souls-tracker-skull-compact.ico</ApplicationIcon>", desktopProject, StringComparison.Ordinal);
        Assert.Contains("SetupIconFile=..\\assets\\branding\\souls-tracker-skull-compact.ico", installer, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "assets", "branding", "souls-tracker-skull-compact.ico")));
        Assert.True(File.Exists(Path.Combine(root, "assets", "branding", "souls-tracker-skull-compact.png")));
    }

    [Fact]
    public void MinimumSupportedWindowKeepsTheWorkspaceVerticallyReachable()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Width = 560, Height = 400 };
                window.Show();
                window.UpdateLayout();

                ScrollViewer workspace = Assert.IsType<ScrollViewer>(window.FindName("MainContentScrollViewer"));
                Assert.True(workspace.ActualHeight > 0);
                Assert.Equal(ScrollBarVisibility.Auto, workspace.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Disabled, workspace.HorizontalScrollBarVisibility);
                Assert.True(Assert.IsType<ScrollViewer>(window.FindName("BossesScrollViewer")).ActualHeight > 0);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void GlobalManualGuidanceIsVisibleForEveryManualGame()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            var coordinator = new SerializedTrackerCoordinator(new PresentationRepository(), new NullPublisher());
            try
            {
                var viewModel = new DesktopTrackerViewModel(coordinator);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                viewModel.SelectGameAsync(viewModel.GameChoices.Single(choice => choice.GameId == GameId.Ds1)).GetAwaiter().GetResult();
                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();

                TextBlock guidance = Assert.IsType<TextBlock>(window.FindName("GlobalHotkeyStatusTextBlock"));
                Assert.Equal(Visibility.Collapsed, guidance.Visibility);

                window.Close();
                window = null;
                viewModel.SelectGameAsync(viewModel.GameChoices.Single(choice => choice.GameId == GameId.Bloodborne)).GetAwaiter().GetResult();
                Assert.True(viewModel.IsBloodborneSelected);
                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();
                guidance = Assert.IsType<TextBlock>(window.FindName("GlobalHotkeyStatusTextBlock"));
                Assert.Equal(Visibility.Visible, guidance.Visibility);

                window.Close();
                window = null;
                viewModel.SelectGameAsync(viewModel.GameChoices.Single(choice => choice.GameId == GameId.DemonsSouls)).GetAwaiter().GetResult();
                window = new MainWindow { DataContext = viewModel };
                window.Show();
                window.UpdateLayout();
                guidance = Assert.IsType<TextBlock>(window.FindName("GlobalHotkeyStatusTextBlock"));
                Assert.Equal(Visibility.Visible, guidance.Visibility);
            }
            finally
            {
                window?.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    private static void AssertOneWayTextBinding(MainWindow window, string controlName, string expectedBindingPath)
    {
        TextBox textBox = Assert.IsType<TextBox>(window.FindName(controlName));
        Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(textBox, TextBox.TextProperty));

        Assert.Equal(expectedBindingPath, binding.Path?.Path);
        Assert.Equal(BindingMode.OneWay, binding.Mode);
    }

    private static void AssertOneWayTextBlockBinding(MainWindow window, string controlName, string expectedBindingPath)
    {
        TextBlock textBlock = Assert.IsType<TextBlock>(window.FindName(controlName));
        Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(textBlock, TextBlock.TextProperty));
        Assert.Equal(expectedBindingPath, binding.Path?.Path);
        Assert.Equal(BindingMode.OneWay, binding.Mode);
    }

    private static void AssertVisibilityBinding(MainWindow window, string controlName, string expectedBindingPath)
    {
        StackPanel panel = Assert.IsType<StackPanel>(window.FindName(controlName));
        Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(panel, UIElement.VisibilityProperty));
        Assert.Equal(expectedBindingPath, binding.Path?.Path);
    }

    private static string Between(string text, string startMarker, string endMarker)
    {
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find start marker: {startMarker}");
        int end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find end marker: {endMarker}");
        return text[start..end];
    }

    private static void AssertOrder(string text, params string[] markers)
    {
        int previous = -1;
        foreach (string marker in markers)
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index > previous, $"Expected '{marker}' after the previous field.");
            previous = index;
        }
    }

    private static void AssertExplicitDarkComboBoxTemplate(ComboBox comboBox)
    {
        comboBox.ApplyTemplate();
        Assert.NotNull(comboBox.Template);
        Assert.IsType<Border>(comboBox.Template.FindName("ClosedSurface", comboBox));
        ToggleButton toggle = Assert.IsType<ToggleButton>(comboBox.Template.FindName("PART_ToggleButton", comboBox));
        Assert.Equal(2, Grid.GetColumnSpan(toggle));
        ContentPresenter selectionText = Assert.IsType<ContentPresenter>(comboBox.Template.FindName("SelectionText", comboBox));
        Assert.True(selectionText.IsHitTestVisible == false);
        Assert.True(Panel.GetZIndex(selectionText) > Panel.GetZIndex(toggle));
        Assert.IsType<Popup>(comboBox.Template.FindName("PART_Popup", comboBox));
    }

    private static void AssertNoPersistentFocusVisual(Control control)
    {
        Assert.Null(control.FocusVisualStyle);
        Assert.True(control.OverridesDefaultStyle);
    }

    private static void AssertComboBoxAccentIsLimitedToTheOpenDropDown(ComboBox comboBox)
    {
        Trigger[] triggers = comboBox.Template.Triggers.OfType<Trigger>().ToArray();

        Assert.Contains(triggers, trigger => trigger.Property == ComboBox.IsDropDownOpenProperty && Equals(trigger.Value, true));
        Assert.DoesNotContain(triggers, trigger => trigger.Property == Control.IsKeyboardFocusedProperty);
        Assert.DoesNotContain(triggers, trigger => trigger.Property == Control.IsKeyboardFocusWithinProperty);
    }

    private static void AssertOneWayCheckBoxBinding(
        MainWindow window,
        string controlName,
        string expectedCheckedPath,
        string expectedEnabledPath,
        string automationName)
    {
        CheckBox checkBox = Assert.IsType<CheckBox>(window.FindName(controlName));
        Binding checkedBinding = Assert.IsType<Binding>(BindingOperations.GetBinding(checkBox, CheckBox.IsCheckedProperty));
        Binding enabledBinding = Assert.IsType<Binding>(BindingOperations.GetBinding(checkBox, CheckBox.IsEnabledProperty));

        Assert.Equal(expectedCheckedPath, checkedBinding.Path?.Path);
        Assert.Equal(BindingMode.OneWay, checkedBinding.Mode);
        Assert.Equal(expectedEnabledPath, enabledBinding.Path?.Path);
        Assert.Equal(automationName, AutomationProperties.GetName(checkBox));
    }

    private static void AssertOneWayStatusBinding(MainWindow window, string controlName, string expectedBindingPath, string automationName)
    {
        TextBlock status = Assert.IsType<TextBlock>(window.FindName(controlName));
        Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(status, TextBlock.TextProperty));
        Binding helpTextBinding = Assert.IsType<Binding>(BindingOperations.GetBinding(status, AutomationProperties.HelpTextProperty));

        Assert.Equal(expectedBindingPath, binding.Path?.Path);
        Assert.Equal(BindingMode.OneWay, binding.Mode);
        Assert.Equal(automationName, AutomationProperties.GetName(status));
        Assert.Equal(expectedBindingPath, helpTextBinding.Path?.Path);
        Assert.Equal(BindingMode.OneWay, helpTextBinding.Mode);
    }

    private static void AssertPropertyBinding(MainWindow window, string controlName, string expectedBindingPath, DependencyProperty property)
    {
        Control control = Assert.IsAssignableFrom<Control>(window.FindName(controlName));
        Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(control, property));

        Assert.Equal(expectedBindingPath, binding.Path?.Path);
        Assert.Equal(BindingMode.Default, binding.Mode);
    }

    private static string GetInlineText(TextBlock textBlock) => string.Concat(textBlock.Inlines.OfType<Run>().Select(static run => run.Text));

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T matchingDescendant)
            {
                yield return matchingDescendant;
            }

            foreach (T nestedDescendant in FindVisualDescendants<T>(child))
            {
                yield return nestedDescendant;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SoulsTracker.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class SelectedDs1Repository : ITrackerStateRepository
    {
        private static readonly PersistentTrackerState State = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.Ds1,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 0),
            BossProgress.Empty,
            OverlayConfiguration.Default);

        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TrackerStateLoadResult.Loaded(State));

        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PresentationRepository : ITrackerStateRepository
    {
        public int SaveCount { get; private set; }

        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TrackerStateLoadResult.Loaded(PersistentTrackerState.Default));

        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TextExportPersistenceRepository(PersistentTrackerState? initialState = null) : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = initialState ?? PersistentTrackerState.Default;
        public int SaveCount { get; private set; }

        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TrackerStateLoadResult.Loaded(State));

        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            State = state;
            SaveCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void WaitForDispatcher(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (condition() || ticks >= 300)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert.True(condition(), "The routed WPF Save action did not publish its visible volume confirmation.");
    }
}
