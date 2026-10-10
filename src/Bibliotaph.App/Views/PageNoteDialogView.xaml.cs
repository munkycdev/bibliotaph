using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bibliotaph.App.Views;

/// <summary>A note on pages, over the reader. Keyboard focus goes to the note as it opens, and back where it was when it closes.</summary>
public partial class PageNoteDialogView
{
    IInputElement? _focusBefore;

    public PageNoteDialogView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _focusBefore = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    NoteTextBox.Focus();
                    NoteTextBox.CaretIndex = NoteTextBox.Text.Length;
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
