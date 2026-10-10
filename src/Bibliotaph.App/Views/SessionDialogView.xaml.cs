using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bibliotaph.App.Views;

/// <summary>
/// Naming a session pack or a section, picking a pack, or Add pages…, over a page (slice 3c). Keyboard focus goes into
/// it as it opens, on its first box, and back to where it was when it closes.
/// </summary>
public partial class SessionDialogView
{
    IInputElement? _focusBefore;

    public SessionDialogView()
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
        if (TitleBox.IsVisible)
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        }
        else if (FromBox.IsVisible)
        {
            FromBox.Focus();
            FromBox.SelectAll();
        }
        else FilterBox.Focus();
    }
}
