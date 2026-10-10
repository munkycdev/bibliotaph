using System.Windows;

namespace Bibliotaph.App.Views;

/// <summary>Asks for a PDF's password, whether to make the book searchable with it, and whether to remember it.</summary>
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
        // A remembered password lets indexing read the book at any time, so it is searchable either way.
        Remember.Checked += (_, _) => (Searchable.IsChecked, Searchable.IsEnabled) = (true, false);
        Remember.Unchecked += (_, _) => Searchable.IsEnabled = true;
    }

    public string EnteredPassword => Password.Password;

    public bool RememberPassword => Remember.IsChecked == true;

    public bool MakeSearchable => Searchable.IsChecked == true || RememberPassword;

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Password.Password.Length == 0) return;
        DialogResult = true;
    }
}
