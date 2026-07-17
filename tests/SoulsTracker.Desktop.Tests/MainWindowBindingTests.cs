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
        Assert.DoesNotContain("AutomationProperties.Name=\"Boss List padding\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Boss List corner radius\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Show selected game name\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Inline\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PickColor_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("local:ColorField", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Text effects\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Total Deaths outline\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Total Deaths shadow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Boss List outline\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Enable Boss List shadow\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose an overlay type", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("border-left", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "web_overlay", "src", "overlay.css")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualHotkeyGuidanceAndRecorderPromptUseTheApprovedWording()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml.cs"));
        Assert.Contains("Choose a field to record. Enter saves and returns to the main menu; Esc cancels.", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+Alt plus a key", xaml, StringComparison.Ordinal);
        Assert.Contains("Recording input", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Press Enter to save and go back to main menu.", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Press Esc to exit without saving.", codeBehind, StringComparison.Ordinal);
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
                CheckBox enabled = Assert.IsType<CheckBox>(window.FindName("DeathSoundEnabledCheckBox"));
                Assert.Equal("Enable death sound", AutomationProperties.GetName(enabled));
                TextBox volume = Assert.IsType<TextBox>(window.FindName("DeathSoundVolumeTextBox"));
                Assert.Equal("Death sound volume percentage", AutomationProperties.GetName(volume));
                Assert.True(volume.Focusable);
                Assert.DoesNotContain("Slider", volume.Name, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("DeathSoundVolume_KeyDown", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "SoulsTracker.Desktop", "MainWindow.xaml")), StringComparison.Ordinal);
                Assert.IsType<Button>(window.FindName("SaveDeathSoundVolumeButton"));
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
    public void TextExportSettingsExposeOnlyFileNamesAndGenericStatus()
    {
        RunOnStaThread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                AssertOneWayTextBlockBinding(window, "DeathsExportFileNameTextBlock", nameof(DesktopTrackerViewModel.DeathsExportFileName));
                AssertOneWayTextBlockBinding(window, "BossExportFileNameTextBlock", nameof(DesktopTrackerViewModel.BossExportFileName));
                AssertOneWayTextBlockBinding(window, "TextExportStatusTextBlock", nameof(DesktopTrackerViewModel.TextExportStatus));
            }
            finally { window?.Close(); }
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
                Assert.Equal(
                    "Elden Ring is labeled SOON and cannot be selected.",
                    Assert.IsType<TextBlock>(window.FindName("GameAvailabilityHintTextBlock")).Text);
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
    public void DesktopAndInstallerUseTheOriginalSkullIconResource()
    {
        string root = FindRepositoryRoot();
        string desktopProject = File.ReadAllText(Path.Combine(root, "src", "SoulsTracker.Desktop", "SoulsTracker.Desktop.csproj"));
        string installer = File.ReadAllText(Path.Combine(root, "installer", "SoulsTracker.iss"));

        Assert.Contains("<ApplicationIcon>../../assets/branding/souls-tracker-skull.ico</ApplicationIcon>", desktopProject, StringComparison.Ordinal);
        Assert.Contains("SetupIconFile=..\\assets\\branding\\souls-tracker-skull.ico", installer, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "assets", "branding", "souls-tracker-skull.ico")));
        Assert.True(File.Exists(Path.Combine(root, "assets", "branding", "souls-tracker-skull.png")));
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
