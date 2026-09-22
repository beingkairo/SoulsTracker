using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearancePreviewControlTests
{
    [Fact]
    public Task BrowserScrollClippingEvidence() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 560, Height = 400, Left = 100, Top = 100, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            preview.BringIntoView(); await Idle();
            await preview.StartBrowserAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoulsTracker-preview-tests", Guid.NewGuid().ToString("N")));
            await HostedDesktopPublisherTests.WaitUntil(() => preview.IsReady);
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 100); await Idle(); await Task.Delay(100);
            string? output = Environment.GetEnvironmentVariable("SOULS_APPEARANCE_EVIDENCE");
            if (output is not null)
            {
                System.IO.Directory.CreateDirectory(output);
                AppearanceGeometryTests.Capture(window, "preview-scroll-shell");
                var field = typeof(AppearancePreview).GetField("browser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var browser = (Microsoft.Web.WebView2.Wpf.WebView2)field.GetValue(preview)!;
                using var image = System.IO.File.Create(System.IO.Path.Combine(output, "preview-browser.png"));
                await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OfflineBrowserRendersDraftWithoutRequests() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var preview = new AppearancePreview { DataContext = vm, Width = 480, Height = 220 };
        var window = new MainWindow { Content = preview, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var start = preview.GetType().GetMethod("StartBrowserAsync");
            Assert.NotNull(start);
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoulsTracker-preview-tests", Guid.NewGuid().ToString("N"));
            await (Task)start.Invoke(preview, [root])!;
            var field = preview.GetType().GetField("browser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            dynamic browser = field.GetValue(preview)!;
            var core = (Microsoft.Web.WebView2.Core.CoreWebView2)browser.CoreWebView2;
            string navigation = "not observed";
            core.NavigationStarting += (_, e) => navigation = e.Uri[..Math.Min(100, e.Uri.Length)] + " canceled=" + e.Cancel;
            try { await HostedDesktopPublisherTests.WaitUntil(() => (bool)preview.GetType().GetProperty("IsReady")!.GetValue(preview)!); }
            catch
            {
                string diagnostic = await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({url:location.href,html:document.documentElement.outerHTML.slice(0,400),ready:document.readyState,render:typeof renderHosted,chrome:typeof window.chrome.webview,model:typeof pending})");
                throw new InvalidOperationException(navigation + " " + diagnostic);
            }
            string content = await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.overlay-heading').textContent");
            Assert.Contains("0", content);
            vm.TotalDeathsAppearanceDraft.Title = "Offline test"; await Idle();
            await Task.Delay(100);
            content = await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.overlay-heading').textContent");
            Assert.Contains("Offline test", content);
            Assert.Empty(repository.Saves);
            Assert.Equal(0, (int)preview.GetType().GetProperty("BlockedRequestCount")!.GetValue(preview)!);
        }
        finally { preview.Dispose(); window.Close(); }
    });

    [Fact]
    public Task LocalPreviewTracksDraftWithoutSavingAndDisposes() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var control = window.FindName("LocalAppearancePreview");
            Assert.NotNull(control);
            dynamic preview = control;
            Assert.Equal("0", (string)preview.Model.Value);
            vm.TotalDeathsAppearanceDraft.Title = "Local draft";
            vm.TotalDeathsAppearanceDraft.FontSize = "36";
            await Idle();
            Assert.Equal("Local draft", (string)preview.Model.Appearance.Title);
            Assert.Equal(36, (int)preview.Model.Appearance.FontSize);
            Assert.Empty(repository.Saves);
            vm.TotalDeathsAppearanceDraft.FontSize = "-";
            vm.TotalDeathsAppearanceDraft.IconColor = "#123456"; await Idle();
            Assert.Equal(36, (int)preview.Model.Appearance.FontSize);
            Assert.Equal("#123456", (string)preview.Model.Appearance.IconColor);
            Assert.Equal("-", vm.TotalDeathsAppearanceDraft.FontSize);
            Assert.Empty(repository.Saves);
            window.Close();
            Assert.True((bool)preview.IsDisposed);
        }
        finally { window.Close(); }
    });
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
