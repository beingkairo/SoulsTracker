using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearanceGeometryTests
{
    [Theory]
    [InlineData("number")]
    [InlineData("color")]
    [InlineData("font")]
    public Task CompositeStatesRetainAllDescendantGeometry(string kind) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { ShowInTaskbar = false };
        FrameworkElement control = kind switch
        {
            "number" => new AppearanceNumberField { Minimum = -20, Maximum = 20, Value = "2" },
            "color" => new ColorField(),
            _ => new AppearanceFontField { Fonts = ["Arial", "Verdana"], SelectedFont = "Arial" }
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var neutral = new Button { Content = "Neutral" }; panel.Children.Add(neutral); panel.Children.Add(control); window.Content = panel;
        try
        {
            window.Show(); neutral.Focus(); await Idle();
            var baseline = Geometry(control);
            foreach (var child in Tree(control).OfType<Control>().Where(x => x is Button or TextBox).ToArray())
            {
                typeof(MouseDevice).GetMethod("ChangeMouseOver", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Mouse.PrimaryDevice, [child, Environment.TickCount]);
                Assert.True(child.IsMouseOver); window.UpdateLayout(); Assert.Equal(baseline, Geometry(control));
                Assert.True(child.Focus()); await Idle(); Assert.True(child.IsKeyboardFocusWithin);
                Assert.Equal(baseline, Geometry(control));
                if (child is Button button)
                {
                    button.RaiseEvent(new KeyEventArgs(new PressedKeyboard(), PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                    Assert.True(button.IsPressed); window.UpdateLayout(); Assert.Equal(baseline, Geometry(control));
                    button.IsEnabled = false; await Idle(); Assert.False(button.IsPressed); button.IsEnabled = true;
                }
                neutral.Focus(); await Idle();
            }
            if (Tree(control).OfType<TextBox>().FirstOrDefault() is { } input)
            {
                Validation.MarkInvalid(input.GetBindingExpression(TextBox.TextProperty), new ValidationError(new ExceptionValidationRule(), input.GetBindingExpression(TextBox.TextProperty), "Invalid value", null));
                await Idle(); Assert.True(Validation.GetHasError(input));
                Assert.Equal(baseline, Geometry(control));
                Assert.NotNull(System.Windows.Documents.AdornerLayer.GetAdornerLayer(input));
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task AppearanceRendersAndInvalidFieldsDoNotMoveActions(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var draft = vm.TotalDeathsAppearanceDraft;
        draft.BackgroundEnabled = draft.ShadowEnabled = draft.OutlineEnabled = true;
        vm.DraftTitleIconModeChoice = vm.TitleIconModes.Single(x => x.Value == OverlayTitleIconMode.PrefixSkull);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var scroll = (ScrollViewer)window.FindName("OverlayConfigurationScrollViewer");
            var form = (FrameworkElement)scroll.Content;
            Tree(form).OfType<AppearanceFontField>().Single().BringIntoView(); await Idle();
            Capture(window, $"appearance-{width}-{height}-font");
            scroll.ScrollToEnd(); await Idle();
            Capture(window, $"appearance-{width}-{height}-bottom");
            var before = LayoutGeometry(form);
            foreach (var color in Tree(form).OfType<ColorField>())
            {
                var text = color.Children.OfType<TextBox>().Single();
                text.Text = "bad";
            }
            foreach (var number in Tree(form).OfType<AppearanceNumberField>()) number.Children.OfType<TextBox>().Single().Text = "bad";
            await Idle();
            Assert.Equal(before, LayoutGeometry(form));
            Assert.All(Tree(form).OfType<ColorField>(), color => Assert.True(Validation.GetHasError(color.Children.OfType<TextBox>().Single())));
            Assert.All(Tree(form).OfType<AppearanceNumberField>(), number => Assert.True(Validation.GetHasError(number.Children.OfType<TextBox>().Single())));
            ((Button)window.FindName("ApplyAppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Empty(repository.Saves);
            Assert.Contains("Text color", vm.TotalDeathsAppearanceStatus);
            Assert.Contains("Shadow Y", vm.TotalDeathsAppearanceStatus);
            Capture(window, $"appearance-{width}-{height}-invalid");
        }
        finally { window.Close(); }
    });

    internal static void Capture(FrameworkElement element, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("SOULS_APPEARANCE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var content = element is Window window ? (FrameworkElement)window.Content : element;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right), (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        var metrics = Tree(content).OfType<FrameworkElement>().Where(x => x is Control or ColorField or AppearanceNumberField or AppearanceFontField).Select(x => new
        {
            type = x.GetType().Name,
            name = AutomationProperties.GetName(x),
            x.ActualWidth,
            x.ActualHeight,
            position = x.TranslatePoint(new Point(), content).ToString(CultureInfo.InvariantCulture),
            margin = x.Margin.ToString()
        });
        File.WriteAllText(Path.Combine(directory, name + ".json"), System.Text.Json.JsonSerializer.Serialize(metrics));
    }
    private static string[] Geometry(FrameworkElement root) => Tree(root).OfType<FrameworkElement>().Where(x => x is not System.Windows.Documents.Adorner).Select(x =>
        $"{x.GetType().Name}|{x.RenderSize}|{x.DesiredSize}|{x.Margin}|{(x is Control control ? control.Padding.ToString() : "")}").ToArray();
    private static string[] LayoutGeometry(FrameworkElement root) => Tree(root).OfType<FrameworkElement>().Where(x => x is ColorField or AppearanceNumberField or AppearanceFontField).Select(x =>
        $"{x.GetType().Name}|{x.RenderSize}|{x.Margin}|{x.TranslatePoint(new Point(), root)}").ToArray();
    private sealed class PressedKeyboard : KeyboardDevice
    {
        public PressedKeyboard() : base(InputManager.Current) { }
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.Down;
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
