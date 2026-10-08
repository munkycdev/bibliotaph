using System.Windows;
using System.Windows.Input;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;
        CommandBindings.Add(new CommandBinding(ShellCommands.FocusSearch, (_, _) => FocusSearch()));
    }

    void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    // The mouse back button. MouseBinding has no gesture for the X buttons, so it is handled here.
    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton != MouseButton.XButton1 || DataContext is not ShellViewModel shell) return;
        if (shell.GoBackCommand.CanExecute(null)) shell.GoBackCommand.Execute(null);
        e.Handled = true;
    }

    // View-only behaviour: Esc leaves the search box. Searching itself arrives in slice 1.
    void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Search.Clear();
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, this);
        e.Handled = true;
    }
}
