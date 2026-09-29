using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class SectionHelpControlTests
{
    private static readonly string[] OverlayHelpNames = ["Overlay", "Preview", "Appearance", "DockedPreview"];

    public static IEnumerable<object[]> SectionCases()
    {
        foreach (var (width, height) in new[] { (560, 400), (560, 760), (1060, 760) })
        {
            yield return [width, height, "Overlay", "Overlay", "Your overlay URL is a private link. Anyone with it can view the overlay, so only share it where you need to."];
            yield return [width, height, "Preview", "Overlay", "Preview your changes before applying them to the overlay. Large designs are scaled down to fit this preview. The live overlay keeps your chosen sizes."];
            yield return [width, height, "Appearance", "Overlay", "The overlay uses these settings. Click Apply to live overlay to show your changes."];
            yield return [width, height, "Updates", "Settings", "Checks GitHub for the latest updates."];
            yield return [width, height, "StreamingTextExport", "Settings", "Write local TXT files for text sources in your streaming software."];
        }
    }

    [Theory]
    [MemberData(nameof(SectionCases))]
    public Task SectionExplanationIsAccessibleHelpInsteadOfBodyCopy(int width, int height, string section, string workspace, string text) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show();
            var tab = (TabItem)window.FindName(workspace + "WorkspaceTab");
            tab.IsSelected = true; await Idle();
            var help = Assert.IsType<Button>(window.FindName(section + "HelpButton"));
            help.BringIntoView(); await Idle();
            Assert.Same(window.FindResource("ContextHelpButton"), help.Style);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(help)));
            Assert.Equal(text, AutomationProperties.GetHelpText(help));
            Assert.True(help.IsVisible && help.IsEnabled && help.IsHitTestVisible && help.Focusable && help.IsTabStop);
            Assert.True(ToolTipService.GetIsEnabled(help));
            Assert.Equal(60000, ToolTipService.GetShowDuration(help));
            Assert.NotNull(help.FocusVisualStyle);
            var hit = window.InputHitTest(help.TranslatePoint(new Point(help.ActualWidth / 2, help.ActualHeight / 2), window));
            Assert.True(ReferenceEquals(help, hit) || help.IsAncestorOf(Assert.IsAssignableFrom<DependencyObject>(hit)));
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.Text == text);
            var header = Assert.IsType<HeaderedContentControl>(help.Parent);
            header.ApplyTemplate();
            var title = Assert.IsType<TextBlock>(header.Template.FindName("PrimarySectionHeaderTitle", header));
            var divider = Assert.IsType<Border>(header.Template.FindName("PrimarySectionHeaderDivider", header));
            Assert.Equal(VerticalAlignment.Center, title.VerticalAlignment);
            Assert.Equal(new Thickness(0, 4, 0, 8), divider.Margin);
            Assert.InRange(Math.Abs(title.TranslatePoint(new Point(0, title.ActualHeight / 2), header).Y - help.TranslatePoint(new Point(0, help.ActualHeight / 2), header).Y), 0, 0.5);
            var scroll = (ScrollViewer)window.FindName(workspace == "Overlay" ? "OverlayConfigurationScrollViewer" : "SettingsContentScrollViewer");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + header.TranslatePoint(new Point(), viewport).Y);
            await Idle();
            Assert.True(help.Focus()); await Idle();
            Assert.True(help.IsKeyboardFocused);
            var tooltip = Assert.IsType<ToolTip>(help.ToolTip);
            Assert.True(tooltip.IsOpen);
            var description = Assert.IsType<TextBlock>(tooltip.Content);
            Assert.Equal(text, description.Text);
            Assert.Equal(TextWrapping.Wrap, description.TextWrapping);
            Assert.Equal(320, description.MaxWidth);
            AppearanceGeometryTests.Capture(window, $"section-{section}-{width}x{height}");

            AppearanceGeometryTests.Capture(tooltip, $"tooltip-{section}-{width}x{height}");
            tab.Focus(); await Idle();
            Assert.False(tooltip.IsOpen);
            AssertPointerAvailable(help);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task DockedPreviewRetainsTheSameAccessibleExplanation(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            var model = preview.Model;
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            scroll.ScrollToEnd(); await Idle();
            var card = (Border)window.FindName("DockedPreviewCard");
            Assert.True(card.IsVisible);
            var help = Assert.IsType<Button>(window.FindName("DockedPreviewHelpButton"));
            Assert.Same(window.FindResource("ContextHelpButton"), help.Style);
            Assert.Equal(AutomationProperties.GetHelpText((Button)window.FindName("PreviewHelpButton")), AutomationProperties.GetHelpText(help));
            Assert.Equal("Preview help", AutomationProperties.GetName(help));
            Assert.True(help.IsVisible && help.IsHitTestVisible && help.IsEnabled && help.IsTabStop);
            Assert.True(ToolTipService.GetIsEnabled(help));
            Assert.Equal(60000, ToolTipService.GetShowDuration(help));
            var header = Assert.IsType<HeaderedContentControl>(help.Parent);
            header.ApplyTemplate();
            var title = Assert.IsType<TextBlock>(header.Template.FindName("PrimarySectionHeaderTitle", header));
            var divider = Assert.IsType<Border>(header.Template.FindName("PrimarySectionHeaderDivider", header));
            Assert.Equal(VerticalAlignment.Center, title.VerticalAlignment);
            Assert.Equal(new Thickness(0, 4, 0, 8), divider.Margin);
            Assert.True(help.ActualWidth >= 32 && help.ActualHeight >= 24);
            Assert.DoesNotContain(Tree(card).OfType<TextBlock>(), x => x.Text == AutomationProperties.GetHelpText(help));
            Assert.True(help.Focus()); await Idle();
            Assert.True(help.IsKeyboardFocused);
            var tooltip = Assert.IsType<ToolTip>(help.ToolTip);
            Assert.True(tooltip.IsOpen);
            Assert.Equal(AutomationProperties.GetHelpText(help), Assert.IsType<TextBlock>(tooltip.Content).Text);
            Assert.Equal(TextWrapping.Wrap, ((TextBlock)tooltip.Content).TextWrapping);
            AppearanceGeometryTests.Capture(window, $"section-docked-preview-{width}x{height}");
            AppearanceGeometryTests.Capture(tooltip, $"tooltip-docked-preview-{width}x{height}");
            ((Button)window.FindName("ApplyAppearanceButton")).Focus(); await Idle();
            Assert.False(tooltip.IsOpen);
            Assert.Same(model, preview.Model);
            Assert.Same(window.FindName("DockedPreviewSlot"), preview.Parent);
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            Assert.True(viewport.ActualHeight >= 32, $"Editable viewport height: {viewport.ActualHeight}");
            AssertPointerAvailable(help);
            scroll.ScrollToTop(); await Idle();
            Assert.False(card.IsVisible);
            Assert.Same(window.FindName("NormalPreviewSlot"), preview.Parent);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task PrimaryHeadersShareOneRegularHeaderAndDividerWithoutRestylingContent(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            Style? sharedStyle = null;
            Brush? sharedDividerBrush = null;
            foreach (var (workspace, sections) in new[]
            {
                ("Main", new[] { ("GameSession", "GAME SESSION"), ("TotalDeaths", "TOTAL DEATHS") }),
                ("Overlay", new[] { ("Overlay", "OVERLAY URL"), ("Preview", "PREVIEW"), ("Appearance", "APPEARANCE") }),
                ("Settings", new[] { ("Updates", "UPDATES"), ("GlobalHotkeys", "GLOBAL HOTKEYS"), ("StreamingTextExport", "STREAMING TEXT EXPORT") })
            })
            {
                ((TabItem)window.FindName(workspace + "WorkspaceTab")).IsSelected = true; await Idle();
                foreach (var (section, label) in sections)
                {
                    var header = Assert.IsType<HeaderedContentControl>(window.FindName(section + "SectionHeader"));
                    header.ApplyTemplate();
                    var title = Assert.IsType<TextBlock>(header.Template.FindName("PrimarySectionHeaderTitle", header));
                    var divider = Assert.IsType<Border>(header.Template.FindName("PrimarySectionHeaderDivider", header));
                    Assert.Equal(label, header.Header);
                    Assert.Equal(label, title.Text);
                    Assert.Equal(FontWeights.Normal, title.FontWeight);
                    Assert.Equal(14, title.FontSize);
                    sharedStyle ??= title.Style;
                    Assert.Same(sharedStyle, title.Style);
                    Assert.Equal(DependencyProperty.UnsetValue, title.ReadLocalValue(TextBlock.FontSizeProperty));
                    Assert.Equal(DependencyProperty.UnsetValue, title.ReadLocalValue(TextBlock.FontWeightProperty));
                    sharedDividerBrush ??= divider.BorderBrush;
                    Assert.Same(sharedDividerBrush, divider.BorderBrush);
                    Assert.Equal(new Thickness(0, 0, 0, 1), divider.BorderThickness);
                    Assert.Equal(0.45, divider.Opacity);
                    Assert.Equal(new Thickness(0, 4, 0, 8), divider.Margin);
                    var help = Tree(header).OfType<Button>().SingleOrDefault();
                    if (help is not null) Assert.Equal(new Thickness(8, 0, 0, 0), help.Margin);
                    foreach (var body in Tree((FrameworkElement)header.Parent).OfType<TextBlock>().Where(x => !ReferenceEquals(x, title)))
                    {
                        Assert.NotSame(sharedStyle, body.Style);
                    }
                    if (help is not null) Assert.True(help.FontSize < title.FontSize);
                }
                AppearanceGeometryTests.Capture(window, $"primary-headers-{workspace}-{width}x{height}");
            }
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            ((ScrollViewer)window.FindName("OverlayConfigurationScrollViewer")).ScrollToEnd(); await Idle();
            var dockedHeader = (HeaderedContentControl)window.FindName("DockedPreviewSectionHeader");
            dockedHeader.ApplyTemplate();
            var dockedTitle = (TextBlock)dockedHeader.Template.FindName("PrimarySectionHeaderTitle", dockedHeader);
            var dockedDivider = (Border)dockedHeader.Template.FindName("PrimarySectionHeaderDivider", dockedHeader);
            Assert.Equal("PREVIEW", dockedHeader.Header);
            Assert.Same(sharedStyle, dockedTitle.Style);
            Assert.Equal(14, dockedTitle.FontSize);
            Assert.Equal(FontWeights.Normal, dockedTitle.FontWeight);
            Assert.Equal(DependencyProperty.UnsetValue, dockedTitle.ReadLocalValue(TextBlock.FontSizeProperty));
            Assert.Same(sharedDividerBrush, dockedDivider.BorderBrush);
            Assert.Equal(new Thickness(0, 0, 0, 1), dockedDivider.BorderThickness);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OverlayNavigationAndResponsiveLayoutDoNotOpenHelpUntilIntentionalFocus() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { Width = 560, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate(); await Idle();
            var main = (TabItem)window.FindName("MainWorkspaceTab");
            var overlay = (TabItem)window.FindName("OverlayWorkspaceTab");
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var helps = OverlayHelpNames
                .Select(x => (Button)window.FindName(x + "HelpButton")).ToArray();

            foreach (var help in helps) Assert.False(((ToolTip)help.ToolTip).IsOpen);
            overlay.IsSelected = true; await Idle();
            foreach (var help in helps) Assert.False(((ToolTip)help.ToolTip).IsOpen);
            main.IsSelected = true; await Idle(); overlay.IsSelected = true; await Idle();
            window.Width = 1060; window.Height = 760; await Idle();
            scroll.ScrollToEnd(); await Idle(); scroll.ScrollToTop(); await Idle();
            foreach (var help in helps) Assert.False(((ToolTip)help.ToolTip).IsOpen);

            var overlayHelp = helps[0];
            Assert.True(overlayHelp.Focus()); await Idle();
            Assert.True(((ToolTip)overlayHelp.ToolTip).IsOpen);
            main.IsSelected = true; await Idle();
            Assert.False(((ToolTip)overlayHelp.ToolTip).IsOpen);
            overlay.IsSelected = true; await Idle();
            Assert.False(((ToolTip)overlayHelp.ToolTip).IsOpen);
            Assert.True(overlayHelp.IsKeyboardFocused);
            Assert.True(((FrameworkElement)window.FindName("AppearanceActions")).Focus()); await Idle();
            Assert.False(((ToolTip)overlayHelp.ToolTip).IsOpen);
        }
        finally { window.Close(); }
    });

    internal static void AssertPointerAvailable(Button help)
    {
        // Exercise WPF hover routing without moving the operating-system cursor.
        // Timer-driven native pointer opening remains an independent interaction check.
        var transition = typeof(MouseDevice).GetMethod("ChangeMouseOver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            transition.Invoke(Mouse.PrimaryDevice, [help, Environment.TickCount]);
            Assert.True(help.IsMouseOver);
            Assert.False(help.IsKeyboardFocused);
            Assert.True(ToolTipService.GetIsEnabled(help));
            Assert.True(help.IsEnabled && help.IsHitTestVisible);
            Assert.Equal(AutomationProperties.GetHelpText(help), Assert.IsType<TextBlock>(((ToolTip)help.ToolTip).Content).Text);
        }
        finally
        {
            transition.Invoke(Mouse.PrimaryDevice, [null, Environment.TickCount]);
        }
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
