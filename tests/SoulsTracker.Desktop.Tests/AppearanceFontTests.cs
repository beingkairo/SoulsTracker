using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearanceFontTests
{
    [Fact]
    public Task FontSelectionFiltersWithoutCommittingSearchOrHover() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { ShowInTaskbar = false };
        var type = typeof(MainWindow).Assembly.GetType("SoulsTracker.Desktop.AppearanceFontField");
        Assert.NotNull(type);
        var control = (FrameworkElement)Activator.CreateInstance(type)!;
        var fonts = Enumerable.Range(0, 80).Select(i => $"Font {i:D2}").ToArray();
        type.GetProperty("Fonts")!.SetValue(control, fonts);
        type.GetProperty("SelectedFont")!.SetValue(control, "Font 40");
        window.Content = control;
        try
        {
            window.Show(); await Idle();
            var popup = Tree(control).OfType<Popup>().Single();
            Assert.False(popup.IsOpen);
            var search = Assert.Single(Tree(control).OfType<TextBox>());
            Assert.Equal(TextAlignment.Left, search.TextAlignment);
            Assert.Equal(HorizontalAlignment.Left, search.HorizontalContentAlignment);
            search.Focus();
            search.Text = "fOnT 2"; await Idle();
            Assert.True(popup.IsOpen);
            Assert.Empty(Tree(popup.Child).OfType<TextBox>());
            var list = Tree(popup.Child).OfType<ListBox>().Single();
            Assert.Equal(10, list.Items.Count);
            AppearanceGeometryTests.Capture((FrameworkElement)popup.Child, "font-filtered");
            Assert.Equal("Font 40", type.GetProperty("SelectedFont")!.GetValue(control));
            search.Text = ""; await Idle();
            var scroll = Tree(list).OfType<ScrollViewer>().Single();
            scroll.ScrollToTop(); await Idle();
            double offset = scroll.VerticalOffset;
            var last = Tree(list).OfType<ListBoxItem>().Last();
            for (int sample = 0; sample < 16; sample++)
            {
                typeof(MouseDevice).GetMethod("ChangeMouseOver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(Mouse.PrimaryDevice, [last, Environment.TickCount]);
                Assert.True(last.IsMouseOver);
                await Task.Delay(50); await Idle();
                Assert.Equal(offset, scroll.VerticalOffset);
            }
            Assert.Equal(offset, scroll.VerticalOffset);
            ScrollBar.LineDownCommand.Execute(null, Tree(list).OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical)); await Idle();
            Assert.True(scroll.VerticalOffset > offset);
            offset = scroll.VerticalOffset;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent }); await Idle();
            Assert.True(scroll.VerticalOffset > offset);
            list.Focus();
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.End) { RoutedEvent = Keyboard.KeyDownEvent }); await Idle();
            Assert.Equal("Font 79", list.SelectedItem);
            Assert.True(scroll.VerticalOffset > offset);
            Assert.Equal("Font 40", type.GetProperty("SelectedFont")!.GetValue(control));
            search.Text = "Font 25"; await Idle();
            list.SelectedIndex = 0;
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal("Font 25", type.GetProperty("SelectedFont")!.GetValue(control));
            Tree(control).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal("Font 25", search.Text);
            Assert.Equal(fonts.Length, list.Items.Count);
            search.Text = "no matches"; await Idle();
            search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal("Font 25", type.GetProperty("SelectedFont")!.GetValue(control));
            Assert.Equal("Font 25", search.Text);
        }
        finally { window.Close(); }
    });
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
