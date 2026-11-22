using Bibliotaph.Presentation.Services;

namespace Bibliotaph.App.Services;

public sealed class WpfClipboard : IClipboard
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
