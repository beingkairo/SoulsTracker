using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearancePreviewLayoutTests
{
    private static readonly bool[] PreviewModes = [false, true];

    [Fact]
    public Task CompactSampleCaptionLeavesAnEditableViewport() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.Ds3);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            Assert.True(preview.Model!.IsRepresentative);
            Assert.Equal("123", preview.Model.Value);
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            scroll.ScrollToEnd(); await Idle();
            Assert.Contains(Tree(preview).OfType<TextBlock>(), x => x.Text == "Local preview uses a sample count of 123." && x.IsVisible);
            Assert.True(Tree(scroll).OfType<ScrollContentPresenter>().First().ActualHeight >= 32);
            var dock = preview.Parent;
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new(GameId.Ds3, 0, DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(repository.State))));
            await Idle();
            Assert.False(preview.Model.IsRepresentative);
            Assert.Equal("0", preview.Model.Value);
            Assert.Same(dock, preview.Parent);
            var slot = (FrameworkElement)window.FindName("NormalPreviewSlot");
            Assert.Equal(180 + preview.RowDefinitions[1].ActualHeight, slot.ActualHeight, 1);
            vm.ApplyRuntimeReaderResult(RuntimeGameReadResult.WaitingForActiveCharacter(GameId.Ds3)); await Idle();
            Assert.True(preview.Model.IsRepresentative);
            Assert.Equal(180 + preview.RowDefinitions[1].ActualHeight, slot.ActualHeight, 1);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task PreviewStartsFullWidthAboveCompactFields(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            var font = Tree(window).OfType<AppearanceFontField>().Single();
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var content = (FrameworkElement)scroll.Content;
            AppearanceGeometryTests.Capture(window, $"layout-{width}-{height}");
            var p = preview.TranslatePoint(new Point(), window); var f = font.TranslatePoint(new Point(), window);
            Record($"layout-{width}-{height}", new { preview = new { p.X, p.Y, preview.ActualWidth, preview.ActualHeight }, font = new { f.X, f.Y, font.ActualWidth }, copy = Tree(window).OfType<TextBlock>().Select(x => x.Text).Where(x => x.Contains("preview", StringComparison.OrdinalIgnoreCase) || x.Contains("overlay uses")) });
            var previewInContent = preview.TranslatePoint(new Point(), content);
            var fontInContent = font.TranslatePoint(new Point(), content);
            Assert.True(previewInContent.Y + preview.ActualHeight < fontInContent.Y, "Preview must precede the controls in flow.");
            Assert.True(preview.ActualWidth > content.ActualWidth - 40, "Preview must use the full panel width.");
            Assert.Equal(((FrameworkElement)window.FindName("AppearanceFields")).ActualWidth - 120, font.ActualWidth, 1);
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "Preview");
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "Preview your changes before applying them to the hosted overlay. Large designs are scaled down to fit this preview. The live overlay keeps your chosen sizes.");
            Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => x.Text == "Local preview. Draft edits are not published.");
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "The overlay uses these settings. Click Apply to live overlay to show your changes.");
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task CompactCardKeepsFullScrollbarAndFormWidths(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
            var settingsScroll = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            settingsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible; await Idle();
            var settings = (ScrollBar)settingsScroll.Template.FindName("PART_VerticalScrollBar", settingsScroll);
            var expected = settings.TransformToAncestor(window).TransformBounds(new Rect(settings.RenderSize));
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var normal = bar.TransformToAncestor(window).TransformBounds(new Rect(bar.RenderSize));
            Assert.Equal(expected, normal);
            var fields = (FrameworkElement)window.FindName("AppearanceFields");
            var section = (FrameworkElement)window.FindName("NormalPreviewSection");
            Assert.Equal(section.ActualWidth - 34, fields.ActualWidth, 1);
            scroll.ScrollToEnd(); await Idle();
            Assert.Equal(normal, bar.TransformToAncestor(window).TransformBounds(new Rect(bar.RenderSize)));
            var card = Assert.IsType<Border>(window.FindName("DockedPreviewCard"));
            Assert.True(card.IsVisible);
            Assert.Contains(Tree(card).OfType<TextBlock>(), x => x.Text == "Preview" && x.IsVisible);
            Assert.DoesNotContain(Tree(card).OfType<TextBlock>(), x => x.Text.StartsWith("Preview your changes", StringComparison.Ordinal));
            var bounds = card.TransformToAncestor(window).TransformBounds(new Rect(card.RenderSize));
            Assert.True(bounds.Right < normal.Left);
            var workspace = (FrameworkElement)window.FindName("OverlayWorkspaceLayout");
            double left = workspace.TranslatePoint(new Point(), window).X;
            Assert.Equal(bounds.Left - left, normal.Left - bounds.Right, 1);
            double beforeWheel = scroll.VerticalOffset;
            card.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent }); await Idle();
            Assert.True(scroll.VerticalOffset < beforeWheel, "The card heading must route wheel input to the page too.");
            AppearanceGeometryTests.Capture(window, $"compact-card-{width}-{height}");
            Record($"compact-card-{width}-{height}", new { normal = normal.ToString(System.Globalization.CultureInfo.InvariantCulture), expected = expected.ToString(System.Globalization.CultureInfo.InvariantCulture), card = bounds.ToString(System.Globalization.CultureInfo.InvariantCulture), fields.ActualWidth });
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task OnePreviewDocksOnlyAfterSectionLeavesAndReturns(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            var section = (FrameworkElement)window.FindName("NormalPreviewSection");
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            var normalParent = preview.Parent;
            var model = preview.Model;
            await preview.StartBrowserAsync(Path.Combine(Path.GetTempPath(), "SoulsTracker-preview-tests", Guid.NewGuid().ToString("N")));
            await HostedDesktopPublisherTests.WaitUntil(() => preview.IsReady);
            var browser = Tree(preview).OfType<WebView2>().Single();
            var core = browser.CoreWebView2;
            preview.BringIntoView(); await Idle(); await WaitForPaint(browser);
            await CaptureBrowser(browser, $"normal-browser-{width}-{height}");
            AppearanceGeometryTests.Capture(window, $"normal-shell-{width}-{height}");
            AssertNativeBounds(browser, viewport, $"normal-{width}-{height}");
            ((Button)window.FindName("ApplyAppearanceButton")).Focus(); await Idle();
            var focus = Keyboard.FocusedElement;
            double threshold = section.TranslatePoint(new Point(0, section.ActualHeight), viewport).Y + scroll.VerticalOffset;
            double sectionTop = section.TranslatePoint(new Point(), viewport).Y + scroll.VerticalOffset;
            scroll.ScrollToVerticalOffset(sectionTop); await Idle();
            AppearanceGeometryTests.Capture(window, $"normal-section-{width}-{height}");
            scroll.ScrollToVerticalOffset(sectionTop + 10); await Idle();
            Assert.Same(normalParent, preview.Parent);
            AssertNativeBounds(browser, viewport, $"partial-title-{width}-{height}");
            for (int crossing = 0; crossing < 3; crossing++)
            {
                scroll.ScrollToVerticalOffset(threshold - 1); await Idle();
                Assert.Same(normalParent, preview.Parent);
                var fields = (FrameworkElement)window.FindName("AppearanceFields");
                double beforeCrossing = fields.TranslatePoint(new Point(), window).Y;
                scroll.ScrollToVerticalOffset(threshold + 1); await Idle();
                Assert.Equal(beforeCrossing - 2, fields.TranslatePoint(new Point(), window).Y, 1);
                Assert.NotSame(normalParent, preview.Parent);
                Assert.Equal(height == 400 ? 72 : 96, preview.RowDefinitions[0].ActualHeight);
                Assert.Same(model, preview.Model);
                Assert.Same(core, Tree(preview).OfType<WebView2>().Single().CoreWebView2);
                Assert.Same(focus, Keyboard.FocusedElement);
                Assert.Single(Tree(window).OfType<AppearancePreview>());
                await WaitForPaint(browser);
                if (crossing == 0) await CaptureBrowser(browser, $"docked-browser-{width}-{height}");
                AssertNativeBounds(browser, (FrameworkElement)preview.Parent, $"docked-{width}-{height}-{crossing}");
                AppearanceGeometryTests.Capture(window, $"docked-{width}-{height}-{crossing}");
                Assert.True(viewport.ActualHeight >= 32, "Docking must leave room for a complete editor hit target.");
                double y = preview.TranslatePoint(new Point(), window).Y;
                double viewportTop = viewport.TranslatePoint(new Point(), window).Y;
                Assert.True(y >= ((FrameworkElement)window.FindName("OverlayWorkspaceLayout")).TranslatePoint(new Point(), window).Y);
                Assert.True(y + preview.ActualHeight <= viewportTop);
                double offset = scroll.VerticalOffset;
                await Idle(); Assert.Equal(offset, scroll.VerticalOffset);
                beforeCrossing = fields.TranslatePoint(new Point(), window).Y;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 2); await Idle();
                Assert.Same(normalParent, preview.Parent);
                Assert.Equal(beforeCrossing + 2, fields.TranslatePoint(new Point(), window).Y, 1);
                Assert.Equal(180, preview.RowDefinitions[0].ActualHeight);
            }
            vm.TotalDeathsAppearanceDraft.Title = "Docked draft";
            vm.TotalDeathsAppearanceDraft.ShadowEnabled = true;
            await Idle();
            var lastEditor = Tree(window).OfType<AppearanceNumberField>().Last();
            var input = Tree(lastEditor).OfType<TextBox>().First();
            input.Focus(); input.BringIntoView(); await Idle();
            Assert.NotSame(normalParent, preview.Parent);
            var inputBounds = input.TransformToAncestor(viewport).TransformBounds(new Rect(input.RenderSize));
            AppearanceGeometryTests.Capture(window, $"focused-editor-{width}-{height}");
            Record($"focused-editor-{width}-{height}", new { inputBounds = inputBounds.ToString(System.Globalization.CultureInfo.InvariantCulture), viewport.ActualHeight, scroll.VerticalOffset, scroll.ScrollableHeight });
            Assert.True(inputBounds.Top >= -1 && inputBounds.Bottom <= viewport.ActualHeight + 1, $"Editor {inputBounds}; viewport {viewport.ActualHeight}");
            await WaitForPaint(browser);
            Assert.Contains("Docked draft", await core.ExecuteScriptAsync("document.querySelector('section').textContent"));
            await core.ExecuteScriptAsync("window.wheelEvents=0;addEventListener('wheel',()=>window.wheelEvents++)");
            ((Button)window.FindName("ApplyAppearanceButton")).Focus(); await Idle();
            focus = Keyboard.FocusedElement;
            foreach (int delta in new[] { 80, -80, 600, -600 })
            {
                double before = scroll.VerticalOffset;
                double insetBeforeWheel = viewport.Margin.Top;
                double expected = Math.Clamp(before + delta, 0, scroll.ScrollableHeight);
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseWheel", x = 40, y = 40, deltaX = 0, deltaY = delta }));
                await Task.Delay(100); await Idle();
                // A crossing compensates the presenter inset in the scroll offset;
                // the requested page motion remains exactly one wheel delta.
                expected = Math.Clamp(expected + viewport.Margin.Top - insetBeforeWheel, 0, scroll.ScrollableHeight);
                Assert.Equal(expected, scroll.VerticalOffset, 1);
                Assert.Same(focus, Keyboard.FocusedElement);
            }
            Assert.Equal("4", await core.ExecuteScriptAsync("window.wheelEvents"));
            Assert.Equal("0", await core.ExecuteScriptAsync("scrollY"));
            scroll.ScrollToEnd(); await Idle();
            foreach (var size in new[] { (560, 400), (560, 760), (1060, 760) })
            {
                window.Width = size.Item1; window.Height = size.Item2; await Idle();
                Assert.NotSame(normalParent, preview.Parent);
                AssertNativeBounds(browser, (FrameworkElement)preview.Parent, $"resize-docked-{width}-{height}-{size.Item1}-{size.Item2}");
            }
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
            Assert.False(IsWindowVisible(browser.Handle));
            var settingsScroll = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            double settingsRight = settingsScroll.TranslatePoint(new Point(settingsScroll.ActualWidth, 0), window).X;
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            Assert.Equal(settingsRight, scroll.TranslatePoint(new Point(scroll.ActualWidth, 0), window).X, 1);
            Assert.Same(core, browser.CoreWebView2);
            Assert.Same(model, preview.Model);
            AssertNativeBounds(browser, (FrameworkElement)preview.Parent, $"tab-return-docked-{width}-{height}");
            scroll.ScrollToHome(); await Idle();
            Assert.Same(normalParent, preview.Parent);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task TabReturnPreservesViewportAndVisibleContentFocus(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var draft = vm.TotalDeathsAppearanceDraft;
        draft.ShadowEnabled = true;
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            var workspace = (FrameworkElement)window.FindName("OverlayWorkspaceLayout");
            var fields = (FrameworkElement)window.FindName("AppearanceFields");
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            var model = preview.Model;
            var normal = preview.Parent;
            foreach (bool sticky in PreviewModes)
            {
                window.Width = width; window.Height = height; await Idle();
                ((Button)window.FindName("ApplyAppearanceButton")).Focus();
                scroll.ScrollToVerticalOffset(sticky ? scroll.ScrollableHeight : 0); await Idle();
                if (sticky)
                {
                    Tree(Tree(fields).OfType<AppearanceNumberField>().Last()).OfType<TextBox>().Single().Focus();
                    await Idle();
                }
                for (int round = 0; round < 3; round++)
                {
                    double offset = scroll.VerticalOffset;
                    double fieldTop = fields.TranslatePoint(new Point(), window).Y;
                    ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
                    ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
                    Assert.Equal(offset, scroll.VerticalOffset, 1);
                    Assert.Equal(fieldTop, fields.TranslatePoint(new Point(), window).Y, 1);
                    AssertState(sticky);
                }
                double beforeResize = scroll.VerticalOffset, inset = viewport.Margin.Top;
                ((TabItem)window.FindName("MainWorkspaceTab")).IsSelected = true; await Idle();
                window.Width = width == 1060 ? 560 : 1060;
                window.Height = height == 400 ? 760 : 400; await Idle();
                ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
                Assert.Equal(Math.Clamp(beforeResize + viewport.Margin.Top - inset, 0, scroll.ScrollableHeight), scroll.VerticalOffset, 1);
                AssertState(sticky);
            }
            Assert.Empty(repository.Saves);

            void AssertState(bool sticky)
            {
                Assert.Equal(sticky, !ReferenceEquals(normal, preview.Parent));
                Assert.Same(model, preview.Model);
                Assert.Same(draft, vm.TotalDeathsAppearanceDraft);
                var focused = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
                Assert.IsNotType<TabItem>(focused);
                Assert.True(workspace.IsAncestorOf(focused));
                Assert.True(focused.IsVisible && focused.IsEnabled && focused.Focusable);
                if (viewport.IsAncestorOf(focused))
                {
                    var bounds = focused.TransformToAncestor(viewport).TransformBounds(new Rect(focused.RenderSize));
                    Assert.True(bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1);
                }
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NativeBrowserWheelScrollsContainingViewOnceWithoutFocusChange() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 1060, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            preview.BringIntoView(); await Idle();
            await preview.StartBrowserAsync(Path.Combine(Path.GetTempPath(), "SoulsTracker-preview-tests", Guid.NewGuid().ToString("N")));
            await HostedDesktopPublisherTests.WaitUntil(() => preview.IsReady);
            var browser = Tree(preview).OfType<WebView2>().Single();
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            ((Button)window.FindName("ApplyAppearanceButton")).Focus(); await Idle();
            await WaitForPaint(browser);
            var focus = Keyboard.FocusedElement; double before = scroll.VerticalOffset;
            string dimensions = await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({width:innerWidth,height:innerHeight,panel:document.querySelector('.souls-tracker-overlay-panel').getBoundingClientRect().toJSON(),transform:getComputedStyle(document.querySelector('main')).transform})");
            await browser.CoreWebView2.ExecuteScriptAsync("window.wheelEvents=0;addEventListener('wheel',()=>window.wheelEvents++)");
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseWheel\",\"x\":40,\"y\":40,\"deltaX\":0,\"deltaY\":80}");
            await Task.Delay(150); await Idle();
            double after = scroll.VerticalOffset;
            Record("native-wheel", new { before, after, dimensions, browserBase = browser.GetType().BaseType?.FullName, wheelEvents = await browser.CoreWebView2.ExecuteScriptAsync("window.wheelEvents"), focusRetained = ReferenceEquals(focus, Keyboard.FocusedElement) });
            Assert.Equal(before + 80, after, 1);
            Assert.Equal("1", await browser.CoreWebView2.ExecuteScriptAsync("window.wheelEvents"));
            Assert.Same(focus, Keyboard.FocusedElement);
            Assert.Equal("0", await browser.CoreWebView2.ExecuteScriptAsync("scrollY"));
            await browser.CoreWebView2.ExecuteScriptAsync("for(const value of [{},null,123,'wheel:NaN','wheel:Infinity','wheel:601','wheel:-601','wheel:'+'1'.repeat(100)])chrome.webview.postMessage(value)");
            await Task.Delay(100); await Idle();
            Assert.Equal(after, scroll.VerticalOffset);
            Assert.Equal(0, preview.BlockedRequestCount);
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NativeBrowserVisibleRegionStaysInsideScrollViewport() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            preview.BringIntoView(); await Idle();
            await preview.StartBrowserAsync(Path.Combine(Path.GetTempPath(), "SoulsTracker-preview-tests", Guid.NewGuid().ToString("N")));
            await HostedDesktopPublisherTests.WaitUntil(() => preview.IsReady);
            var browser = Tree(preview).OfType<WebView2>().Single();
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            await WaitForPaint(browser);
            string? output = Environment.GetEnvironmentVariable("SOULS_APPEARANCE_EVIDENCE");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                AppearanceGeometryTests.Capture(window, "loaded-preview-shell");
                using var image = File.Create(Path.Combine(output, "loaded-preview-browser.png"));
                await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            var slot = browser.TranslatePoint(new Point(), viewport);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + slot.Y + 60); await Idle();
            AssertNativeBounds(browser, viewport, "partial");
            scroll.ScrollToEnd(); await Idle(); AssertNativeBounds(browser, (FrameworkElement)preview.Parent, "end-docked");
            scroll.ScrollToHome(); await Idle(); AssertNativeBounds(browser, viewport, "home");
            window.Height = 760; preview.BringIntoView(); await Idle(); AssertNativeBounds(browser, viewport, "tall");
            window.Width = 1060; window.Height = 760; preview.BringIntoView(); await Idle(); AssertNativeBounds(browser, viewport, "resize");
            ((TabItem)window.FindName("MainWorkspaceTab")).IsSelected = true; await Idle();
            Assert.False(IsWindowVisible(browser.Handle));
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; preview.BringIntoView(); await Idle();
            Assert.True(IsWindowVisible(browser.Handle));
            AssertNativeBounds(browser, viewport, "return");
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    private static async Task WaitForPaint(WebView2 browser)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('main').dataset.fit") != "\"painted\"")
            await Task.Delay(1, timeout.Token);
    }

    private static async Task CaptureBrowser(WebView2 browser, string name)
    {
        string? output = Environment.GetEnvironmentVariable("SOULS_APPEARANCE_EVIDENCE");
        if (output is null) return;
        Directory.CreateDirectory(output);
        using var image = File.Create(Path.Combine(output, name + ".png"));
        await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
    }

    private static void AssertNativeBounds(WebView2 browser, FrameworkElement viewport, string name)
    {
        GetWindowRect(browser.Handle, out var bounds);
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            int kind = GetWindowRgn(browser.Handle, region); int regionKind = GetRgnBox(region, out var clip);
            var top = viewport.PointToScreen(new Point()); var bottom = viewport.PointToScreen(new Point(viewport.ActualWidth, viewport.ActualHeight));
            Record("native-" + name, new { visible = IsWindowVisible(browser.Handle), kind, regionKind, bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }, clip = new { clip.Left, clip.Top, clip.Right, clip.Bottom }, viewport = new { top.X, top.Y, right = bottom.X, bottom = bottom.Y } });
            if (!IsWindowVisible(browser.Handle) || kind == 1) return;
            double left = bounds.Left + (kind > 1 ? clip.Left : 0), upper = bounds.Top + (kind > 1 ? clip.Top : 0);
            double right = kind > 1 ? bounds.Left + clip.Right : bounds.Right, lower = kind > 1 ? bounds.Top + clip.Bottom : bounds.Bottom;
            Assert.True(left >= top.X - 1 && upper >= top.Y - 1 && right <= bottom.X + 1 && lower <= bottom.Y + 1, "Actual native visible region must stay within the scroll viewport.");
        }
        finally { DeleteObject(region); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);

    internal static void Record(string name, object value)
    {
        string? output = Environment.GetEnvironmentVariable("SOULS_APPEARANCE_EVIDENCE");
        if (output is null) return;
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(value));
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
