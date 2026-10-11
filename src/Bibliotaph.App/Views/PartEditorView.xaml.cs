using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>
/// Marking pages as an adventure, or moving one, over the reader or the Library. Keyboard focus goes to the title (or
/// the first page, for one being moved) as it opens, and back where it was when it closes.
/// </summary>
public partial class PartEditorView
{
    IInputElement? _focusBefore;

    public PartEditorView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _focusBefore = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    TextBox box = DataContext is PartEditorViewModel { ShowsTitle: true } ? PartTitleBox : PartFromBox;
                    box.Focus();
                    box.SelectAll();
                });
            }
            else if (_focusBefore is UIElement { IsVisible: true } previous)
            {
                previous.Focus();
                _focusBefore = null;
            }
        };
    }
}
