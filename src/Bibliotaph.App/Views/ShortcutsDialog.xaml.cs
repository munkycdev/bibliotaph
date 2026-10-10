using System.Windows;

namespace Bibliotaph.App.Views;

/// <summary>The Keyboard shortcuts popup (Ctrl+/, or the link under the Settings sections): every shortcut, by where it works.</summary>
public partial class ShortcutsDialog
{
    public ShortcutsDialog()
    {
        InitializeComponent();
        // At 200% a small screen has little height, so the popup never opens taller than the screen.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.9);
        Loaded += (_, _) => ShortcutList.Focus();
    }
}
