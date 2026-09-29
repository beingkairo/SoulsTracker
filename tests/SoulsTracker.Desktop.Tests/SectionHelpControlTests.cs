using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class SectionHelpControlTests
{
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
            var row = Assert.IsType<StackPanel>(help.Parent);
            var title = Assert.Single(row.Children.OfType<TextBlock>());
            Assert.Equal(Orientation.Horizontal, row.Orientation);
            Assert.Equal(VerticalAlignment.Center, title.VerticalAlignment);
            Assert.Equal(8, row.Margin.Bottom);
            var panel = Assert.IsType<StackPanel>(row.Parent);
            var next = Assert.IsAssignableFrom<FrameworkElement>(panel.Children[panel.Children.IndexOf(row) + 1]);
            Assert.InRange(next.TranslatePoint(new Point(), panel).Y - (row.TranslatePoint(new Point(), panel).Y + row.ActualHeight), 7.5, 8.5);
            Assert.InRange(Math.Abs(title.TranslatePoint(new Point(0, title.ActualHeight / 2), row).Y - help.TranslatePoint(new Point(0, help.ActualHeight / 2), row).Y), 0, 0.5);
            var scroll = (ScrollViewer)window.FindName(workspace == "Overlay" ? "OverlayConfigurationScrollViewer" : "SettingsContentScrollViewer");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + row.TranslatePoint(new Point(), viewport).Y);
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
            var row = Assert.IsType<StackPanel>(help.Parent);
            var title = Assert.Single(row.Children.OfType<TextBlock>());
            Assert.Equal(VerticalAlignment.Center, title.VerticalAlignment);
            // Retain the compact card's two-DIP gap and a usable 32-by-24 hit target.
            Assert.Equal(2, row.Margin.Bottom);
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
    public Task PrimaryHeadersShareBoldTypographyWithoutRestylingContent(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            Style? sharedStyle = null;
            foreach (var (workspace, sections) in new[]
            {
                ("Overlay", new[] { ("Overlay", "OVERLAY"), ("Preview", "PREVIEW"), ("Appearance", "APPEARANCE") }),
                ("Settings", new[] { ("Updates", "UPDATES"), ("GlobalHotkeys", "GLOBAL HOTKEYS"), ("StreamingTextExport", "STREAMING TEXT EXPORT") })
            })
            {
                ((TabItem)window.FindName(workspace + "WorkspaceTab")).IsSelected = true; await Idle();
                foreach (var (section, label) in sections)
                {
                    var help = (Button)window.FindName(section + "HelpButton");
                    var row = (StackPanel)help.Parent;
                    var title = row.Children.OfType<TextBlock>().Single();
                    Assert.Equal(label, title.Text);
                    Assert.Equal(FontWeights.Bold, title.FontWeight);
                    Assert.Equal(14, title.FontSize);
                    sharedStyle ??= title.Style;
                    Assert.Same(sharedStyle, title.Style);
                    Assert.Equal(DependencyProperty.UnsetValue, title.ReadLocalValue(TextBlock.FontSizeProperty));
                    Assert.Equal(DependencyProperty.UnsetValue, title.ReadLocalValue(TextBlock.FontWeightProperty));
                    Assert.Equal(new Thickness(8, 0, 0, 0), help.Margin);
                    Assert.Equal(new Thickness(0, 0, 0, 8), row.Margin);
                    foreach (var body in Tree((StackPanel)row.Parent).OfType<TextBlock>().Where(x => !ReferenceEquals(x, title)))
                    {
                        Assert.NotSame(sharedStyle, body.Style);
                        Assert.True(body.FontSize < title.FontSize, $"Content inherited heading size: {body.Text}");
                    }
                    Assert.True(help.FontSize < title.FontSize);
                }
            }
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            ((ScrollViewer)window.FindName("OverlayConfigurationScrollViewer")).ScrollToEnd(); await Idle();
            var dockedHelp = (Button)window.FindName("DockedPreviewHelpButton");
            var dockedTitle = ((StackPanel)dockedHelp.Parent).Children.OfType<TextBlock>().Single();
            Assert.Equal("PREVIEW", dockedTitle.Text);
            Assert.Same(sharedStyle, dockedTitle.Style);
            Assert.Equal(14, dockedTitle.FontSize);
            Assert.Equal(FontWeights.Bold, dockedTitle.FontWeight);
            Assert.Equal(DependencyProperty.UnsetValue, dockedTitle.ReadLocalValue(TextBlock.FontSizeProperty));
            Assert.Equal(new Thickness(8, 0, 0, 0), dockedHelp.Margin);
            Assert.Empty(repository.Saves);
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
