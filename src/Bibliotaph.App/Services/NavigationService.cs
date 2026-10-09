using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Services;

public interface INavigationService
{
    PageViewModel? Current { get; }
    bool CanGoBack { get; }
    event EventHandler? Navigated;
    void NavigateTo(Route route);
    void Show(PageViewModel page);
    void NavigateUp(Route route);
    bool GoBack();
}

/// <summary>
/// Keeps a back stack of page view models, not just routes, so going back restores a page as it was left
/// (search, filters, selection and scroll position, once those exist; spec §5.4).
/// </summary>
public sealed class NavigationService(Func<Route, PageViewModel> createPage) : INavigationService
{
    const int MaxDepth = 50;
    readonly LinkedList<PageViewModel> _back = new();

    public PageViewModel? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public event EventHandler? Navigated;

    public void NavigateTo(Route route)
    {
        if (Current?.Route == route) return;
        Show(createPage(route));
    }

    /// <summary>
    /// Shows a page made by the caller, such as a reader made with the book it opens, even on the same route as the
    /// page it replaces, which goes on the back stack.
    /// </summary>
    public void Show(PageViewModel page)
    {
        if (ReferenceEquals(Current, page)) return;
        if (Current is not null)
        {
            _back.AddLast(Current);
            if (_back.Count > MaxDepth) _back.RemoveFirst();
        }
        Current = page;
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Goes to a parent page, as from the breadcrumb: back to the latest <paramref name="route"/> page in the history,
    /// as it was left, dropping the pages after it; or to a new one if the history has none.
    /// </summary>
    public void NavigateUp(Route route)
    {
        if (Current?.Route == route) return;
        for (var node = _back.Last; node is not null; node = node.Previous)
        {
            if (node.Value.Route != route) continue;
            while (_back.Last != node) _back.RemoveLast();
            _back.RemoveLast();
            Current = node.Value;
            Navigated?.Invoke(this, EventArgs.Empty);
            return;
        }
        NavigateTo(route);
    }

    public bool GoBack()
    {
        if (_back.Last is not { } previous) return false;
        _back.RemoveLast();
        Current = previous.Value;
        Navigated?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
