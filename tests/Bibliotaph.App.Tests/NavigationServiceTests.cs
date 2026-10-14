using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Tests;

public class NavigationServiceTests
{
    sealed class Page(Route route) : PageViewModel
    {
        public override Route Route => route;

        public override string Title => route.ToString();
    }

    readonly List<Page> _made = [];
    readonly NavigationService _navigation;
    int _navigated;

    public NavigationServiceTests()
    {
        _navigation = new NavigationService(route =>
        {
            var page = new Page(route);
            _made.Add(page);
            return page;
        });
        _navigation.Navigated += (_, _) => _navigated++;
    }

    [Fact]
    public void Going_to_a_page_shows_it_and_keeps_the_one_before_for_Back()
    {
        _navigation.NavigateTo(Route.Home);
        var home = _navigation.Current;
        _navigation.NavigateTo(Route.Library);

        Assert.Equal(Route.Library, _navigation.Current?.Route);
        Assert.Same(home, _navigation.Previous);
        Assert.True(_navigation.CanGoBack);
        Assert.Equal(2, _navigated);
    }

    [Fact]
    public void Going_to_the_page_already_showing_makes_no_new_page()
    {
        _navigation.NavigateTo(Route.Library);
        _navigation.NavigateTo(Route.Library);

        Assert.Single(_made);
        Assert.False(_navigation.CanGoBack);
        Assert.Equal(1, _navigated);
    }

    [Fact]
    public void Back_returns_the_very_page_that_was_left_so_it_shows_as_it_was()
    {
        _navigation.NavigateTo(Route.Library);
        var library = _navigation.Current;
        _navigation.NavigateTo(Route.Settings);

        Assert.True(_navigation.GoBack());

        Assert.Same(library, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
        Assert.Equal(2, _made.Count);
    }

    [Fact]
    public void Back_with_nowhere_to_go_does_nothing()
    {
        _navigation.NavigateTo(Route.Home);

        Assert.False(_navigation.GoBack());

        Assert.Equal(Route.Home, _navigation.Current?.Route);
        Assert.Equal(1, _navigated);
    }

    [Fact]
    public void A_page_shown_on_the_route_already_showing_still_puts_the_old_one_on_the_back_stack()
    {
        var first = new Page(Route.Viewer);
        var second = new Page(Route.Viewer);
        _navigation.Show(first);
        _navigation.Show(second);

        Assert.Same(second, _navigation.Current);
        Assert.Same(first, _navigation.Previous);
    }

    [Fact]
    public void Going_up_returns_to_the_latest_page_of_that_route_and_forgets_the_pages_after_it()
    {
        _navigation.NavigateTo(Route.Home);
        var home = _navigation.Current;
        _navigation.NavigateTo(Route.Collections);
        var collections = _navigation.Current;
        _navigation.NavigateTo(Route.Library);
        _navigation.NavigateTo(Route.Viewer);

        _navigation.NavigateUp(Route.Collections);

        Assert.Same(collections, _navigation.Current);
        Assert.Same(home, _navigation.Previous);
        Assert.True(_navigation.GoBack());
        Assert.False(_navigation.CanGoBack);
        Assert.Equal(4, _made.Count);
    }

    [Fact]
    public void Going_up_to_a_route_not_in_the_history_opens_a_new_page_of_it()
    {
        _navigation.NavigateTo(Route.Library);
        var library = _navigation.Current;

        _navigation.NavigateUp(Route.Sessions);

        Assert.Equal(Route.Sessions, _navigation.Current?.Route);
        Assert.Same(library, _navigation.Previous);
    }

    [Fact]
    public void The_history_keeps_only_the_latest_fifty_pages()
    {
        var pages = Enumerable.Range(0, 52).Select(_ => new Page(Route.Viewer)).ToList();
        foreach (var page in pages) _navigation.Show(page);

        var backs = 0;
        while (_navigation.GoBack()) backs++;

        Assert.Equal(50, backs);
        Assert.Same(pages[1], _navigation.Current);
    }
}
