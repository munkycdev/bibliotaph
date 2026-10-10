using System.Globalization;

namespace Bibliotaph.Core;

/// <summary>
/// The names of the groups of entries the Library can be limited to (slice 3 plan, choices 3 and 4): favourites,
/// collections and session packs. catalog.db says which entries are in each; index.db holds a copy in
/// <c>entry_scope</c>, keyed by these names, so library queries can join it.
/// </summary>
public static class ScopeKeys
{
    /// <summary>The books marked with a heart.</summary>
    public const string Favorites = "favorite";

    /// <summary>A collection's books and its sub-collections' books (choice 9).</summary>
    public static string Collection(long collectionId) => "collection:" + collectionId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Only the books added to the collection itself: its "Only books added here" switch.</summary>
    public static string CollectionOwn(long collectionId) => "collection-own:" + collectionId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The books in a session pack, whole or by a page range (choice 4).</summary>
    public static string Session(long packId) => "session:" + packId.ToString(CultureInfo.InvariantCulture);
}
