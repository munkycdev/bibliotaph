using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Bibliotaph.App.Services;

/// <summary>
/// Colours the standard Windows title bar through DWM window attributes. Dark mode works on Windows 10 20H1
/// and later; caption, text and border colours need Windows 11. Older systems ignore what they don't support.
/// </summary>
static class DwmTitleBar
{
    const int UseImmersiveDarkMode = 20;
    const int BorderColor = 34;
    const int CaptionColor = 35;
    const int TextColor = 36;

    public static void Apply(IntPtr hwnd, bool dark, Color caption, Color text, Color border)
    {
        var darkValue = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref darkValue, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        var captionValue = ColorRef(caption);
        var textValue = ColorRef(text);
        var borderValue = ColorRef(border);
        _ = DwmSetWindowAttribute(hwnd, CaptionColor, ref captionValue, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, TextColor, ref textValue, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, BorderColor, ref borderValue, sizeof(int));
    }

    /// <summary>Gives the title bar back to Windows, as under a contrast theme.</summary>
    public static void Reset(IntPtr hwnd)
    {
        var light = 0;
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref light, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        // DWMWA_COLOR_DEFAULT
        var standard = unchecked((int)0xFFFFFFFF);
        _ = DwmSetWindowAttribute(hwnd, CaptionColor, ref standard, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, TextColor, ref standard, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, BorderColor, ref standard, sizeof(int));
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
