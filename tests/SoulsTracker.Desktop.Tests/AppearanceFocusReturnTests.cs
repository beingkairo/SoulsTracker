using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearanceFocusReturnTests
{
    private static readonly int[] WheelDeltas = [80, -80, 600, -600];
    private static readonly bool[] PreviewModes = [false, true];

    [Theory]
    [InlineData("none")]
    [InlineData("failed-save")]
    [InlineData("startup-dismiss")]
    public Task NativePreviewTransitionsKeepApplyFocus(string antecedent) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new UpdateSessionTests.Repository
        {
            State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: antecedent == "startup-dismiss")
        };
        var checker = new UpdateSessionTests.ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "1.3.2"));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new UpdateSessionTests.Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "1.3.1");
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 560, Height = 760, ShowInTaskbar = false };
        var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
        var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
        var apply = (Button)window.FindName("ApplyAppearanceButton");
        var overlay = (TabItem)window.FindName("OverlayWorkspaceTab");
        var settings = (TabItem)window.FindName("SettingsWorkspaceTab");
        var failures = new List<string>();
        var trace = new List<object>();
        string stage = "show";
        window.GotKeyboardFocus += (_, e) => Record("got", e.OldFocus, e.NewFocus);
        window.LostKeyboardFocus += (_, e) => Record("lost", e.OldFocus, e.NewFocus);
        window.Activated += (_, _) => Record("activated");
        window.Deactivated += (_, _) => Record("deactivated");
        try
        {
            window.Show();
            settings.IsSelected = true;
            await Idle();
            var check = (Button)window.FindName("CheckForUpdatesButton");
            if (antecedent == "failed-save")
            {
                await vm.CheckForUpdatesAsync();
                await Idle();
                Assert.True(check.Focus());
                Assert.True(check.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(((Button)window.FindName("OpenUpdateReleasePageButton")).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                await Idle();
                var toggle = (CheckBox)window.FindName("CheckForUpdatesOnStartupCheckBox");
                Assert.True(toggle.IsKeyboardFocused);
                repository.FailSave = true;
                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                await Idle();
                Assert.False(toggle.IsChecked);
                Assert.Contains("could not be saved", vm.UpdatePreferenceStatus, StringComparison.Ordinal);
                Record("failed-save-first-idle");
            }
            if (antecedent == "startup-dismiss")
            {
                Assert.True(check.Focus());
                vm.StartStartupUpdateCheck();
                var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = window.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = window.OwnedWindows.Cast<Window>().SingleOrDefault();
                    try
                    {
                        Assert.NotNull(dialog);
                        ((Button)dialog.FindName("DismissUpdateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        dismissed.TrySetResult();
                    }
                    catch (Exception error) { dismissed.TrySetException(error); }
                    finally { dialog?.Close(); }
                }, DispatcherPriority.ApplicationIdle);
                await dismissed.Task;
                await Idle();
                Assert.Same(check, Keyboard.FocusedElement);
                Record("modal-first-idle");
            }
            overlay.IsSelected = true;
            await Idle();
            var normalParent = preview.Parent;
            var section = (FrameworkElement)window.FindName("NormalPreviewSection");
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            stage = "native-start";
            await preview.StartBrowserAsync(Path.Combine(Path.GetTempPath(), "SoulsTracker-focus-return", Guid.NewGuid().ToString("N")));
            await HostedDesktopPublisherTests.WaitUntil(() => preview.IsReady);
            var browser = Tree(preview).OfType<WebView2>().Single();
            var core = browser.CoreWebView2;
            preview.BringIntoView();
            await Idle();
            await WaitForPaint(browser);
            Assert.True(apply.Focus());
            await Idle();
            ExpectApply("before-leave");
            double threshold = section.TranslatePoint(new Point(0, section.ActualHeight), viewport).Y + scroll.VerticalOffset;
            double sectionTop = section.TranslatePoint(new Point(), viewport).Y + scroll.VerticalOffset;
            scroll.ScrollToVerticalOffset(sectionTop);
            await Idle();
            scroll.ScrollToVerticalOffset(sectionTop + 10);
            await Idle();
            for (int crossing = 0; crossing < 3; crossing++)
            {
                stage = $"crossing-{crossing}-normal";
                scroll.ScrollToVerticalOffset(threshold - 1);
                await Idle();
                Assert.Same(normalParent, preview.Parent);
                ExpectApply("before-leave");
                stage = $"crossing-{crossing}-sticky";
                scroll.ScrollToVerticalOffset(threshold + 1);
                Record("requested-leave");
                await Idle();
                Assert.NotSame(normalParent, preview.Parent);
                ExpectApply("after-leave-first-idle");
                Assert.NotNull(Keyboard.FocusedElement);
                await WaitForPaint(browser);
                ExpectApply("after-paint");
                stage = $"crossing-{crossing}-return";
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 2);
                Record("requested-return");
                await Idle();
                Assert.Same(normalParent, preview.Parent);
                ExpectApply("return-first-idle");
                Assert.NotNull(Keyboard.FocusedElement);
            }
            vm.TotalDeathsAppearanceDraft.Title = "Docked draft";
            vm.TotalDeathsAppearanceDraft.ShadowEnabled = true;
            await Idle();
            var input = Tree(Tree(window).OfType<AppearanceNumberField>().Last()).OfType<TextBox>().First();
            input.Focus(); input.BringIntoView();
            await Idle();
            await WaitForPaint(browser);
            Assert.True(apply.Focus());
            await Idle();
            foreach (int delta in WheelDeltas)
            {
                stage = $"wheel-{delta}";
                ExpectApply("before-wheel");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseWheel", x = 40, y = 40, deltaX = 0, deltaY = delta }));
                await Task.Delay(100);
                await Idle();
                ExpectApply("wheel-first-idle");
            }
            foreach (bool sticky in PreviewModes)
            {
                stage = $"tab-{sticky}";
                scroll.ScrollToVerticalOffset(sticky ? scroll.ScrollableHeight : 0);
                await Idle();
                ExpectApply("before-tab-leave");
                settings.IsSelected = true;
                Record("tab-left");
                await Idle();
                Record("tab-left-first-idle");
                overlay.IsSelected = true;
                Record("tab-return");
                await Idle();
                ExpectApply("tab-return-first-idle");
                Assert.Equal(sticky, !ReferenceEquals(normalParent, preview.Parent));
                Assert.True(apply.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Record("traversal-next");
                Assert.NotNull(Keyboard.FocusedElement);
                Assert.True(((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
                ExpectApply("traversal-previous");
            }
            Assert.Empty(failures);
        }
        finally
        {
            AppearancePreviewLayoutTests.Record("focus-return-" + antecedent, new { failures, trace });
            await vm.StopUpdateChecksAsync();
            window.Close();
        }

        void ExpectApply(string point)
        {
            Record(point);
            if (!ReferenceEquals(apply, Keyboard.FocusedElement)) failures.Add(stage + ":" + point + ":" + Identity(Keyboard.FocusedElement));
        }
        void Record(string point, IInputElement? oldFocus = null, IInputElement? newFocus = null) => trace.Add(new
        {
            stage,
            point,
            focus = Identity(Keyboard.FocusedElement),
            oldFocus = Identity(oldFocus),
            newFocus = Identity(newFocus),
            previewParent = (preview.Parent as FrameworkElement)?.Name,
            scroll.VerticalOffset,
            window.IsActive,
            apply.IsVisible,
            apply.IsEnabled,
            apply.Focusable
        });
    });

    private static string Identity(IInputElement? element) => element is FrameworkElement field ? field.GetType().Name + ":" + field.Name : element?.GetType().Name ?? "null";
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task WaitForPaint(WebView2 browser)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('main').dataset.fit") != "\"painted\"")
            await Task.Delay(1, timeout.Token);
    }
}
