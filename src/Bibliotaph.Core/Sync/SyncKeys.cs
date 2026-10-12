using System.Globalization;

namespace Bibliotaph.Core.Sync;

/// <summary>
/// How the library sync file names user work (library sync plan, choice 4). A book with files is named by a content
/// hash, so two PCs that indexed the same file agree on it without ever having talked; everything the user made gets a
/// sync id (a GUID made once, on the PC that made it) and is named by that. Pins are named by what they point at.
/// </summary>
public static class SyncKeys
{
    /// <summary>A book with files: the lowest content hash among its copies.</summary>
    public const string Book = "h:";

    /// <summary>A book owned elsewhere, with no file: its entry's sync id.</summary>
    public const string Elsewhere = "e:";

    /// <summary>A pack of images: the lowest content hash among its images.</summary>
    public const string Pack = "p:";

    public const string Collection = "c:";
    public const string SavedSearch = "s:";
    public const string Binder = "b:";
    public const string Section = "bs:";
    public const string Item = "bi:";
    public const string PageNote = "n:";

    /// <summary>A vocabulary term, by vocabulary and key: "t:system:dnd-5e". Keys never change, so they name terms everywhere.</summary>
    public const string Term = "t:";

    /// <summary>"Not the same book" for two files, by their content hashes, lower first.</summary>
    public const string NotSameBook = "nsb:";

    /// <summary>A pin, by what it points at: "pin:fav", "pin:c:…", "pin:b:…", "pin:s:…". A book's pin is a field of the book.</summary>
    public const string Pin = "pin:";

    public const string FavoritesPin = Pin + "fav";

    public static string ForBook(string contentHash) => Book + contentHash;

    public static string ForPack(string contentHash) => Pack + contentHash;

    public static string For(string prefix, Guid syncId) => prefix + syncId.ToString("D", CultureInfo.InvariantCulture);

    public static string ForTerm(string vocabulary, string key) => Term + vocabulary + ":" + key;

    public static string ForNotSameBook(string firstHash, string secondHash) =>
        string.CompareOrdinal(firstHash, secondHash) <= 0 ? NotSameBook + firstHash + ":" + secondHash : NotSameBook + secondHash + ":" + firstHash;

    /// <summary>A book, a book owned elsewhere or a pack: a card, whose fields are its metadata and the user's marks.</summary>
    public static bool IsCard(string key) => key.StartsWith(Book, StringComparison.Ordinal) || key.StartsWith(Elsewhere, StringComparison.Ordinal)
        || key.StartsWith(Pack, StringComparison.Ordinal);

    /// <summary>
    /// Whether the record exists only while the user keeps it, so its going away on one PC is a deletion to send to the
    /// others. A book with files exists while its files do, which is no user decision, and a term is never deleted:
    /// those are never deleted through sync, only their fields cleared.
    /// </summary>
    public static bool CanBeDeleted(string key) =>
        !key.StartsWith(Book, StringComparison.Ordinal) && !key.StartsWith(Pack, StringComparison.Ordinal) && !key.StartsWith(Term, StringComparison.Ordinal);

    /// <summary>The sync id in a key made by <see cref="For"/>, or null when it holds none.</summary>
    public static Guid? IdOf(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(key.AsSpan(prefix.Length), "D", out var id) ? id : null;

    /// <summary>The content hash in a book or pack key, or null.</summary>
    public static string? HashOf(string key) =>
        key.StartsWith(Book, StringComparison.Ordinal) ? key[Book.Length..] : key.StartsWith(Pack, StringComparison.Ordinal) ? key[Pack.Length..] : null;
}

/// <summary>The names of the fields a sync record carries (library sync plan, choice 5).</summary>
public static class SyncFields
{
    /// <summary>A card's metadata field, by its key: "m.title". The value says what the user decided: values kept and values rejected.</summary>
    public const string MetadataPrefix = "m.";

    public const string Favorite = "fav";
    public const string Note = "note";

    /// <summary>A card's place among the pins, when it is pinned.</summary>
    public const string PinPosition = "pin";

    /// <summary>A card's membership of a collection, by the collection's sync id: "in.3f2…". One field each, so adding a book to two collections on two PCs keeps both.</summary>
    public const string InCollectionPrefix = "in.";

    /// <summary>An existing book owned elsewhere: always true, so the record has a field to outlive a tombstone with.</summary>
    public const string Exists = "x";

    public const string Name = "name";
    public const string Description = "description";
    public const string Parent = "parent";
    public const string Definition = "definition";
    public const string Title = "title";
    public const string Date = "date";
    public const string Notes = "notes";
    public const string AtHand = "atHand";
    public const string System = "system";
    public const string Binder = "binder";
    public const string Section = "section";
    public const string Position = "position";
    public const string Card = "book";
    public const string Pages = "pages";

    /// <summary>A binder item that is a page of a book owned online: the page's address.</summary>
    public const string Link = "link";

    public const string Label = "label";
    public const string Text = "text";
    public const string ShortLabel = "short";
    public const string Aliases = "aliases";
}
