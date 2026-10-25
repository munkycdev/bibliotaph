namespace Bibliotaph.Api.Contracts;

/// <summary>A saved search in the toolbar's list: every one, pinned or not, by name.</summary>
public sealed record SavedSearchInfo(long Id, string Name, bool Pinned);

/// <summary>
/// A saved search as its page opens it: the Library's search, filters, scope and order (<see cref="LibraryQuery.Sort"/>),
/// whether it shows a list rather than covers, and why it shows more than it was saved with, when it does. The page's
/// subtitle, what it says once the search or filters differ from the saved ones, and the name Save as new starts from
/// come with it.
/// </summary>
public sealed record SavedSearch(long Id, string Name, string Subtitle, string ChangedText, string CopyName, LibraryQuery Query, bool List, string? Note, bool Pinned);

/// <summary>Save search and Save as new: a name for what the Library shows.</summary>
public sealed record SavedSearchSave(string Name, LibraryQuery Query, bool List);

/// <summary>Update saved search: it keeps what the Library shows now.</summary>
public sealed record SavedSearchChange(LibraryQuery Query, bool List);

public sealed record SavedSearchRename(string Name);

public sealed record SavedSearchCreated(SavedSearch Search, ActionResult Result);
