namespace Bibliotaph.Core.Reading;

/// <summary>
/// The reader's Add to binder, in the Windows app and on the web: one button whose menu names the binders, so where the
/// page goes is always a choice in plain sight rather than whichever binder was worked on last.
/// </summary>
public static class AddToBinderMenu
{
    public const string Label = "Add to binder";

    public const string Tooltip = "Add the page in view to a binder";

    /// <summary>How many binders the menu names, most recently worked on first; the rest are behind Another binder….</summary>
    public const int Named = 8;

    public const string Another = "Another binder…";

    public const string New = "New binder…";

    public const string Pages = "Add pages…";

    public enum Kind
    {
        /// <summary>A binder by name: the page goes straight to it.</summary>
        Binder,

        /// <summary>Picks one of every binder first.</summary>
        Another,

        /// <summary>Names a new binder, then adds to it.</summary>
        New,
    }

    public sealed record Item(Kind Kind, long? Id, string Label);

    /// <summary>The menu's items above Add pages…, for <paramref name="binders"/> listed most recently worked on first.</summary>
    public static IReadOnlyList<Item> For(IReadOnlyList<(long Id, string Title)> binders) =>
    [
        .. binders.Take(Named).Select(b => new Item(Kind.Binder, b.Id, b.Title)),
        .. binders.Count > Named ? [new Item(Kind.Another, null, Another)] : Array.Empty<Item>(),
        new(Kind.New, null, New),
    ];
}
