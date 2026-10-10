using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bibliotaph.App.Views;

/// <summary>
/// Naming or picking a collection, over a page (slice 3b). Keyboard focus goes into it as it opens, on the name or the
/// picker's filter box, and back to where it was when it closes.
/// </summary>
public partial class CollectionDialogView
{
    IInputElement? _focusBefore;

    public CollectionDialogView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _focusBefore = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusFirst);
            }
            else if (_focusBefore is UIElement { IsVisible: true } previous)
            {
                previous.Focus();
                _focusBefore = null;
            }
        };
    }

    void FocusFirst()
    {
        if (NameBox.IsVisible)
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }
        else FilterBox.Focus();
    }
}
