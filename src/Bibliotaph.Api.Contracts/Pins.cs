namespace Bibliotaph.Api.Contracts;

/// <summary>What a pin points at, as the API names it.</summary>
public static class PinKinds
{
    public const string Favorites = "favorites";
    public const string Collection = "collection";
    public const string Binder = "binder";
    public const string Search = "search";
    public const string Book = "book";
}

/// <summary>A pin: its kind, from <see cref="PinKinds"/>, and which one; Favorites has no id.</summary>
public sealed record PinRef(string Kind, long? Id);

/// <summary>
/// A pin as the sidebar and Home show it, in the sidebar's order: its name, the page it opens and its icon. A book
/// whose file can't be opened right now is dimmed, and <see cref="Tip"/> says why.
/// </summary>
public sealed record PinItem(string Kind, long? Id, string Label, string Href, string Icon, bool Dimmed, string Tip);

/// <summary>The sidebar's Pinned group, in order, and what it says while it has none.</summary>
public sealed record PinList(IReadOnlyList<PinItem> Items, string Hint);

/// <summary>Moves a pin to <see cref="Index"/> in the sidebar, counted once it has been taken out.</summary>
public sealed record PinMove(string Kind, long? Id, int Index);
