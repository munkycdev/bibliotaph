using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;

namespace Bibliotaph.App.Services;

/// <summary>
/// Opens the Library as a page of its own: the whole library, a part of it such as Favorites (slice 3 plan, choice 3),
/// or with one book's details open, as Home and the sidebar do.
/// </summary>
public sealed class LibraryPages(INavigationService navigation, Func<LibraryViewModel> createLibrary)
{
    /// <summary>The Library, scoped or whole. Nothing happens when that is already the page shown.</summary>
    public void Open(LibraryScope? scope)
    {
        if (navigation.Current is LibraryViewModel current && current.Scope?.Key == scope?.Key) return;
        var page = createLibrary();
        page.ShowScope(scope);
        navigation.Show(page);
    }

    /// <summary>The whole Library with a book's details open, as from a cover on Home.</summary>
    public void ShowDetails(EntryId entryId)
    {
        var page = createLibrary();
        page.DetailsOnLoad = entryId;
        navigation.Show(page);
    }
}
