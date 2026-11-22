using AppKit;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The Windows app's colours (its Tokens.Light and Tokens.Dark themes): a warm off-white and a green-black behind the
/// pages, with panels a shade lighter. Each follows light and dark as the window's appearance changes. Bars and the
/// sidebar keep the system's glass over them.
/// </summary>
static class Palette
{
    /// <summary>Behind every page.</summary>
    public static NSColor Background { get; } = Dynamic("BibliotaphBackground", 0xf9f8f5, 0x1d211f);

    /// <summary>A panel on a page: a settings section, a card.</summary>
    public static NSColor Panel { get; } = Dynamic("BibliotaphPanel", 0xfffefc, 0x242925);

    /// <summary>The app's green, where its own drawing marks something, when the system's accent isn't the app's.</summary>
    public static NSColor Accent { get; } = Dynamic("BibliotaphAccent", 0x365847, 0xaec9a9);

    static NSColor Dynamic(string name, int light, int dark) =>
        NSColor.GetColor(name, appearance => Rgb(IsDark(appearance) ? dark : light));

    static bool IsDark(NSAppearance appearance) =>
        appearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();

    static NSColor Rgb(int rgb) => NSColor.FromSrgb(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1);
}
