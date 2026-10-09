using System.Runtime.InteropServices;
using System.Windows.Interop;
using Bibliotaph.App.Services;
using Bibliotaph.App.Views;

namespace Bibliotaph.App;

// "About Bibliotaph…" in the window's system menu (right-click the title bar, or Alt+Space). There is no menu bar
// to hang a Help menu on, and the system menu is where Windows apps without one put About.
public partial class MainWindow
{
    const int WmSysCommand = 0x0112;
    const int AboutCommandId = 0x0010;  // WM_SYSCOMMAND ids must be below 0xF000 with the low four bits clear
    const uint MfString = 0x0000;
    const uint MfSeparator = 0x0800;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var menu = GetSystemMenu(handle, false);
        if (menu != IntPtr.Zero)
        {
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, AboutCommandId, "&About Bibliotaph…");
        }
        HwndSource.FromHwnd(handle)?.AddHook(OnSystemCommand);
    }

    IntPtr OnSystemCommand(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmSysCommand || (wParam.ToInt64() & 0xFFF0) != AboutCommandId) return IntPtr.Zero;
        handled = true;
        // Leave the menu's own message loop before opening a modal window.
        Dispatcher.BeginInvoke(ShowAbout);
        return IntPtr.Zero;
    }

    /// <summary>Opens the About popup over this window, from the system menu or Settings.</summary>
    void ShowAbout()
    {
        var dialog = new AboutDialog(AboutInfo.ForThisApp()) { Owner = this };
        _theme.Track(dialog);
        dialog.ShowDialog();
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
}
