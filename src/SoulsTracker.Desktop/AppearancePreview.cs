using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Windows;
using System.Windows.Controls;
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
        Height = 220;
        RowDefinitions.Add(new RowDefinition());
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
            browser = new WebView2 { Focusable = false, IsHitTestVisible = false, DefaultBackgroundColor = System.Drawing.Color.Transparent };
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
            core.WebMessageReceived += (_, e) =>
            {
                if (IsDisposed || e.Source != "about:blank") return;
                string message = e.TryGetWebMessageAsString();
                if (message == "ready") SendDraft();
                if (message == "rendered") { IsReady = true; unavailable.Visibility = Visibility.Collapsed; }
            };
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
        browser?.Dispose();
    }
}
