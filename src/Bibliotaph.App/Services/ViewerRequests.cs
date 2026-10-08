using Bibliotaph.Core.Search;

namespace Bibliotaph.App.Services;

/// <summary>
/// A book to open: from a search hit (with its page and the search, so the words can be highlighted) or from the
/// inspector (at the first page).
/// </summary>
public sealed record ViewerRequest(long DocumentId, string Title, int PageIndex = 0, SearchQuery? Query = null);

/// <summary>
/// Hands a request to the viewer page the navigation service is about to create. Each viewer page takes its
/// request once, so Back returns to the page as it was, not to a fresh request.
/// </summary>
public sealed class ViewerRequests(INavigationService navigation)
{
    ViewerRequest? _pending;

    public void Open(ViewerRequest request)
    {
        _pending = request;
        navigation.NavigateTo(Route.Viewer);
    }

    public ViewerRequest? Take()
    {
        var request = _pending;
        _pending = null;
        return request;
    }
}
