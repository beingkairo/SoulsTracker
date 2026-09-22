using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace SoulsTracker.Desktop;

public sealed class AppearanceResetDialog : Window
{
    public AppearanceResetDialog()
    {
        Title = "Reset overlay appearance to defaults?";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 29, 35));
        var content = new StackPanel { Margin = new Thickness(20), Focusable = true };
        System.Windows.Automation.AutomationProperties.SetName(content, Title);
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(content, System.Windows.Input.KeyboardNavigationMode.Cycle);
        content.Children.Add(new TextBlock { Text = "This will replace your current appearance settings.", TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var reset = new Button { Content = "Reset", Margin = new Thickness(10, 0, 0, 0) };
        reset.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(reset); content.Children.Add(actions);
        Content = content;
        ContentRendered += (_, _) => content.Focus();
    }
}
