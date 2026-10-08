using System.Windows;

namespace Bibliotaph.App.Views;

/// <summary>Asks for a PDF's password, and whether to remember it.</summary>
public partial class PasswordDialog
{
    public PasswordDialog(string title, bool retry)
    {
        InitializeComponent();
        Heading.Text = $"“{title}” needs a password";
        Message.Text = retry
            ? "That password didn't open it. Check it and try again."
            : "Bibliotaph uses it only to open the book. It's never stored in Bibliotaph's own files.";
        Loaded += (_, _) => Password.Focus();
    }

    public string EnteredPassword => Password.Password;

    public bool RememberPassword => Remember.IsChecked == true;

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Password.Password.Length == 0) return;
        DialogResult = true;
    }
}
