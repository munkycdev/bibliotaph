using System.Windows;
using System.Windows.Input;
using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App;

public partial class MainWindow : Window
{
    readonly AboutBox _about;

    public MainWindow(ShellViewModel shell, AboutBox about)
    {
        InitializeComponent();
        DataContext = shell;
        _about = about;
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

    // Enter searches at once; Esc clears the search and leaves the box.
    void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (e.Key == Key.Enter)
        {
            shell.SubmitSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            shell.ClearSearchCommand.Execute(null);
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
            e.Handled = true;
        }
    }
}
