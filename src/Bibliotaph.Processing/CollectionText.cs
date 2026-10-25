using System.Globalization;
using Bibliotaph.Catalog;

namespace Bibliotaph.Processing;

/// <summary>
/// How collections read wherever they show, in the Windows app and on the web: their names as paths ("Campaign ›
/// Maps"), their tree for a picker, a book menu's Add to collection items, a card's count, and what each change says.
/// </summary>
public static class CollectionText
{
    /// <summary>How many recent collections a book's menu offers before New collection… and Choose….</summary>
    public const int RecentCount = 4;

    public const string NewItem = "Add to a new collection…";

    public const string ChooseItem = "Add to another collection…";

    /// <summary>A collection's subtitle on its page when it has no description.</summary>
    public const string Subtitle = "A collection. The books in its sub-collections show here too.";

    public const string EmptyTitle = "Nothing in this collection yet.";

    public const string EmptyMessage = "Add books from the Library: right-click a book, or choose Select and tick several, then add them to this collection.";

    public const string TopLevel = "Collections (the top level)";

    public static string AddItem(string name) => $"Add to {name}";

    /// <summary>"Campaign › Maps": the collection's name after those of the collections it sits in.</summary>
    public static string PathOf(IReadOnlyDictionary<long, CollectionInfo> byId, long collectionId)
    {
        var names = new List<string>();
        for (long? at = collectionId; at is { } id && byId.TryGetValue(id, out var c) && names.Count <= byId.Count; at = c.ParentId) names.Add(c.Name);
        names.Reverse();
        return string.Join(" › ", names);
    }

    /// <summary>The collections inside <paramref name="parentId"/>, or at the top, by name.</summary>
    public static IReadOnlyList<CollectionInfo> ChildrenOf(IEnumerable<CollectionInfo> all, long? parentId) =>
        [.. all.Where(c => c.ParentId == parentId).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Every collection, depth first, for a picker: each after the one it sits in, with how deep it is.
    /// <paramref name="excluding"/> leaves out a collection and everything under it, for Move to….
    /// </summary>
    public static IReadOnlyList<(CollectionInfo Collection, int Depth)> Tree(IReadOnlyCollection<CollectionInfo> all, long? excluding = null)
    {
        var result = new List<(CollectionInfo, int)>();
        void Add(long? parent, int depth)
        {
            foreach (var child in ChildrenOf(all, parent).Where(c => c.Id != excluding))
            {
                result.Add((child, depth));
                if (depth < 32) Add(child.Id, depth + 1);
            }
        }
        Add(null, 0);
        return result;
    }

    /// <summary>The collections a book menu offers by name: those used last, in path order.</summary>
    public static IReadOnlyList<CollectionInfo> Recent(IReadOnlyCollection<CollectionInfo> all, Func<long, string> pathOf) =>
        [.. all.OrderByDescending(c => c.UsedUtc).Take(RecentCount).OrderBy(c => pathOf(c.Id), StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Whether a book menu needs Add to another collection…: there are more than the recent ones.</summary>
    public static bool OffersChoose(int collections) => collections > RecentCount;

    /// <summary>"12 books · 2 collections", under a collection's card.</summary>
    public static string Count(long books, int children)
    {
        var parts = new List<string> { books == 0 ? "No books yet" : books == 1 ? "1 book" : $"{books.ToString("N0", CultureInfo.CurrentCulture)} books" };
        if (children > 0) parts.Add(children == 1 ? "1 collection" : $"{children.ToString("N0", CultureInfo.CurrentCulture)} collections");
        return string.Join(" · ", parts);
    }

    /// <summary>A card's tip: its name, and its description when it has one.</summary>
    public static string Tip(CollectionInfo collection) => collection.Description is { } description ? $"{collection.Name}: {description}" : collection.Name;

    /// <summary>The picker's heading for New collection: at the top, or inside another.</summary>
    public static string NewHeading(string? parentName) => parentName is null ? "New collection" : $"New collection in {parentName}";

    public static string Added(string what, int asked, int added, string name) =>
        added == 0 ? $"{Capital(what)} {(asked == 1 ? "is" : "are")} in {name} already." : $"Added {what} to {name}.";

    public static string Removed(string what, int asked, int removed, string name) =>
        removed == 0 ? $"{Capital(what)} {(asked == 1 ? "was" : "were")} only in {name} through a collection inside it." : $"Removed {what} from {name}.";

    public static string Renamed(string from, string to) => $"Renamed {from} to {to}.";

    public static string Moved(string name, string? to) => $"Moved {name} to {to ?? "the top level"}.";

    public static string Deleted(CollectionDeletion deleted) =>
        $"Deleted {deleted.Name}. Its books are still in your library{(deleted.Children.Count > 0 ? ", and its collections moved up a level" : "")}.";

    static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
