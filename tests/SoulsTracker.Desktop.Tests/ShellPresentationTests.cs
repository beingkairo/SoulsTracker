using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Data;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

// WPF's process-wide BAML metadata initialization is not safe across concurrent
// first-window loads. Keep actual-window suites in one serial collection.
[CollectionDefinition("Shell presentation", DisableParallelization = true)]
public sealed class ShellPresentationTestGroup;

[Collection("Shell presentation")]
public sealed class ShellPresentationTests
{
    private static readonly string[] HotkeyStatuses = ["Global hotkeys are active.", "Hotkey conflict. Choose another binding.", "Choose the other binding or apply the change."];
    private static readonly string[] AppearanceFields = ["Title", "FontFamily", "FontSize", "TextColor", "TextOpacity", "IconColor", "BackgroundEnabled", "BackgroundColor", "BackgroundOpacity", "OutlineEnabled", "OutlineColor", "OutlineWidth", "ShadowEnabled", "ShadowColor", "ShadowOffsetX", "ShadowOffsetY", "ShadowBlur"];

    [Fact]
    public Task ShellAccentAndSelectionUseRedWithoutChangingOverlayDefaults() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            await Idle();
            Assert.Equal(Color.FromRgb(232, 154, 154), ((SolidColorBrush)window.Resources["AccentBrush"]).Color);
            Assert.Equal(Color.FromRgb(74, 45, 50), ((SolidColorBrush)window.Resources["SelectionBrush"]).Color);
            var input = (TextBox)window.FindName("IncrementHotkeyTextBox");
            Assert.Equal(Color.FromRgb(232, 154, 154), ((SolidColorBrush)input.SelectionBrush).Color);
            var tab = (TabItem)window.FindName("MainWorkspaceTab");
            var selectedTab = (Border)tab.Template.FindName("TabSurface", tab);
            Assert.Equal(Color.FromRgb(74, 45, 50), ((SolidColorBrush)selectedTab.Background).Color);
            // The cached style belongs to the first test's dispatcher. Build the same
            // palette focus template on this dispatcher for visual inspection.
            var style = (Style)typeof(ColorField).GetMethod("CreateKeyboardFocusVisualStyle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
            var template = (ControlTemplate)style.Setters.OfType<Setter>().Single(x => x.Property == Control.TemplateProperty).Value;
            template.Seal();
            Assert.Equal(Color.FromRgb(232, 154, 154), ((SolidColorBrush)((Border)template.LoadContent()).BorderBrush).Color);
            Assert.Equal("#A78BFA", new OverlayAppearanceDraft().AccentColor);
            Assert.Equal("#A78BFA", OverlayAppearance.Default.AccentColor);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public Task LongSetupAndSettingsRowsRemainInsideViewport(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow
        {
            Width = width,
            Height = height,
            ShowActivated = false,
            ShowInTaskbar = false,
            DataContext = new
            {
                IsEldenRingSelected = true,
                IsBlackMythWukongSelected = true,
                IsLiesOfPSelected = true,
                IsEldenRingSaveSelectorVisible = true,
                IsEldenRingBrowseVisible = true,
                IsEldenRingChangeVisible = true,
                IsEldenRingCancelVisible = true,
                IsWukongSaveSelectorVisible = true,
                IsWukongBrowseVisible = true,
                IsWukongChangeVisible = true,
                IsWukongCancelVisible = true,
                IsLiesOfPSaveSelectorVisible = true,
                IsLiesOfPBrowseVisible = true,
                IsLiesOfPChangeVisible = true,
                IsLiesOfPCancelVisible = true,
                HasCheckedForUpdates = true,
                CanCheckForUpdates = true,
                CanOpenAvailableUpdateReleasePage = true,
                UpdateCurrentVersion = "1.3.2",
                UpdateLatestVersion = "1.3.3",
                UpdateCheckStatus = "An update is available. Open the release page to download it."
            }
        };
        try
        {
            window.Show();
            foreach (var (tab, scrollName) in new[] { ("MainWorkspaceTab", "MainContentScrollViewer"), ("SettingsWorkspaceTab", "SettingsContentScrollViewer") })
            {
                ((TabItem)window.FindName(tab)).IsSelected = true;
                await Idle();
                var scroll = (ScrollViewer)window.FindName(scrollName);
                foreach (var control in Tree(scroll).OfType<Control>().Where(x => x.IsVisible && (x is Button || x is ComboBox)).ToArray())
                {
                    control.BringIntoView();
                    await Idle();
                    AssertFits(control, scroll);
                }
                if (tab == "SettingsWorkspaceTab")
                {
                    var status = (TextBlock)window.FindName("UpdateCheckStatusTextBlock");
                    status.BringIntoView();
                    await Idle();
                    AssertFits(status, scroll);
                    var measured = new TextBlock { Text = status.Text, FontFamily = status.FontFamily, FontSize = status.FontSize, TextWrapping = TextWrapping.Wrap };
                    measured.Measure(new Size(status.ActualWidth, double.PositiveInfinity));
                    Assert.True(status.ActualHeight >= measured.DesiredSize.Height - 1);
                }
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public Task AppearanceFieldsAreDirectAndApplyResetRemainReachable(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var discovery = new EmptyDiscovery();
        var vm = new DesktopTrackerViewModel(coordinator, blackMythWukongSaveDiscovery: discovery, eldenRingSaveDiscovery: discovery, liesOfPSaveDiscovery: discovery);
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
            vm.TotalDeathsAppearanceDraft.BackgroundEnabled = true;
            vm.TotalDeathsAppearanceDraft.OutlineEnabled = true;
            vm.TotalDeathsAppearanceDraft.ShadowEnabled = true;
            vm.DraftTitleIconModeChoice = vm.TitleIconModes.Single(x => x.Value == OverlayTitleIconMode.PrefixSkull);
            await Idle();
            Assert.Single(Tree(window).OfType<TabControl>());
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "Put this as a browser source in your OBS or as a Link Source in TikTok Live Studio to display it on stream");
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "The overlay uses these settings. Click Apply to live overlay to show your changes.");
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text.Contains("does not withdraw publication consent", StringComparison.Ordinal));
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            foreach (string field in AppearanceFields)
            {
                var control = Tree(window).OfType<FrameworkElement>().Single(x => BoundPath(x) == $"TotalDeathsAppearanceDraft.{field}");
                Assert.True(control.IsVisible, field);
                control.BringIntoView();
                await Idle();
                AssertFits(control, scroll);
                if (control is ColorField color)
                {
                    foreach (FrameworkElement child in color.Children) AssertFits(child, scroll);
                }
            }
            var title = Tree(window).OfType<TextBox>().Single(x => BoundPath(x) == "TotalDeathsAppearanceDraft.Title");
            title.Text = "Synthetic total";
            title.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            var apply = Tree(window).OfType<Button>().Single(x => Equals(x.Content, "Apply to live overlay"));
            apply.BringIntoView();
            await Idle();
            AssertFixedActionFits(apply, scroll);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsBusy && repository.State.OverlayConfiguration.TotalDeaths.Appearance.Title == "Synthetic total");
            var reset = (Button)window.FindName("ResetSelectedOverlayAppearanceButton");
            reset.BringIntoView();
            await Idle();
            AssertFixedActionFits(reset, scroll);
            var confirmReset = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            {
                var dialog = window.OwnedWindows.Cast<Window>().Single();
                Tree(dialog).OfType<Button>().Single(x => Equals(x.Content, "Reset")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await confirmReset;
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsBusy && repository.State.OverlayConfiguration.TotalDeaths.Appearance.Title == OverlayAppearance.Default.Title);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(OverlayAppearance.Default), System.Text.Json.JsonSerializer.Serialize(repository.State.OverlayConfiguration.TotalDeaths.Appearance));
            Assert.Equal(OverlayConfiguration.Default.TotalDeaths.Appearance.Title, vm.TotalDeathsAppearanceDraft.Title);
        }
        finally { window.Close(); }
    });

    private static string? BoundPath(FrameworkElement control) => control switch
    {
        TextBox text => BindingOperations.GetBinding(text, TextBox.TextProperty)?.Path.Path,
        ComboBox combo => BindingOperations.GetBinding(combo, ComboBox.SelectedItemProperty)?.Path.Path,
        CheckBox check => BindingOperations.GetBinding(check, CheckBox.IsCheckedProperty)?.Path.Path,
        ColorField color => BindingOperations.GetBinding(color, ColorField.ValueProperty)?.Path.Path,
        AppearanceNumberField number => BindingOperations.GetBinding(number, AppearanceNumberField.ValueProperty)?.Path.Path,
        AppearanceFontField font => BindingOperations.GetBinding(font, AppearanceFontField.SelectedFontProperty)?.Path.Path,
        _ => null
    };

    private static void AssertFixedActionFits(FrameworkElement control, ScrollViewer scroll)
    {
        var layout = (FrameworkElement)scroll.Parent;
        var bounds = control.TransformToAncestor(layout).TransformBounds(new Rect(control.RenderSize));
        Assert.True(bounds.Left >= 0 && bounds.Right <= layout.ActualWidth + 1);
        // The page scrollbar spans the action row; only the editable presenter
        // must end above the fixed actions.
        var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
        double contentBottom = viewport.TranslatePoint(new Point(0, viewport.ActualHeight), layout).Y;
        Assert.True(bounds.Top >= contentBottom && bounds.Bottom <= layout.ActualHeight + 1);
    }

    private static void AssertFits(FrameworkElement control, ScrollViewer scroll)
    {
        var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1, $"{control.GetType().Name} horizontal bounds {bounds}; viewport {viewport.RenderSize}");
        Assert.True(bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1, $"{control.GetType().Name} vertical bounds {bounds}; viewport {viewport.RenderSize}");
    }

    private sealed class MemoryRepository : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = new(PersistentTrackerState.CurrentSchemaVersion, GameId.DemonsSouls, OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true);
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyDiscovery : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>([]);
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    [Fact]
    public Task HelpReplacesBodyExplanationsAndOnlyActiveHotkeyStatusIsHidden() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            foreach (string status in HotkeyStatuses)
            {
                ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true;
                window.DataContext = new
                {
                    IsHotkeyRecording = false,
                    IsEldenRingNoticeVisible = false,
                    IsGlobalHotkeyConfigurationAvailable = true,
                    GlobalHotkeyStatus = status,
                    GlobalHotkeyUsageDescription = "Synthetic hotkey usage.",
                    IsEldenRingMissedDeathAdjustmentAvailable = true
                };
                await Idle();
                var feedback = (TextBlock)window.FindName("GlobalHotkeyStatusTextBlock");
                Assert.Equal(status != "Global hotkeys are active.", feedback.IsVisible);
                Assert.Equal(status, feedback.Text);
            }
            const string missed = "Some PvP and invasion-style deaths, including the Great Jar challenge, are excluded from Elden Ring's save files. Customizable hotkeys for increment and decrement of deaths can be found in the Settings tab.";
            foreach (var (name, expected) in new[] { ("MissedDeathsHelpButton", missed), ("GlobalHotkeysHelpButton", "Synthetic hotkey usage. Choose a field to record. Enter saves; Esc cancels. Both return to the view where recording started.") })
            {
                ((TabItem)window.FindName(name == "MissedDeathsHelpButton" ? "MainWorkspaceTab" : "SettingsWorkspaceTab")).IsSelected = true;
                await Idle();
                var help = Assert.IsType<Button>(window.FindName(name));
                Assert.True(help.Focusable && help.IsTabStop && help.IsEnabled);
                Assert.Equal(expected, AutomationProperties.GetHelpText(help));
                var tooltip = Assert.IsType<ToolTip>(help.ToolTip);
                Assert.True(ToolTipService.GetIsEnabled(help));
                help.BringIntoView();
                await Idle();
                Assert.True(help.Focus());
                await Idle();
                Assert.True(help.IsKeyboardFocused);
                Assert.True(tooltip.IsOpen);
                Assert.Equal(expected, Assert.IsType<TextBlock>(tooltip.Content).Text);
                ((TabControl)window.FindName("WorkspaceTabs")).Focus();
                await Idle();
                Assert.False(tooltip.IsOpen);
            }
            ((TabItem)window.FindName("MainWorkspaceTab")).IsSelected = true;
            await Idle();
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "Add any missed deaths manually");
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.Text == missed || x.Text == "Synthetic hotkey usage.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DefaultWindowUsesCompactDimensions() => HostedConnectionTests.OnDispatcher(() =>
    {
        var window = new MainWindow();
        try
        {
            Assert.Equal(560, window.Width);
            Assert.Equal(560, window.MinWidth);
            Assert.Equal(760, window.Height);
            Assert.Equal(400, window.MinHeight);
            Assert.Equal("Just a simple death tracker.", ((TextBlock)window.FindName("HeaderSubtitleTextBlock")).Text);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public Task WorkspacesUseFullWidthAndScrollToLastControl(double width, double height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { Width = width, Height = height, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            foreach (var (tabName, scrollName) in new[] { ("MainWorkspaceTab", "MainContentScrollViewer"), ("OverlayWorkspaceTab", "OverlayConfigurationScrollViewer") })
            {
                ((TabItem)window.FindName(tabName)).IsSelected = true;
                await Idle();
                var scroll = (ScrollViewer)window.FindName(scrollName);
                var layout = (FrameworkElement)scroll.Parent;
                Assert.InRange(scroll.ActualWidth, layout.ActualWidth - 1, layout.ActualWidth + 1);
                Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.Equal(0, scroll.ScrollableWidth);
                var panel = Tree(scroll).OfType<Border>().First(x => AutomationProperties.GetName(x).EndsWith("panel", StringComparison.Ordinal));
                Assert.Equal(0, ((FrameworkElement)scroll.Content).Margin.Right);
                Assert.InRange(panel.ActualWidth, scroll.ViewportWidth - 1, scroll.ViewportWidth + 1);
                scroll.ScrollToEnd();
                await Idle();
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
                if (tabName == "OverlayWorkspaceTab")
                {
                    var reset = (Button)window.FindName("ResetSelectedOverlayAppearanceButton");
                    AssertFixedActionFits(reset, scroll);
                }
            }
        }
        finally { window.Close(); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
