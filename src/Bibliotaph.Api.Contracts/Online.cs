namespace Bibliotaph.Api.Contracts;

/// <summary>A service a library page can be imported from, with what Settings says about it.</summary>
public sealed record OnlineService(string Key, string Name, string LibraryPage, string Explanation);

/// <summary>
/// What importing a saved library page will do, as Settings › Online libraries previews it: its groups of books by
/// what each becomes, the books from earlier imports the page no longer lists, and how many of the page's covers are
/// among the images given with it so far. <paramref name="Wanted"/> is the addresses of the covers still missing, which
/// Send to Bibliotaph fetches from the service's page.
/// </summary>
public sealed record OnlinePreview(string Token, string Service, string Summary, IReadOnlyList<OnlineGroup> Groups, IReadOnlyList<OnlineMissing> Missing,
    int Covers, int CoversFound, IReadOnlyList<string>? Wanted = null);

/// <summary>The preview's books with one outcome: "New · 12" and what that means.</summary>
public sealed record OnlineGroup(string Heading, string Explanation, IReadOnlyList<OnlineRow> Rows);

/// <summary>A book in the preview, by its place in the page, ticked to import unless there is nothing to do.</summary>
public sealed record OnlineRow(int Index, string Title, string Detail, bool CanChoose);

/// <summary>A book imported before that the page no longer lists, which Remove from library takes away.</summary>
public sealed record OnlineMissing(long EntryId, string Title);

/// <summary>A saved page read, or why it couldn't be.</summary>
public sealed record OnlinePageRead(OnlinePreview? Preview, string? Problem);

/// <summary>The books of a preview to import, by their places in it.</summary>
public sealed record OnlineImportChoice(IReadOnlyList<int> Rows);

/// <summary>What an import did, with its Undo, and the page's preview as it now stands.</summary>
public sealed record OnlineImported(ActionResult Result, OnlinePreview Preview);
