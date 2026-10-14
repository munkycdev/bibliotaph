using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>The Library's order, kept from one page to the next and across restarts.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// An order picked while browsing and another while searching each come back on their own: on clearing the search,
    /// on a new Library page, and from the settings as the next run reads them. The usual orders are put back after.
    /// </summary>
    static async Task RememberSortAsync(IServiceProvider services, Window window, int books)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var search = services.GetRequiredService<SearchState>();
        var settings = services.GetRequiredService<SettingsStore>();
        shell.NavigateCommand.Execute(Route.Library);
        var page = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        await WaitUntilAsync(window, () => page.Items.Count == books, () => $"The Library shows {page.Items.Count} books, not {books}.");
        if (page.SortChoice.Value != LibrarySort.RecentlyAdded) throw new InvalidOperationException($"The Library starts in {page.SortChoice.Label} order, not Recently added.");

        await PickSortAsync(window, page, LibrarySort.Title);
        search.Search("dragon");
        await WaitUntilAsync(window, () => page.IsSearching && page.SortChoice.Value == LibrarySort.Relevance,
            () => $"A search started in {page.SortChoice.Label} order, not Best match.");
        await PickSortAsync(window, page, LibrarySort.RecentlyOpened);
        search.Search("");
        await WaitUntilAsync(window, () => !page.IsSearching && page.SortChoice.Value == LibrarySort.Title && page.Items.Count == books,
            () => $"Clearing the search left the Library in {page.SortChoice.Label} order, not Title A–Z.");

        shell.NavigateCommand.Execute(Route.Home);
        await WaitUntilAsync(window, () => shell.CurrentPage is HomeViewModel, () => "Home didn't open.");
        shell.NavigateCommand.Execute(Route.Library);
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel again && !ReferenceEquals(again, page),
            () => "The Library route didn't open a new Library page.");
        var next = (LibraryViewModel)shell.CurrentPage!;
        await WaitUntilAsync(window, () => next.SortChoice.Value == LibrarySort.Title && next.Items.Count == books,
            () => $"A new Library page opened in {next.SortChoice.Label} order, not Title A–Z.");
        search.Search("dragon");
        await WaitUntilAsync(window, () => next.IsSearching && next.SortChoice.Value == LibrarySort.RecentlyOpened,
            () => $"A search on the new page started in {next.SortChoice.Label} order, not Recently opened.");

        // The settings are written in the background, as the pick is made.
        string? browse = null, searching = null;
        for (var tries = 0; tries < 50; tries++)
        {
            browse = await settings.GetAsync(SettingKeys.LibrarySort);
            searching = await settings.GetAsync(SettingKeys.LibrarySearchSort);
            if (browse == nameof(LibrarySort.Title) && searching == nameof(LibrarySort.RecentlyOpened)) break;
            await Task.Delay(200);
        }
        if (browse != nameof(LibrarySort.Title) || searching != nameof(LibrarySort.RecentlyOpened))
            throw new InvalidOperationException($"The settings keep “{browse}” and “{searching}” as the orders, not Title and RecentlyOpened.");
        var restarted = new LibrarySorts(settings, services.GetRequiredService<ILogger<LibrarySorts>>());
        await restarted.InitializeAsync();
        if (restarted.Browse != LibrarySort.Title || restarted.Search != LibrarySort.RecentlyOpened)
            throw new InvalidOperationException($"The next run would read {restarted.Browse} and {restarted.Search} as the orders.");

        // Later checks expect the usual orders.
        await PickSortAsync(window, next, LibrarySort.Relevance);
        search.Search("");
        await WaitUntilAsync(window, () => !next.IsSearching && next.Items.Count == books, () => "Clearing the search didn't bring the library back.");
        await PickSortAsync(window, next, LibrarySort.RecentlyAdded);
        var sorts = services.GetRequiredService<LibrarySorts>();
        await WaitUntilAsync(window, () => sorts.Browse == LibrarySort.RecentlyAdded && sorts.Search == LibrarySort.Relevance,
            () => $"Picking the usual orders again kept {sorts.Browse} and {sorts.Search}.");
    }

    /// <summary>Picks an order from the toolbar's Sort menu, as a click on it would.</summary>
    static async Task PickSortAsync(Window window, LibraryViewModel page, LibrarySort sort)
    {
        await Settle(window);
        var menu = Descendants<ComboBox>(window).FirstOrDefault(c => c.IsVisible && c.IsEnabled && AutomationProperties.GetName(c) == "Sort")
            ?? throw new InvalidOperationException("The Library's Sort menu isn't showing.");
        menu.SelectedItem = menu.Items.Cast<Choice<LibrarySort>>().FirstOrDefault(c => c.Value == sort)
            ?? throw new InvalidOperationException($"The Sort menu has no {sort} order.");
        await WaitUntilAsync(window, () => page.SortChoice.Value == sort, () => $"Picking {sort} left the Library in {page.SortChoice.Label} order.");
    }
}
