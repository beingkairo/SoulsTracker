using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearanceEditorTests
{
    private static readonly string[] InvalidColors = ["red", "#fff", "#GG0011", "#12345678", ""];
    private static readonly string[] ValidColors = ["#abcdef", "#ABCDEF", "#000000"];
    [Fact]
    public Task SkullOnlyHidesTitleWithoutDiscardingDraft() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator);
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show();
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
            await Idle();
            var title = Tree(window).OfType<TextBox>().Single(x => BindingOperations.GetBinding(x, TextBox.TextProperty)?.Path.Path == "TotalDeathsAppearanceDraft.Title");
            title.Text = "My draft title";
            var selector = Tree(window).OfType<ComboBox>().Single(x => BindingOperations.GetBinding(x, ComboBox.SelectedItemProperty)?.Path.Path == "DraftTitleIconModeChoice");
            selector.SelectedItem = vm.TitleIconModes.Single(x => x.Value == OverlayTitleIconMode.PrefixSkull);
            await Idle();
            var iconColor = Tree(window).OfType<ColorField>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Total Deaths icon color");
            Assert.True(selector.TranslatePoint(new Point(), window).Y < iconColor.TranslatePoint(new Point(), window).Y);
            Assert.True(iconColor.TranslatePoint(new Point(), window).Y < title.TranslatePoint(new Point(), window).Y);
            vm.TotalDeathsAppearanceDraft.IconColor = "bad";
            Assert.Equal("Icon color: use #RRGGBB.", vm.TotalDeathsAppearanceDraft[nameof(OverlayAppearanceDraft.IconColor)]);
            vm.TotalDeathsAppearanceDraft.IconColor = "#FFFFFF";
            selector.SelectedItem = vm.TitleIconModes.Single(x => x.Value == OverlayTitleIconMode.SkullOnly);
            await Idle();
            Assert.False(title.IsVisible);
            Assert.False(((FrameworkElement)title.Parent).IsVisible);
            Assert.Equal("My draft title", vm.TotalDeathsAppearanceDraft.Title);
            foreach (var mode in new[] { OverlayTitleIconMode.PrefixSkull, OverlayTitleIconMode.Off })
            {
                selector.SelectedItem = vm.TitleIconModes.Single(x => x.Value == mode);
                await Idle();
                Assert.True(title.IsVisible);
                Assert.Equal("My draft title", title.Text);
            }
            Assert.Empty(repository.Saves);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("FontSize", 12, 96, "px")]
    [InlineData("TextOpacity", 0, 100, "%")]
    [InlineData("BackgroundOpacity", 0, 100, "%")]
    [InlineData("OutlineWidth", 0, 8, "px")]
    [InlineData("ShadowOffsetX", -20, 20, "px")]
    [InlineData("ShadowOffsetY", -20, 20, "px")]
    [InlineData("ShadowBlur", 0, 20, "px")]
    public Task NumbersSupportBoundedStepsAndInvalidEntry(string property, int minimum, int maximum, string unit) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var draft = vm.TotalDeathsAppearanceDraft;
        draft.BackgroundEnabled = draft.OutlineEnabled = draft.ShadowEnabled = true;
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var input = Assert.Single(Tree(window).OfType<FrameworkElement>(), x => x.GetType().Name == "AppearanceNumberField" &&
                BindingOperations.GetBinding(x, x.GetType().GetField("ValueProperty")!.GetValue(null) as DependencyProperty)?.Path.Path == $"TotalDeathsAppearanceDraft.{property}");
            var text = Tree(input).OfType<TextBox>().Single();
            var buttons = Tree(input).OfType<Button>().ToArray();
            Assert.Contains(Tree(input).OfType<TextBlock>(), x => x.Text == unit);
            Assert.Contains(minimum.ToString(CultureInfo.InvariantCulture), System.Windows.Automation.AutomationProperties.GetHelpText(text));
            Assert.Contains(maximum.ToString(CultureInfo.InvariantCulture), System.Windows.Automation.AutomationProperties.GetHelpText(text));
            var increment = buttons.Single(x => System.Windows.Automation.AutomationProperties.GetName(x).StartsWith("Increase", StringComparison.Ordinal));
            var decrement = buttons.Single(x => System.Windows.Automation.AutomationProperties.GetName(x).StartsWith("Decrease", StringComparison.Ordinal));
            Assert.DoesNotContain(buttons, x => Equals(x.Content, "+") || Equals(x.Content, "-"));
            var up = increment.TransformToAncestor(input).TransformBounds(new Rect(increment.RenderSize));
            var down = decrement.TransformToAncestor(input).TransformBounds(new Rect(decrement.RenderSize));
            Assert.Equal(up.Left, down.Left);
            Assert.True(up.Bottom <= down.Top);
            Assert.True(up.Width >= 24 && up.Height >= 14);
            Assert.True(down.Right <= input.ActualWidth && down.Bottom <= input.ActualHeight);
            Assert.Equal(new Thickness(0), text.BorderThickness);
            foreach (var boundary in new[] { minimum, maximum })
            {
                text.Text = boundary.ToString(CultureInfo.InvariantCulture); await Idle();
                Assert.Equal(text.Text, draft.GetType().GetProperty(property)!.GetValue(draft));
                Assert.False(Validation.GetHasError(text));
                (boundary == maximum ? increment : decrement).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(boundary.ToString(CultureInfo.InvariantCulture), text.Text);
                (boundary == maximum ? decrement : increment).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal((boundary == maximum ? boundary - 1 : boundary + 1).ToString(CultureInfo.InvariantCulture), text.Text);
            }
            foreach (string invalid in new[] { "", "-", "1.5", "abc", (minimum - 1).ToString(CultureInfo.InvariantCulture), (maximum + 1).ToString(CultureInfo.InvariantCulture) })
            {
                text.SelectAll(); text.SelectedText = invalid; await Idle();
                Assert.True(Validation.GetHasError(text), invalid);
                await vm.ApplyOverlayAppearanceAsync(true);
                Assert.Empty(repository.Saves);
                Assert.Equal(invalid, text.Text);
            }
            text.Focus(); text.SelectAll();
            text.RaiseEvent(new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice,
                new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, text, minimum.ToString(CultureInfo.InvariantCulture)))
            { RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent });
            await Idle();
            Assert.Equal(minimum.ToString(CultureInfo.InvariantCulture), text.Text);
            text.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, System.Windows.Input.Key.Up)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            await Idle();
            Assert.Equal((minimum + 1).ToString(CultureInfo.InvariantCulture), text.Text);
            // Exercise WPF's paste pipeline with a synthetic data object, never the OS clipboard.
            var assembly = typeof(TextBox).Assembly;
            var editor = assembly.GetType("System.Windows.Documents.TextEditor")!.GetMethod("_GetTextEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [text]);
            var paste = assembly.GetType("System.Windows.Documents.TextEditorCopyPaste")!.GetMethod("_DoPaste", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            foreach (string pasted in new[] { maximum.ToString(CultureInfo.InvariantCulture), "not a number" })
            {
                text.SelectAll();
                Assert.Equal(true, paste.Invoke(null, [editor, new DataObject(DataFormats.UnicodeText, pasted), false]));
                await Idle();
                Assert.Equal(pasted, text.Text);
                Assert.Equal(pasted == "not a number", Validation.GetHasError(text));
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ColorErrorsAreImmediateAndSpecificWithoutChangingGeometry() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { ShowInTaskbar = false };
        var color = new ColorField();
        window.Content = color;
        try
        {
            window.Show(); await Idle();
            var input = color.Children.OfType<TextBox>().Single();
            var before = color.RenderSize;
            foreach (string invalid in InvalidColors)
            {
                input.Text = invalid; await Idle();
                Assert.True(Validation.GetHasError(input));
                Assert.Contains("#RRGGBB", System.Windows.Automation.AutomationProperties.GetHelpText(input));
                Assert.Equal(before, color.RenderSize);
            }
            foreach (string valid in ValidColors)
            {
                input.Text = valid; await Idle();
                Assert.False(Validation.GetHasError(input));
                Assert.Equal(before, color.RenderSize);
            }
        }
        finally { window.Close(); }
    });

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
