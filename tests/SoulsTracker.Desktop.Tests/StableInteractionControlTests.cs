using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class StableInteractionControlTests
{
    [Fact]
    public Task DirectoryCopyFocusPreservesMeasuredGeometry() => WithDirectory(async window =>
    {
        var copy = Copy(window);
        var container = (Border)((Grid)copy.Parent).Parent;
        ((TabControl)window.FindName("WorkspaceTabs")).Focus(); await Idle();
        var before = (copy.DesiredSize, copy.RenderSize, container.DesiredSize, container.RenderSize);
        Assert.True(copy.Focus()); await Idle();
        Assert.True(copy.IsKeyboardFocused);
        Assert.Equal(before, (copy.DesiredSize, copy.RenderSize, container.DesiredSize, container.RenderSize));
    });

    [Fact]
    public Task DirectoryActionsHaveNoReservedFeedbackGap() => WithDirectory(async window =>
    {
        var copy = Copy(window);
        var container = (Border)((Grid)copy.Parent).Parent;
        var panel = (StackPanel)container.Parent;
        var actions = panel.Children.OfType<StackPanel>().First(x => x.Children.OfType<Button>().Any(b => Equals(b.Content, "Change Directory")));
        await Idle();
        double bottom = container.TranslatePoint(new Point(0, container.ActualHeight), panel).Y;
        Assert.Equal(container.Margin.Bottom, actions.TranslatePoint(new Point(), panel).Y - bottom);
    });

    private static Button Copy(MainWindow window) => Tree(window).OfType<Button>().Single(x => x.IsVisible &&
        System.Windows.Automation.AutomationProperties.GetName(x) == "Copy directory path");

    private static Task WithDirectory(Func<MainWindow, Task> check) => HostedConnectionTests.OnDispatcher(async () =>
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string directory = System.IO.Path.Combine(fixture.Root, "chosen");
        CreateSave("wk", directory);
        var repository = new MemoryRepository(GameId.BlackMythWukong);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync(); await Choose(vm, "wk", directory);
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, ShowInTaskbar = false };
        try { window.Show(); await Idle(); await check(window); }
        finally { window.Close(); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
