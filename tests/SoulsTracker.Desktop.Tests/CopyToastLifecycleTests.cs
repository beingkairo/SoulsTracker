using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class CopyToastLifecycleTests
{
    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public Task LatestSuccessOwnsExpiryAndErrorsDoNotExpire(string game) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        CreateSave(game, fixture.Root);
        var repository = new MemoryRepository(Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync(); await Choose(vm, game, fixture.Root);
        bool fail = false;
        var timers = new List<Expiry>();
        var copies = new List<string>();
        var window = new MainWindow((_, _) => null, path =>
        {
            copies.Add(path);
            if (fail) throw new IOException("Synthetic detail must remain private");
        }, (delay, callback) =>
        {
            var expiry = new Expiry(delay, callback); timers.Add(expiry);
            return () => expiry.Cancelled = true;
        }) { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            Button copy = Tree(window).OfType<Button>().Single(x => x.IsVisible && AutomationProperties.GetName(x) == "Copy directory path");
            var feedback = (TextBlock)window.FindName("DirectoryCopyStatus");
            var overlay = (Border)window.FindName("CopyFeedbackOverlay");
            void Copy() => copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TabControl)window.FindName("WorkspaceTabs")).Focus();
            var focus = Keyboard.FocusedElement;
            Copy(); Copy();
            Assert.Same(focus, Keyboard.FocusedElement);
            Assert.Equal(2, timers.Count);
            Assert.All(timers, x => Assert.Equal(TimeSpan.FromSeconds(5), x.Delay));
            Assert.True(timers[0].Cancelled);
            Assert.False(timers[1].Cancelled);
            timers[0].Callback();
            Assert.Equal("Directory path copied", feedback.Text);
            fail = true; Copy();
            Assert.True(timers[1].Cancelled);
            Assert.Equal(2, timers.Count);
            timers[1].Callback();
            Assert.Equal("The directory path could not be copied. Try again.", feedback.Text);
            Assert.False(((Border)window.FindName("HostedCopyFeedbackOverlay")).IsVisible);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            fail = false; Copy();
            Assert.Equal(3, timers.Count);
            timers[2].Callback();
            Assert.Empty(feedback.Text);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Copy();
            window.DataContext = null;
            Assert.True(timers[3].Cancelled);
            window.DataContext = vm;
            timers[3].Callback();
            Assert.Empty(feedback.Text);
            Copy();
            window.Close();
            Assert.True(timers[4].Cancelled);
            timers[4].Callback();
            Assert.Empty(feedback.Text);
            Assert.All(copies, path => Assert.Equal(fixture.Root, path));
            Assert.Equal(6, copies.Count);
        }
        finally { window.Close(); }
    });

    private sealed class Expiry(TimeSpan delay, Action callback)
    {
        internal TimeSpan Delay { get; } = delay;
        internal Action Callback { get; } = callback;
        internal bool Cancelled { get; set; }
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
