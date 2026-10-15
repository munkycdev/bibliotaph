using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Ask beside the search box, with no model server: what it does while AI is off.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// With AI off, the Ask button opens Ask's panel saying it needs AI, with its link to Settings > AI; typing a
    /// request doesn't search, and Enter still only says it needs AI. The link opens Settings > AI and leaves Ask.
    /// Back in the Library the search box searches as it always has, and Esc leaves Ask keeping the text.
    /// </summary>
    static async Task PlainSearchWithoutAiAsync(IServiceProvider services, Window window, int books)
    {
        if (services.GetRequiredService<PlainSearchService>().IsReady) throw new InvalidOperationException("The Ask check expects AI to be off.");
        var shell = services.GetRequiredService<ShellViewModel>();
        var search = services.GetRequiredService<SearchState>();
        var navigation = services.GetRequiredService<INavigationService>();
        shell.NavigateCommand.Execute(Route.Library);
        await Settle(window);
        var page = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Ask check couldn't open the Library.");
        var box = (TextBox)window.FindName("Search");
        var panel = (FrameworkElement)window.FindName("PlainSearchPanel");
        var message = (TextBlock)window.FindName("PlainSearchMessage");
        if (shell.PlainSearch.IsOn || panel.IsVisible || search.IsSearching) throw new InvalidOperationException("Ask is on, or a search is running, before the Ask check.");

        Click(window.FindName("PlainSearchButton") as Button, "Ask");
        await WaitUntilAsync(window, () => shell.PlainSearch is { IsOn: true, NeedsAi: true } && panel.IsVisible && message.Text == PlainSearchText.NeedsAi && Shown(window, "PlainSearchSetUpAi"),
            () => $"With AI off, the Ask button showed “{message.Text}” (panel showing {panel.IsVisible}), not that Ask needs AI with a link to set it up.");

        // A request is read on Enter, never searched as it is typed; without AI, Enter only says Ask needs AI.
        box.Text = "Mothership, level 4, jungle world with terraforming";
        await Task.Delay(500);
        await Settle(window);
        if (search.IsSearching) throw new InvalidOperationException($"Typing a request in Ask searched for “{search.Text}”.");
        Press(box, Key.Enter);
        await WaitUntilAsync(window, () => shell.PlainSearch is { NeedsAi: true, Parts.Count: 0 } && !search.IsSearching,
            () => $"Enter in Ask with AI off left “{message.Text}” and searched {search.IsSearching}.");
        await AuditAsync(window, "Ask with AI off");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "PlainSearchSetUpAi"), "Ask's Set AI up");
        await ExpectSettingsAsync(window, shell, SettingsSection.Ai, "Ask's Set AI up link");
        await WaitUntilAsync(window, () => !shell.PlainSearch.IsOn && !panel.IsVisible, () => "Following Ask's link to Settings > AI left Ask on.");
        navigation.GoBack();
        await Settle(window);

        // With Ask off, typing searches as it always has.
        box.Text = "dragon";
        await WaitUntilAsync(window, () => search.Text == "dragon" && shell.CurrentPage is LibraryViewModel { Items.Count: > 0 },
            () => $"With Ask off, typing “dragon” searched for “{search.Text}”.");

        // Esc leaves Ask and keeps what was typed.
        Click(window.FindName("PlainSearchButton") as Button, "Ask");
        await WaitUntilAsync(window, () => shell.PlainSearch.IsOn && panel.IsVisible, () => "The Ask button didn't turn Ask on again.");
        Press(box, Key.Escape);
        await WaitUntilAsync(window, () => !shell.PlainSearch.IsOn && !panel.IsVisible && box.Text == "dragon", () => $"Esc in Ask left Ask on {shell.PlainSearch.IsOn} with “{box.Text}”.");

        shell.ClearSearchCommand.Execute(null);
        page = shell.CurrentPage as LibraryViewModel ?? page;
        await WaitUntilAsync(window, () => !search.IsSearching && page.Items.Count == books, () => "Clearing the search after Ask didn't show the whole library.");
    }
}
