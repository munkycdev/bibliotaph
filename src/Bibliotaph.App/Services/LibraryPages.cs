using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.App.Services;

/// <summary>
/// Opens the Library as a page of its own: the whole library, a part of it such as Favorites, or with one book's
/// details open, as Home and the sidebar do.
/// </summary>
public sealed class LibraryPages(INavigationService navigation, SearchState search, Func<LibraryViewModel> createLibrary)
{
    /// <summary>The Library, scoped or whole. Nothing happens when that is already the page shown.</summary>
    public void Open(LibraryScope? scope)
    {
        if (navigation.Current is LibraryViewModel { SmartViews.ActiveView: null } current && current.Scope?.Key == scope?.Key) return;
        var page = createLibrary();
        page.ShowScope(scope);
        navigation.Show(page);
    }

    /// <summary>A saved Smart View: the Library with its search, filters and order. Nothing happens when it is already shown.</summary>
    public void OpenView(SmartViewInfo view)
    {
        if (navigation.Current is LibraryViewModel { SmartViews.ActiveView: { } shown } && shown.Id == view.Id) return;
        var page = createLibrary();
        page.SmartViews.OpenView(view);
        navigation.Show(page);
    }

    /// <summary>
    /// The whole Library at one part of it, as a figure on the Summary page opens it: <paramref name="query"/> in the
    /// search box (or none) and <paramref name="filters"/>'s filters, with the order and layout as they were.
    /// </summary>
    public void OpenFiltered(string query, SmartViewDefinition? filters)
    {
        // Searched first, so the page made next takes the order remembered for searching or browsing.
        search.Search(query);
        var page = createLibrary();
        page.FiltersOnLoad = filters;
        navigation.Show(page);
    }

    /// <summary>The Summary page, as from Home.</summary>
    public void OpenSummary() => navigation.NavigateTo(Route.Summary);

    /// <summary>The Collections page, as from Home.</summary>
    public void OpenCollections() => navigation.NavigateTo(Route.Collections);

    /// <summary>The whole Library with a book's details open, as from a cover on Home.</summary>
    public void ShowDetails(EntryId entryId)
    {
        var page = createLibrary();
        page.DetailsOnLoad = entryId;
        navigation.Show(page);
    }

    /// <summary>
    /// The whole Library with a book's editor open for Cataloged wrong…, as from a cover's menu on Home or a "You said
    /// this book was cataloged wrong" card.
    /// </summary>
    public void ShowCorrection(EntryId entryId)
    {
        var page = createLibrary();
        page.DetailsOnLoad = entryId;
        page.CorrectOnLoad = true;
        navigation.Show(page);
    }
}
