using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedConnectionTests;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

// The real Overlay XAML and click handler are composed with local-only transport.
// No Desktop App startup, system cursor, production HTTP handler, or user data root is involved.
[Collection("Shell presentation")]
public sealed class HostedToggleInteractionTests
{
    private const string Origin = "https://overlay.beingkairo.com";

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task MouseAndSpaceToggleRetainFocusAndShowOffAndOfflineStates(int width, int height) => OnDispatcher(async () =>
    {
        string root = Root();
        try
        {
            var preference = new HostedOverlayPreferenceStore(root);
            Assert.False(await preference.ResolveAsync(_ => Task.FromResult(false)));
            var pairing = Pairing(root);
            await pairing.SaveAsync(Configuration());
            int clients = 0, senders = 0, requests = 0;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, pairing,
                config =>
                {
                    senders++; return new HostedOverlayPublisher(config, new Handler((_, _) =>
                {
                    requests++;
                    throw new HttpRequestException("Synthetic offline");
                }));
                },
                preferenceStore: preference, initiallyEnabled: false,
                createProvisioningClient: () =>
                {
                    clients++; return new HostedOverlayProvisioningClient(Origin,
                    new Handler((_, _) => throw new InvalidOperationException("No provisioning allowed for an active pairing")));
                });
            await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(GameId.DemonsSouls), connection);
            var vm = new DesktopTrackerViewModel(coordinator);
            vm.ConfigureHostedOverlay(connection);
            await vm.InitializeAsync();
            await connection.InitializeAsync(vm.CurrentState!);
            var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
            try
            {
                window.Show();
                ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await Idle();
                var check = (CheckBox)window.FindName("HostedOverlayEnabledCheckBox");
                Assert.Equal("Enable hosted Overlay", new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check).GetName());
                Assert.Equal("Enable hosted Overlay", check.Content);
                Assert.Null(window.FindName("HostedOverlayStatusTextBlock"));
                Assert.False(((StackPanel)window.FindName("SettingsContentStack")).IsAncestorOf(check));
                Assert.True(((ScrollViewer)window.FindName("OverlayConfigurationScrollViewer")).IsAncestorOf(check));
                Assert.False(check.IsChecked);
                Assert.Contains("off", connection.StatusText, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, clients); Assert.Equal(0, senders); Assert.Equal(0, requests);
                AssertContained(check, window);
                SetInputModality(Keyboard.PrimaryDevice);
                Assert.True(check.Focus()); await Idle();
                AssertFocus(check);

                // WPF mouse routing, with synthetic hover; no OS cursor movement.
                Hover(check);
                check.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                check.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                // Routed pointer events leave WPF's capture behind when the OS
                // button was never pressed. Release it before the Space path.
                if (Mouse.Captured == check) Mouse.Capture(null);
                Assert.Null(Mouse.Captured);
                Hover(null);
                // Synthetic routed pointer events cannot change the OS button state;
                // dispatch WPF's click event for the pointer-release activation.
                check.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await WaitUntil(() => connection.IsEnabled);
                await WaitUntil(() => connection.StatusText.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
                    connection.StatusText.Contains("could not connect", StringComparison.OrdinalIgnoreCase));
                Assert.True(check.IsChecked); AssertFocus(check);
                Assert.True(await preference.ResolveAsync(_ => Task.FromResult(false)));
                Assert.Equal(1, senders); Assert.Equal(1, clients);
                Assert.True(requests > 0);

                SetInputModality(Keyboard.PrimaryDevice);
                SendSpace(check, window); await WaitUntil(() => !connection.IsEnabled);
                await WaitUntil(() => connection.CanChangeEnabled);
                await Idle();
                Assert.False(check.IsChecked); AssertFocus(check);
                Assert.Contains("off", connection.StatusText, StringComparison.OrdinalIgnoreCase);
                Assert.False(await preference.ResolveAsync(_ => Task.FromResult(true)));
                int stoppedRequests = requests;
                await connection.EnsureProvisionedAsync(); await connection.RetryAsync(); await Idle();
                Assert.Equal(stoppedRequests, requests);
                Assert.Equal(1, senders); Assert.Equal(1, clients);
            }
            finally { Hover(null); window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public Task PendingErrorAndDisableRemainLocalWithRealOverlayClick() => OnDispatcher(async () =>
    {
        string root = Root();
        try
        {
            var preference = new HostedOverlayPreferenceStore(root);
            Assert.False(await preference.ResolveAsync(_ => Task.FromResult(false)));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int clients = 0, requests = 0, senders = 0;
            var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, Pairing(root),
                _ => { senders++; throw new InvalidOperationException("No sender on failed provisioning"); },
                new HostedProvisioningStateStore(Path.Combine(root, "pending.private"), new CurrentUserDpapiSecretProtector(), HostedProductionOrigins.Approved),
                retryDelay: (_, _) => Task.CompletedTask, preferenceStore: preference, initiallyEnabled: false,
                createProvisioningClient: () =>
                {
                    clients++;
                    return new HostedOverlayProvisioningClient(Origin, new Handler(async (_, ct) =>
                    {
                        requests++; entered.TrySetResult(); await release.Task.WaitAsync(ct);
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }));
                });
            await using (connection)
            await using (var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(GameId.DemonsSouls), connection))
            {
                var vm = new DesktopTrackerViewModel(coordinator); vm.ConfigureHostedOverlay(connection);
                await vm.InitializeAsync(); await connection.InitializeAsync(vm.CurrentState!);
                var window = new MainWindow { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
                try
                {
                    window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
                    var check = (CheckBox)window.FindName("HostedOverlayEnabledCheckBox");
                    Assert.Equal(0, clients); Assert.Equal(0, requests);
                    SetInputModality(Keyboard.PrimaryDevice);
                    Assert.True(check.Focus());
                    SendSpace(check, window);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Idle();
                    Assert.True(check.IsChecked);
                    Assert.True(connection.IsPreparing);
                    Assert.Contains("Preparing", connection.StatusText);
                    Assert.Contains("Preparing", FindUrl(window).Text);
                    SetInputModality(Keyboard.PrimaryDevice); Assert.True(check.Focus()); await Idle();
                    Assert.True(await preference.ResolveAsync(_ => Task.FromResult(false)));
                    AssertFocus(check);
                    release.SetResult();
                    await WaitUntil(() => connection.CanRetry); await Idle();
                    Assert.Contains("could not be prepared", connection.StatusText, StringComparison.Ordinal);
                    Assert.Equal(3, requests); Assert.Equal(0, senders); Assert.Equal(1, clients);
                    Assert.True(check.IsEnabled);
                    AssertFocus(check);
                    SendSpace(check, window);
                    await WaitUntil(() => !connection.IsEnabled);
                    await Idle();
                    Assert.False(check.IsChecked); AssertFocus(check);
                    Assert.Contains("off", connection.StatusText, StringComparison.OrdinalIgnoreCase);
                    Assert.False(await preference.ResolveAsync(_ => Task.FromResult(true)));
                    await connection.RetryAsync(); await Idle(); Assert.Equal(3, requests);
                }
                finally { release.TrySetResult(); window.Close(); }
            }
        }
        finally { Directory.Delete(root, true); }
    });

    private static HostedPublisherConfiguration Configuration() => HostedPublisherConfiguration.Create(Origin,
        new string('a', 32), new string('b', 64), new string('c', 64), HostedProductionOrigins.Approved);
    private static HostedPublisherConfigurationStore Pairing(string root) => new(Path.Combine(root, "pairing.private"),
        new CurrentUserDpapiSecretProtector(), HostedProductionOrigins.Approved);
    private static string Root() { string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); await Idle(); }
        Assert.True(condition());
    }
    private static void Hover(IInputElement? element) => typeof(MouseDevice)
        .GetMethod("ChangeMouseOver", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(Mouse.PrimaryDevice, [element, Environment.TickCount]);
    private static void SetInputModality(InputDevice device) =>
        typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!.SetValue(InputManager.Current, device);
    private static void SendSpace(CheckBox check, Window window)
    {
        Assert.Same(check, Keyboard.FocusedElement);
        var keyboard = new PressedKeyboard(); keyboard.Focus(check);
        InputManager.Current.ProcessInput(new KeyEventArgs(keyboard, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space)
        { RoutedEvent = Keyboard.KeyDownEvent });
        InputManager.Current.ProcessInput(new KeyEventArgs(keyboard, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space)
        { RoutedEvent = Keyboard.KeyUpEvent });
    }
    private static void AssertFocus(CheckBox check)
    {
        Assert.Same(check, Keyboard.FocusedElement);
        Assert.True(check.IsKeyboardFocused);
        Assert.Contains(AdornerLayer.GetAdornerLayer(check)?.GetAdorners(check) ?? [], adorner => adorner.IsVisible);
    }
    private static void AssertContained(FrameworkElement element, Window window)
    {
        element.BringIntoView(); window.UpdateLayout();
        var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
        var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        var bounds = element.TransformToVisual(viewport).TransformBounds(new Rect(element.RenderSize));
        Assert.True(new Rect(viewport.RenderSize).Contains(bounds), $"{element.Name}: {bounds} / {viewport.RenderSize}");
    }
    private static TextBox FindUrl(Window window) => DirectoryPresentationControlTests.Tree(window)
        .OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Overlay URL");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class PressedKeyboard : KeyboardDevice
    {
        internal PressedKeyboard() : base(InputManager.Current) { }
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key == Key.Space ? KeyStates.Down : KeyStates.None;
    }
}
