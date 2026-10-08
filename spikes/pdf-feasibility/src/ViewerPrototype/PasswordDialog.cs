using System.Windows;
using System.Windows.Controls;

namespace Bibliotaph.Spike.Viewer;

/// <summary>Asks for a document's open password. The value lives only in memory for this session.</summary>
sealed class PasswordDialog : Window
{
    readonly PasswordBox _box = new() { Margin = new Thickness(0, 8, 0, 12), MinWidth = 260 };

    public PasswordDialog(string fileName, bool retry)
    {
        Title = "Password required";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "Open", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = retry ? $"That password did not open {fileName}. Try again." : $"{fileName} needs a password to open." });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }

    public string Password => _box.Password;
}
