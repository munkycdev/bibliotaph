namespace Bibliotaph.Api.Contracts;

/// <summary>A collection: where it sits, its name as a path ("Campaign › Maps"), and whether it is pinned.</summary>
public sealed record CollectionNode(long Id, long? ParentId, string Name, string? Description, string Path, bool Pinned);

/// <summary>
/// An Add to collection item, in a book menu's order: <see cref="Kind"/> is "collection" for one of the recent ones,
/// "new" for Add to a new collection…, and "choose" for Add to another collection….
/// </summary>
public sealed record CollectionMenuItem(string Kind, long? Id, string Label, string? Path);

/// <summary>
/// Every collection in tree order, each with how deep it sits, for the pickers; and a book menu's Add to collection
/// items.
/// </summary>
public sealed record CollectionList(IReadOnlyList<CollectionNode> Tree, IReadOnlyList<int> Depths, IReadOnlyList<CollectionMenuItem> Menu);

/// <summary>A collection's card: the covers of the two books added last, its name and "12 books · 2 collections".</summary>
public sealed record CollectionCard(long Id, string Name, string Tip, bool Pinned, string Count, IReadOnlyList<BookCard> Covers);

/// <summary>A collection's page in the Library: its name and subtitle, its path for the scope chip, and its sub-collections.</summary>
public sealed record CollectionPage(CollectionNode Collection, string Subtitle, IReadOnlyList<CollectionCard> Children);

/// <summary>New collection or Rename: its name and description; for a new one, the collection it goes inside.</summary>
public sealed record CollectionName(string Name, string? Description, long? ParentId = null);

/// <summary>Move to…: into another collection, or to the top when null.</summary>
public sealed record CollectionMove(long? ParentId);

/// <summary>Books added to a collection or taken out, and what to call them in the note: "Ghost Tower" or "3 books".</summary>
public sealed record CollectionBooks(IReadOnlyList<long> EntryIds, string What);

/// <summary>A new collection, and the note that says so.</summary>
public sealed record CollectionCreated(CollectionNode Collection, ActionResult Result);
