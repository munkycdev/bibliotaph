using AppKit;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The Windows app's colours (its Tokens.Light and Tokens.Dark themes): a warm off-white and a green-black behind the
/// pages, with panels a shade lighter. Each follows light and dark as the window's appearance changes. Bars and the
/// sidebar keep the system's glass over them. Its green is the app's accent colour, in Assets.xcassets, which macOS
/// uses wherever the system's accent is Multicolor.
/// </summary>
static class Palette
{
    /// <summary>Behind every page.</summary>
    public static NSColor Background { get; } = Dynamic("BibliotaphBackground", 0xf9f8f5, 0x1d211f);

    /// <summary>A panel on a page: a settings section, a card.</summary>
    public static NSColor Panel { get; } = Dynamic("BibliotaphPanel", 0xfffefc, 0x242925);

    /// <summary>A soft green behind what is marked without shouting: a suggestion, the field a card is about.</summary>
    public static NSColor Soft { get; } = Dynamic("BibliotaphSoft", 0xe4eadd, 0x303e30);

    /// <summary>Text on the accent: off-white on the deep green, dark green on the sage.</summary>
    public static NSColor AccentText { get; } = Dynamic("BibliotaphAccentText", 0xfcfcf7, 0x1c3023);

    static NSColor Dynamic(string name, int light, int dark) =>
        NSColor.GetColor(name, appearance => Rgb(IsDark(appearance) ? dark : light));

    static bool IsDark(NSAppearance appearance) =>
        appearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();

    static NSColor Rgb(int rgb) => NSColor.FromSrgb(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1);
}

/// <summary>Text, or a row of views, on a rounded fill of the palette's: a field's badge, a suggested value, a chip.</summary>
sealed class Tinted : NSView
{
    NSColor? _fill;

    public Tinted(NSView content, NSColor? fill, nfloat radius, NSEdgeInsets padding)
    {
        _fill = fill;
        WantsLayer = true;
        Layer!.CornerRadius = radius;
        TranslatesAutoresizingMaskIntoConstraints = false;
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(TopAnchor, padding.Top),
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor, -padding.Bottom),
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, padding.Left),
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -padding.Right),
        ]);
    }

    /// <summary>The fill, or none, as when a value stops being only a suggestion.</summary>
    public NSColor? Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            NeedsDisplay = true;
        }
    }

    public override bool WantsUpdateLayer => true;

    // In the appearance showing, so it follows light and dark.
    public override void UpdateLayer() => Layer!.BackgroundColor = (_fill ?? NSColor.Clear).CGColor;
}
