using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

/// <summary>Local appearance display; application commands remain owned by the editor.</summary>
public sealed class AppearancePreview : Grid, IDisposable
{
    private DesktopTrackerViewModel? source;
    private bool updateQueued;
    private WebView2? browser;
    private Task? startTask;
    private readonly TextBlock unavailable = new() { Text = "Local preview is loading.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock caption = new() { Text = "Local preview. Draft edits are not published.", TextWrapping = TextWrapping.Wrap };
    public AppearancePreviewModel? Model { get; private set; }
    public bool IsDisposed { get; private set; }
    public bool IsReady { get; private set; }
    public int BlockedRequestCount { get; private set; }

    public AppearancePreview()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(180) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(caption, 1); Children.Add(caption);
        Children.Add(unavailable);
        DataContextChanged += (_, _) => BindSource();
    }

    public Task StartBrowserAsync(string userDataRoot) => startTask ??= StartCoreAsync(userDataRoot);

    private async Task StartCoreAsync(string userDataRoot)
    {
        if (IsDisposed) return;
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(userDataRoot, "AppearancePreview"),
                new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disk-cache-size=16777216"));
            if (IsDisposed) return;
            browser = new ClippedPreviewBrowser { Focusable = false, IsHitTestVisible = false, DefaultBackgroundColor = System.Drawing.Color.Transparent };
            Children.Insert(0, browser);
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await browser.EnsureCoreWebView2Async(environment, options);
            if (IsDisposed) return;
            var core = browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                BlockedRequestCount++;
                e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            string? initialDocumentUri = null;
            core.NavigationStarting += (_, e) =>
            {
                e.Cancel = initialDocumentUri is null || e.Uri != initialDocumentUri;
                if (!e.Cancel) initialDocumentUri = null;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.WebMessageReceived += BrowserMessageReceived;
            using var resource = typeof(AppearancePreview).Assembly.GetManifestResourceStream("SoulsTracker.Desktop.AppearancePreview.html")!;
            using var reader = new StreamReader(resource);
            string html = await reader.ReadToEndAsync();
            initialDocumentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html));
            core.NavigateToString(html);
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (IsDisposed) return;
            browser?.Dispose();
            if (browser is not null) Children.Remove(browser);
            browser = null;
            unavailable.Text = error is WebView2RuntimeNotFoundException
                ? "Local preview requires Microsoft WebView2 Runtime. Editing and Apply remain available."
                : "Local preview is unavailable. Editing and Apply remain available.";
        }
    }

    private void SendDraft()
    {
        if (Model is null || browser?.CoreWebView2 is null || IsDisposed) return;
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { appearance = Model.Appearance, value = Model.Value }, JsonSerializerOptions.Web));
    }

    private void BrowserMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (IsDisposed || e.Source != "about:blank" || e.WebMessageAsJson.Length > 80) return;
        string? message;
        try { message = JsonSerializer.Deserialize<string>(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        if (message == "ready") SendDraft();
        if (message == "rendered") { IsReady = true; unavailable.Visibility = Visibility.Collapsed; }
        if (!IsVisible || message is null || !message.StartsWith("wheel:", StringComparison.Ordinal) ||
            !double.TryParse(message.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out double delta) ||
            !double.IsFinite(delta) || Math.Abs(delta) > 600) return;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + delta);
            break;
        }
    }

    private void BindSource()
    {
        if (source is not null)
        {
            source.PropertyChanged -= SourceChanged;
            source.TotalDeathsAppearanceDraft.PropertyChanged -= DraftChanged;
        }
        source = DataContext as DesktopTrackerViewModel;
        if (source is null || IsDisposed) { Model = null; return; }
        Model = new AppearancePreviewModel(source.TotalDeathsAppearanceDraft, source.CurrentState?.OverlayConfiguration.TotalDeaths.Appearance ?? OverlayAppearance.Default);
        source.PropertyChanged += SourceChanged;
        source.TotalDeathsAppearanceDraft.PropertyChanged += DraftChanged;
        Refresh();
    }
    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesktopTrackerViewModel.TotalDeathsText) or nameof(DesktopTrackerViewModel.DraftTitleIconModeChoice)) QueueRefresh();
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
    private void QueueRefresh()
    {
        if (IsDisposed || updateQueued) return;
        updateQueued = true;
        Dispatcher.BeginInvoke(() => { updateQueued = false; if (!IsDisposed) Refresh(); }, DispatcherPriority.DataBind);
    }
    private void Refresh()
    {
        if (source is null || Model is null) return;
        Model.Update(source.DraftTitleIconModeChoice.Value, source.TotalDeathsText);
        caption.Text = Model.IsRepresentative ? "Local preview uses a sample count of 123. Draft edits are not published." : "Local preview. Draft edits are not published.";
        SendDraft();
    }
    // HwndHost is not clipped by WPF's ScrollViewer. Limit this preview's own
    // native window region without moving/resizing its document or using hooks.
    private sealed class ClippedPreviewBrowser : WebView2
    {
        private (int Left, int Top, int Right, int Bottom)? lastClip;

        public ClippedPreviewBrowser() => LayoutUpdated += UpdateClip;

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            UpdateClip(this, EventArgs.Empty);
        }

        private void UpdateClip(object? sender, EventArgs e)
        {
            if (!IsLoaded || Handle == nint.Zero) return;
            ScrollContentPresenter? viewport = null;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollContentPresenter presenter) { viewport = presenter; break; }
            }
            if (viewport is null || PresentationSource.FromVisual(viewport) is null || !GetWindowRect(Handle, out var bounds)) return;
            var top = viewport.PointToScreen(new System.Windows.Point());
            var bottom = viewport.PointToScreen(new System.Windows.Point(viewport.ActualWidth, viewport.ActualHeight));
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            var clip = (
                Left: Math.Clamp((int)Math.Ceiling(top.X) - bounds.Left, 0, width),
                Top: Math.Clamp((int)Math.Ceiling(top.Y) - bounds.Top, 0, height),
                Right: Math.Clamp((int)Math.Floor(bottom.X) - bounds.Left, 0, width),
                Bottom: Math.Clamp((int)Math.Floor(bottom.Y) - bounds.Top, 0, height));
            if (lastClip == clip) return;
            nint region = CreateRectRgn(clip.Left, clip.Top, clip.Right, clip.Bottom);
            if (region == nint.Zero) return;
            if (SetWindowRgn(Handle, region, true) == 0) { _ = DeleteObject(region); return; }
            // Windows owns the region after a successful SetWindowRgn.
            lastClip = clip;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) LayoutUpdated -= UpdateClip;
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
        [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (source is not null)
        {
            source.PropertyChanged -= SourceChanged;
            source.TotalDeathsAppearanceDraft.PropertyChanged -= DraftChanged;
        }
        source = null;
        IsReady = false;
        if (browser?.CoreWebView2 is { } core) core.WebMessageReceived -= BrowserMessageReceived;
        browser?.Dispose();
    }
}
