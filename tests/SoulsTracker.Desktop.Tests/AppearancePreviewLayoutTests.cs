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
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearancePreviewLayoutTests
{
    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task PreviewStaysRightOfCompactFields(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
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
            font.BringIntoView(); await Idle();
            AppearanceGeometryTests.Capture(window, $"layout-{width}-{height}");
            var p = preview.TranslatePoint(new Point(), window); var f = font.TranslatePoint(new Point(), window);
            Record($"layout-{width}-{height}", new { preview = new { p.X, p.Y, preview.ActualWidth, preview.ActualHeight }, font = new { f.X, f.Y, font.ActualWidth }, copy = Tree(window).OfType<TextBlock>().Select(x => x.Text).Where(x => x.Contains("preview", StringComparison.OrdinalIgnoreCase) || x.Contains("overlay uses")) });
            Assert.True(p.X >= f.X + font.ActualWidth + 8, "Preview must be to the right of the fields.");
            Assert.InRange(font.ActualWidth, 160, 300);
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "Preview");
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "Preview your changes before applying them to the hosted overlay. Large designs are scaled down to fit this preview; the live overlay keeps your chosen sizes.");
            Assert.Contains(Tree(window).OfType<TextBlock>(), x => x.Text == "The overlay uses these settings. Click Apply to live overlay to show your changes.");
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Assert.Empty(repository.Saves);
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
            scroll.ScrollToEnd(); await Idle(); AssertNativeBounds(browser, viewport, "end");
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
