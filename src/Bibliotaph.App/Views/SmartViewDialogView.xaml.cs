using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bibliotaph.App.Views;

/// <summary>Naming a Smart View, over the Library. Keyboard focus goes to the name as it opens, and back where it was when it closes.</summary>
public partial class SmartViewDialogView
{
    IInputElement? _focusBefore;

    public SmartViewDialogView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _focusBefore = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    ViewNameBox.Focus();
                    ViewNameBox.SelectAll();
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
