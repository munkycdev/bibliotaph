namespace Bibliotaph.App.Services;

/// <summary>Clipboard text. An interface so view models can be tested without the clipboard.</summary>
public interface IClipboard
{
    /// <summary>Throws <see cref="System.Runtime.InteropServices.COMException"/> while another app has the clipboard open.</summary>
    void SetText(string text);
}
