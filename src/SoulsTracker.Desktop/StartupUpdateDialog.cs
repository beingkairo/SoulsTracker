using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace SoulsTracker.Desktop;

public sealed class StartupUpdateDialog : Window
{
    public StartupUpdateDialog(string message, Func<string> openProductPage)
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = "Update available";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 29, 35));
        var content = new StackPanel { Margin = new Thickness(20), Focusable = true };
        AutomationProperties.SetName(content, Title);
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        RegisterName("UpdateNoticeBody", body);
        content.Children.Add(body);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var open = new Button { Content = "Open release page" };
        var dismiss = new Button { Content = "Dismiss", IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        AutomationProperties.SetName(error, "Open release page error");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        open.Click += (_, _) =>
        {
            error.Text = openProductPage();
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        dismiss.Click += (_, _) => DialogResult = false;
        RegisterName("OpenUpdatePageButton", open);
        RegisterName("DismissUpdateButton", dismiss);
        RegisterName("UpdatePageActionError", error);
        actions.Children.Add(open);
        actions.Children.Add(dismiss);
        content.Children.Add(actions);
        content.Children.Add(error);
        Content = content;
        ContentRendered += (_, _) => content.Focus();
    }
}
