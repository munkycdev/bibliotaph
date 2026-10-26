namespace Bibliotaph.Core;

/// <summary>
/// Settings › Online libraries' two kinds of service, in the Windows app and on the web: books read on the service's
/// own site, and files bought from a store, which come in differently.
/// </summary>
public static class OnlineLibraryGroups
{
    public const string ReadOnline = "Books you read online";

    public const string ReadOnlineNote =
        "Books you read on the service's own site come in as books you own elsewhere, with their covers and details, ready for "
        + "collections and binders. Each opens on its service.";

    public const string Stores = "PDFs you bought from stores";

    public const string StoresNote =
        "Books you bought as files come in as books to get: what you own at the store and haven't downloaded yet, each saying "
        + "where to get it.";
}
