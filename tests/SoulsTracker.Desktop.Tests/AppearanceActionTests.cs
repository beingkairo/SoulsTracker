using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearanceActionTests
{
    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task DockedPreviewRetainsCompleteFirstFocusValidation(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        var publisher = new CountingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        int publications = publisher.Count;
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            ((ScrollViewer)window.FindName("OverlayConfigurationScrollViewer")).ScrollToEnd(); await Idle();
            var preview = (AppearancePreview)window.FindName("LocalAppearancePreview");
            Assert.Equal(height == 400 ? 72 : 96, preview.RowDefinitions[0].ActualHeight);
            vm.TotalDeathsAppearanceDraft.FontSize = "bad";
            vm.TotalDeathsAppearanceDraft.TextColor = "bad";
            vm.TotalDeathsAppearanceDraft.ShadowBlur = "bad";
            ((Button)window.FindName("ApplyAppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var status = (TextBlock)window.FindName("AppearanceApplyStatus");
            Assert.True(status.Focus()); await Idle();
            var tooltip = (ToolTip)status.ToolTip;
            Assert.True(tooltip.IsOpen);
            var detail = (TextBlock)tooltip.Content;
            Assert.Equal(vm.TotalDeathsAppearanceStatus, detail.Text);
            Assert.Contains("Font size", detail.Text); Assert.Contains("Text color", detail.Text); Assert.Contains("Shadow blur", detail.Text);
            Assert.True(detail.ActualHeight >= detail.DesiredSize.Height);
            AppearanceGeometryTests.Capture(tooltip, $"docked-validation-{width}-{height}");
            Assert.Empty(repository.Saves); Assert.Equal(publications, publisher.Count);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task ValidationDetailOpensOnFirstKeyboardFocusWithoutHover(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        var publisher = new CountingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var initialState = repository.State;
        int initialPublications = publisher.Count;
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate();
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var apply = (Button)window.FindName("ApplyAppearanceButton");
            var status = (TextBlock)window.FindName("AppearanceApplyStatus");
            var tooltip = (ToolTip)status.ToolTip;
            var detail = (TextBlock)tooltip.Content;
            var actions = (FrameworkElement)window.FindName("AppearanceActions");
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            apply.Focus(); await Idle();
            var actionBounds = actions.TransformToAncestor(window).TransformBounds(new Rect(actions.RenderSize));
            var scrollBounds = scroll.TransformToAncestor(window).TransformBounds(new Rect(scroll.RenderSize));
            Assert.Null(tooltip.PlacementTarget);
            Assert.False(tooltip.IsOpen);
            vm.TotalDeathsAppearanceDraft.FontSize = "bad";
            vm.TotalDeathsAppearanceDraft.TextColor = "bad";
            vm.TotalDeathsAppearanceDraft.ShadowBlur = "bad";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            string firstDetail = Assert.IsType<string>(vm.TotalDeathsAppearanceStatus);
            Assert.Contains("Font size", firstDetail);
            Assert.Contains("Text color", firstDetail);
            Assert.Contains("Shadow blur", firstDetail);
            Assert.NotEqual(firstDetail, status.Text);
            Assert.Equal(firstDetail, status.Tag);
            Assert.True(status.Focus()); await Idle();
            Assert.True(status.IsKeyboardFocused);
            Assert.True(tooltip.IsOpen);
            Assert.Equal(firstDetail, detail.Text);
            Assert.Same(status, tooltip.PlacementTarget);
            Assert.Equal(firstDetail, System.Windows.Automation.AutomationProperties.GetHelpText(status));
            Assert.True(detail.ActualWidth > 0 && detail.ActualWidth <= 400);
            Assert.True(detail.ActualHeight >= detail.DesiredSize.Height);
            AppearanceGeometryTests.Capture(tooltip, $"validation-detail-{width}-{height}");
            Assert.Equal(actionBounds, actions.TransformToAncestor(window).TransformBounds(new Rect(actions.RenderSize)));
            Assert.Equal(scrollBounds, scroll.TransformToAncestor(window).TransformBounds(new Rect(scroll.RenderSize)));
            apply.Focus(); await Idle();
            Assert.False(tooltip.IsOpen);
            vm.TotalDeathsAppearanceDraft.FontSize = "24";
            vm.TotalDeathsAppearanceDraft.TextColor = "#123456";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            string updatedDetail = Assert.IsType<string>(vm.TotalDeathsAppearanceStatus);
            Assert.Contains("Shadow blur", updatedDetail);
            Assert.DoesNotContain("Font size", updatedDetail);
            Assert.DoesNotContain("Text color", updatedDetail);
            status.Focus(); await Idle();
            Assert.True(status.IsKeyboardFocused);
            Assert.True(tooltip.IsOpen);
            Assert.Equal(updatedDetail, detail.Text);
            Assert.Equal(updatedDetail, System.Windows.Automation.AutomationProperties.GetHelpText(status));
            Assert.Equal(actionBounds, actions.TransformToAncestor(window).TransformBounds(new Rect(actions.RenderSize)));
            Assert.Equal(scrollBounds, scroll.TransformToAncestor(window).TransformBounds(new Rect(scroll.RenderSize)));
            apply.Focus(); await Idle();
            Assert.False(tooltip.IsOpen);
            Assert.Empty(repository.Saves);
            Assert.Equal(initialState, repository.State);
            Assert.Equal(initialPublications, publisher.Count);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task ActionsStayFixedAndApplyReportsRealSuccess(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var apply = Assert.Single(Tree(window).OfType<Button>(), x => Equals(x.Content, "Apply to live overlay"));
            var reset = (Button)window.FindName("ResetSelectedOverlayAppearanceButton");
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var position = apply.TranslatePoint(new Point(), window);
            Assert.Equal(apply.ActualHeight, ((FrameworkElement)window.FindName("AppearanceActions")).ActualHeight);
            Assert.True(position.Y + apply.ActualHeight < window.ActualHeight);
            Assert.DoesNotContain(apply, Tree(scroll));
            Assert.DoesNotContain(reset, Tree(scroll));
            Assert.NotEqual(apply.Background, reset.Background);
            scroll.ScrollToEnd(); await Idle();
            Assert.Equal(position, apply.TranslatePoint(new Point(), window));
            var viewport = Tree(scroll).OfType<ScrollContentPresenter>().First();
            Assert.True(viewport.TranslatePoint(new Point(0, viewport.ActualHeight), window).Y <= position.Y);
            vm.TotalDeathsAppearanceDraft.TextColor = "bad";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Empty(repository.Saves);
            var status = Assert.Single(Tree(window).OfType<TextBlock>(), x => System.Windows.Automation.AutomationProperties.GetName(x) == "Total Deaths appearance apply status");
            Assert.Contains("Text color", status.Text);
            vm.TotalDeathsAppearanceDraft.TextColor = "#123456";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsBusy && vm.TotalDeathsAppearanceStatus == "Total Deaths appearance applied.");
            await Idle();
            Assert.Equal("#123456", repository.State.OverlayConfiguration.TotalDeaths.Appearance.TextColor);
            var color = ((SolidColorBrush)status.Foreground).Color;
            Assert.True(color.G > color.R && color.G > color.B);
            AppearanceGeometryTests.Capture(window, $"apply-success-{width}-{height}");
            Assert.Equal(position, apply.TranslatePoint(new Point(), window));
            repository.FailSave = true;
            vm.TotalDeathsAppearanceDraft.TextColor = "#654321";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsBusy && vm.TotalDeathsAppearanceStatus == "Total Deaths appearance could not be applied.");
            await Idle();
            Assert.False(vm.IsAppearanceApplySuccessful);
            color = ((SolidColorBrush)status.Foreground).Color;
            Assert.True(color.R > color.G);
            Assert.Equal("#123456", repository.State.OverlayConfiguration.TotalDeaths.Appearance.TextColor);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("reset")]
    [InlineData("escape")]
    [InlineData("close")]
    public Task ResetRequiresExplicitModalConfirmation(string action) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        vm.TotalDeathsAppearanceDraft.Title = "Keep my draft";
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            bool inspected = false;
            var inspect = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            {
                var dialog = window.OwnedWindows.Cast<Window>().Single();
                try
                {
                    Assert.Equal("Reset overlay appearance to defaults?", dialog.Title);
                    Assert.Contains(Tree(dialog).OfType<TextBlock>(), x => x.Text == "This will replace your current appearance settings.");
                    var cancel = Tree(dialog).OfType<Button>().Single(x => Equals(x.Content, "Cancel"));
                    Assert.False(cancel.IsDefault);
                    Assert.True(cancel.IsCancel);
                    Assert.False(cancel.IsKeyboardFocused);
                    Assert.True(((UIElement)dialog.Content).IsKeyboardFocused);
                    Assert.DoesNotContain(Tree(dialog).OfType<Button>(), x => x.IsKeyboardFocused || x.IsDefault);
                    ((UIElement)dialog.Content).RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    Assert.True(dialog.IsVisible);
                    ((UIElement)dialog.Content).MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
                    Assert.True(cancel.IsKeyboardFocused);
                    foreach (var button in Tree(dialog).OfType<Button>())
                    {
                        var bounds = button.TransformToAncestor(dialog).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Left >= 0 && bounds.Right <= dialog.ActualWidth);
                        Assert.True(bounds.Top >= 0 && bounds.Bottom <= dialog.ActualHeight);
                    }
                    AppearanceGeometryTests.Capture(dialog, $"reset-{action}");
                    Assert.Empty(repository.Saves);
                    ((Button)window.FindName("ResetSelectedOverlayAppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Single(window.OwnedWindows.Cast<Window>());
                    inspected = true;
                    if (action == "close") dialog.Close();
                    else if (action == "escape") System.Windows.Input.AccessKeyManager.ProcessKey(null, "\u001b", false);
                    else Tree(dialog).OfType<Button>().Single(x => Equals(x.Content, action == "reset" ? "Reset" : "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(dialog.IsVisible);
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            ((Button)window.FindName("ResetSelectedOverlayAppearanceButton")).Focus();
            ((Button)window.FindName("ResetSelectedOverlayAppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await inspect; Assert.True(inspected);
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsBusy); await Idle();
            Assert.False(((Button)window.FindName("ResetSelectedOverlayAppearanceButton")).IsKeyboardFocused);
            Assert.Equal(action == "reset" ? OverlayAppearance.Default.Title : "Keep my draft", vm.TotalDeathsAppearanceDraft.Title);
            if (action != "reset") Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });
    [Fact]
    public async Task DeliveryFailureNeverReportsSuccessfulApply()
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new FailedPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        vm.TotalDeathsAppearanceDraft.Title = "Synthetic change";
        await vm.ApplyOverlayAppearanceAsync(true);
        Assert.False(vm.IsAppearanceApplySuccessful);
        Assert.Equal("Total Deaths appearance could not be applied.", vm.TotalDeathsAppearanceStatus);
        Assert.Equal("Synthetic change", repository.State.OverlayConfiguration.TotalDeaths.Appearance.Title);
    }
    private sealed class FailedPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic delivery failure.");
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private sealed class CountingPublisher : ITrackerStateChangePublisher
    {
        public int Count { get; private set; }
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
