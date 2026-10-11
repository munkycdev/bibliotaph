using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.App.Services;

/// <summary>
/// Opens a pin, from the sidebar or Home (Pinned Views plan, choice 6): a book where it was left, or its details when
/// it has no file (owned elsewhere), or in the online reader when it's owned online; Favorites, a collection or a saved search in the Library; a binder in its reader.
/// A book whose folder is offline or whose file has gone opens to the reader's prompt, which says what to do.
/// </summary>
public sealed class PinOpener(LibraryPages library, SessionPages binders, ReaderWindows readers, CollectionDirectory collections, SmartViewDirectory views,
    LibraryQueries queries, OnlineReaders online)
{
    public async Task OpenAsync(PinnedItemViewModel item)
    {
        var id = item.Target.Id ?? 0;
        switch (item.Target.Kind)
        {
            case PinKind.Favorites:
                library.Open(LibraryScope.Favorites);
                break;
            case PinKind.Collection when collections.Find(id) is { } collection:
                library.Open(collections.ScopeFor(collection));
                break;
            case PinKind.Binder:
                binders.Open(id);
                break;
            case PinKind.SavedSearch when views.Find(id) is { } view:
                library.OpenView(view);
                break;
            case PinKind.Entry when item.Book is { } book:
                if (book is { OpensOnline: true, OnlineLink: { } link })
                {
                    if (await online.OpenAsync(new OnlineReaderRequest(book.EntryId, book.Title, link) { Place = book.Entry.OnlineLabel }) is not null)
                        library.ShowDetails(book.EntryId);
                }
                else if (!book.CanOpen) library.ShowDetails(book.EntryId);
                else if (await RequestAsync(book) is { } request) readers.OpenInMainWindow(request);
                break;
        }
    }

    async Task<ViewerRequest?> RequestAsync(LibraryItemViewModel book)
    {
        if (!book.IsPack) return book.BookRequest();
        var images = await Task.Run(() => queries.GetPackImagesAsync(book.EntryId));
        if (images.Count == 0) return null;
        var pack = images.Select(i => new PackStep(i.DocumentId, System.IO.Path.GetFileNameWithoutExtension(i.Name))).ToList();
        return new ViewerRequest(pack[0].DocumentId, pack[0].Title) { Pack = pack, PackTitle = book.Title };
    }
}
