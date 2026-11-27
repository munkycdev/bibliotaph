using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Presentation.Services;

public interface INavigationService
{
    PageViewModel? Current { get; }
    /// <summary>The page Back returns to, if any.</summary>
    PageViewModel? Previous { get; }
    bool CanGoBack { get; }
    /// <summary>Whether Back was used, and Forward can return to the page it left.</summary>
    bool CanGoForward { get; }
    event EventHandler? Navigated;
    void NavigateTo(Route route);
    void Show(PageViewModel page);
    void NavigateUp(Route route);
    /// <summary>Back to the page before; <paramref name="forward"/> false when the page left isn't one to return to.</summary>
    bool GoBack(bool forward = true);
    bool GoForward();
}

/// <summary>
/// Keeps a back stack of page view models, not just routes, so going back restores a page as it was left
/// (search, filters, selection and scroll position, once those exist).
/// </summary>
public sealed class NavigationService(Func<Route, PageViewModel> createPage) : INavigationService
{
    const int MaxDepth = 50;
    readonly LinkedList<PageViewModel> _back = new();
    // Pages Back left, latest last; going anywhere else forgets them, as a browser does.
    readonly LinkedList<PageViewModel> _forward = new();

    public PageViewModel? Current { get; private set; }

    public PageViewModel? Previous => _back.Last?.Value;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

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
        _forward.Clear();
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
            _forward.Clear();
            Current = node.Value;
            Navigated?.Invoke(this, EventArgs.Empty);
            return;
        }
        NavigateTo(route);
    }

    public bool GoBack(bool forward = true)
    {
        if (_back.Last is not { } previous) return false;
        _back.RemoveLast();
        if (forward && Current is not null) _forward.AddLast(Current);
        Current = previous.Value;
        Navigated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Returns to the page Back left, as it was left.</summary>
    public bool GoForward()
    {
        if (_forward.Last is not { } next) return false;
        _forward.RemoveLast();
        if (Current is not null) _back.AddLast(Current);
        Current = next.Value;
        Navigated?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
