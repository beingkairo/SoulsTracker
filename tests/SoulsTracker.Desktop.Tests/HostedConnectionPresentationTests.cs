using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedConnectionTests;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class HostedConnectionPresentationTests
{
    private static readonly bool[] CopyFailures = [false, true];
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HostedPublisherStatus.CredentialsRequired, "Credentials rejected. Import replacement pairing from the operator.")]
    [InlineData(HttpStatusCode.Conflict, HostedPublisherStatus.Conflict, "Another publisher owns this overlay. Close it before explicitly reconnecting.")]
    [InlineData(HttpStatusCode.BadRequest, HostedPublisherStatus.InvalidProtocol, "Hosted publication paused. Check pairing with the operator before reconnecting.")]
    public async Task CopyPreservesBoundDeliveryInstructions(HttpStatusCode code, HostedPublisherStatus expected, string instruction) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            HostedOverlayPublisher? sender = null;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => sender = new HostedOverlayPublisher(config, new RejectHandler(code)));
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator);
            vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                await WaitUntil(() => sender!.Status == expected && connection.StatusText == instruction);
                await Idle();
                var delivery = Tree(window).OfType<TextBlock>().Single(x => AutomationProperties.GetName(x) == "Hosted delivery status");
                var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
                foreach (bool fail in CopyFailures)
                {
                    Assert.True(copy.IsEnabled);
                    string? copied = null;
                    Assert.Equal(!fail, connection.CopyReadUrl(value =>
                    {
                        copied = value;
                        if (fail) throw new InvalidOperationException("synthetic clipboard detail");
                    }));
                    await Idle();
                    Assert.Equal(Configuration().BuildReadUrl(), copied);
                    Assert.Equal(instruction, delivery.Text);
                    Assert.True(delivery.IsVisible);
                    Assert.Equal(expected, sender!.Status);
                    var feedback = (TextBlock)window.FindName("HostedCopyStatus");
                    Assert.DoesNotContain(Tree(window).OfType<TextBlock>(), x => AutomationProperties.GetName(x) == "Hosted URL copy feedback");
                    Assert.Empty(((TextBlock)window.FindName("DirectoryCopyStatus")).Text);
                    var overlay = (Border)window.FindName("HostedCopyFeedbackOverlay");
                    Assert.Same(window.Content, overlay.Parent);
                    Assert.Equal(VerticalAlignment.Bottom, overlay.VerticalAlignment);
                    Assert.Equal(fail ? "Could not copy the URL. Try Copy OBS URL again." : "Read-only OBS URL copied. Keep the URL private.", feedback.Text);
                    Assert.True(feedback.IsVisible);
                    Assert.Equal(TextWrapping.Wrap, feedback.TextWrapping);
                    foreach (var element in Tree(window))
                    {
                        string surface = AutomationProperties.GetName(element) + AutomationProperties.GetHelpText(element)
                            + (element is FrameworkElement f ? f.ToolTip?.ToString() : "") + (element is TextBlock t ? t.Text : "");
                        Assert.DoesNotContain(new string('b', 64), surface);
                        Assert.DoesNotContain(new string('c', 64), surface);
                        Assert.DoesNotContain("synthetic clipboard detail", surface);
                    }
                }
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData("import")]
    [InlineData("reconnect")]
    [InlineData("remove")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task LifecycleStartClearsCopyFeedback(string action) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => new HostedOverlayPublisher(config, new RejectHandler(HttpStatusCode.Unauthorized)));
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await WaitUntil(() => connection.StatusText.StartsWith("Credentials", StringComparison.Ordinal));
            Assert.True(connection.CopyReadUrl(_ => { }));
            string feedback = connection.CopyFeedbackText;
            await connection.ImportAsync("unused", false);
            await connection.RemoveAsync(false);
            Assert.Equal(feedback, connection.CopyFeedbackText);
            bool sawReset = false;
            connection.PropertyChanged += (_, _) => sawReset |= connection.CopyFeedbackText.Length == 0;
            Task operation = Task.CompletedTask;
            switch (action)
            {
                case "import": operation = connection.ImportAsync(Path.Combine(root, "missing.json"), true); break;
                case "reconnect": operation = connection.ReconnectAsync(); break;
                case "remove": operation = connection.RemoveAsync(true); break;
                case "stop": connection.StopSetup(); break;
                case "dispose": operation = connection.DisposeAsync().AsTask(); break;
            }
            Assert.Empty(connection.CopyFeedbackText);
            Assert.True(sawReset);
            await operation;
            await Idle();
            Assert.Empty(connection.CopyFeedbackText);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task SenderEventsContinueIndependentlyOfCopyFeedback() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int requests = 0;
            var server = new Server
            {
                BeforeSend = async ct =>
                {
                    if (Interlocked.Increment(ref requests) == 1)
                    {
                        await firstRequest.Task.WaitAsync(ct);
                        throw new HttpRequestException("synthetic transport detail");
                    }
                }
            };
            HostedOverlayPublisher? sender = null;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                config => sender = new HostedOverlayPublisher(config, server, delay: (_, ct) => retry.Task.WaitAsync(ct)));
            await using var coordinator = new SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator);
            vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                await WaitUntil(() => Volatile.Read(ref requests) == 1);
                await CheckCopy("Hosted delivery pending.");
                firstRequest.SetResult();
                await WaitUntil(() => connection.StatusText.StartsWith("Retrying", StringComparison.Ordinal));
                Assert.StartsWith("Could not copy", connection.CopyFeedbackText, StringComparison.Ordinal);
                await CheckCopy("Retrying hosted delivery. Local tracking and TXT continue.");
                retry.SetResult();
                await WaitUntil(() => connection.StatusText.StartsWith("Latest offered", StringComparison.Ordinal));
                Assert.StartsWith("Could not copy", connection.CopyFeedbackText, StringComparison.Ordinal);
                await CheckCopy("Latest offered state acknowledged. Waiting for accepted game data if none is available yet.");
                await sender!.DisposeAsync();
                await WaitUntil(() => connection.StatusText == "Hosted publisher stopped.");
                await CheckCopy("Hosted publisher stopped.");
                await connection.DisposeAsync();
                await Idle();
                Assert.Empty(connection.CopyFeedbackText);
                Assert.False(((Button)window.FindName("CopyTotalDeathsOverlayUrlButton")).IsEnabled);

                async Task CheckCopy(string status)
                {
                    await Idle();
                    var delivery = Tree(window).OfType<TextBlock>().Single(x => AutomationProperties.GetName(x) == "Hosted delivery status");
                    var feedback = (TextBlock)window.FindName("HostedCopyStatus");
                    var senderStatus = sender!.Status;
                    int before = Volatile.Read(ref requests);
                    foreach (bool fail in CopyFailures)
                    {
                        Assert.True(((Button)window.FindName("CopyTotalDeathsOverlayUrlButton")).IsEnabled);
                        string? copied = null;
                        Assert.Equal(!fail, connection.CopyReadUrl(value =>
                        {
                            copied = value;
                            if (fail) throw new InvalidOperationException("synthetic clipboard detail");
                        }));
                        await Idle();
                        Assert.Equal(Configuration().BuildReadUrl(), copied);
                        Assert.Equal(status, delivery.Text);
                        Assert.Equal(connection.CopyFeedbackText, feedback.Text);
                        Assert.StartsWith(fail ? "Could not copy" : "Read-only OBS URL copied", feedback.Text, StringComparison.Ordinal);
                        Assert.Equal(senderStatus, sender.Status);
                        Assert.Equal(before, Volatile.Read(ref requests));
                    }
                }
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData(560d, 400d)]
    [InlineData(560d, 760d)]
    [InlineData(1060d, 760d)]
    public async Task ConsentFitsAvailableWidthAndRemainsCheckboxContent(double width, double height) => await OnDispatcher(async () =>
    {
        var window = new MainWindow { Width = width, Height = height, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
            await Idle();
            var consent = (CheckBox)window.FindName("HostedConsentCheckBox");
            consent.BringIntoView();
            await Idle();
            window.UpdateLayout();
            var label = Tree(consent).OfType<TextBlock>().Single();
            Assert.Equal("I agree to hosted publication when importing pairing", label.Text);
            Assert.Equal(TextWrapping.Wrap, label.TextWrapping);
            Assert.Same(label, consent.Content);
            Assert.True(consent.Focusable && consent.IsTabStop);
            Assert.NotNull(consent.FocusVisualStyle);
            Assert.Equal("Consent to hosted publication", System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(consent).GetName());
            var bounds = label.TransformToAncestor(consent).TransformBounds(new Rect(label.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= consent.ActualWidth + 1);
            Assert.True(bounds.Top >= -1 && bounds.Bottom <= consent.ActualHeight + 1, $"Label bounds {bounds}; checkbox {consent.RenderSize}");
            var measured = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight, TextWrapping = TextWrapping.Wrap };
            measured.Measure(new Size(label.ActualWidth, double.PositiveInfinity));
            Assert.True(label.ActualHeight >= measured.DesiredSize.Height - 1);
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            // The outer scrollbar gutter is separate from the panel's padding.
            Assert.Equal(12, ((FrameworkElement)scroll.Content).Margin.Right);
            Assert.InRange(consent.ActualWidth, scroll.ViewportWidth - 47, scroll.ViewportWidth - 45);
            var viewportBounds = label.TransformToAncestor(scroll).TransformBounds(new Rect(label.RenderSize));
            Assert.True(viewportBounds.Top >= 0 && viewportBounds.Bottom <= scroll.ActualHeight + 1);
            Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
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

    private sealed class RejectHandler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(code));
    }
}
