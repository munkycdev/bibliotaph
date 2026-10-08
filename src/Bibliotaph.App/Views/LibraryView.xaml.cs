using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bibliotaph.App.Views;

public partial class LibraryView
{
    IInputElement? _focusBeforeDrawer;

    public LibraryView() => InitializeComponent();

    /// <summary>Keyboard focus goes into the inspector when it opens and back to where it was when it closes.</summary>
    void Drawer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Drawer.IsVisible)
        {
            _focusBeforeDrawer = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => CloseDrawer.Focus());
        }
        else if (_focusBeforeDrawer is UIElement { IsVisible: true } previous)
        {
            previous.Focus();
            _focusBeforeDrawer = null;
        }
    }
}
