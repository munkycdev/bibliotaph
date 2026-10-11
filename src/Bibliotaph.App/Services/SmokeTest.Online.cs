using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

static partial class SmokeTest
{
    /// <summary>
    /// Online libraries (O2): a made-up D&amp;D Beyond library page, saved with its covers beside the data root by
    /// verify.ps1, imported from Library &gt; Online libraries. The preview, the two new books with their links and
    /// covers, the preview again with both imported, then Undo and the library as it was.
    /// </summary>
    static async Task ImportOnlineLibraryAsync(IServiceProvider services, Window window, int books)
    {
        var root = System.IO.Path.GetDirectoryName(services.GetRequiredService<AppPaths>().Root)!;
        var saved = System.IO.Path.Combine(root, "online", "dndbeyond-library.html");
        if (!System.IO.File.Exists(saved))
        {
            Log.Warning("Smoke test: no saved library page at {Path}, so importing one is left out", saved);
            return;
        }

        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        navigation.NavigateTo(Route.Library);
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "OnlineLibraries"), "Online libraries");
        await WaitUntilAsync(window, () => shell.CurrentPage is OnlineLibrariesViewModel, () => "Online libraries didn't open its page.");
        var page = (OnlineLibrariesViewModel)shell.CurrentPage!;
        if (page.Services is not [{ Name: "D&D Beyond" }]) throw new InvalidOperationException("Online libraries doesn't list D&D Beyond.");

        // The file picker can't be driven here, so the page is read as the picker would read it.
        await page.ReadSavedPageAsync(saved);
        await Settle(window);
        if (page.Problem is { } problem) throw new InvalidOperationException($"The saved page wasn't read: {problem}");
        if (page.Summary != "Your D&D Beyond library: 2 books. 1 book shared with you was left out.")
            throw new InvalidOperationException($"The preview says “{page.Summary}”.");
        if (page.Groups is not [{ Outcome: OnlineOutcome.New, Items.Count: 2 }] || page.HasMissing)
            throw new InvalidOperationException($"The preview's groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");
        if (!Descendants<TextBlock>(window).Any(t => t.Text == "The Ember Vault" && t.IsVisible)) throw new InvalidOperationException("The preview doesn't list the first book.");
        await AuditAsync(window, "an online library's import preview");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ImportOnline"), "Import 2 books");
        await WaitUntilAsync(window, () => !page.IsBusy && page.Message == "Added 2 books owned on D&D Beyond.",
            () => $"Importing says “{page.Message}”.");
        if (page.Groups is not [{ Outcome: OnlineOutcome.Unchanged, Items.Count: 2 }])
            throw new InvalidOperationException($"After importing, the preview's groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");

        var visible = await services.GetRequiredService<LibraryStore>().GetVisibleEntryIdsAsync();
        var cards = await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter(visible)));
        var vault = cards.FirstOrDefault(c => c.Title == "The Ember Vault") ?? throw new InvalidOperationException("The imported book isn't in the library.");
        if (cards.Count != books + 2 || !vault.IsElsewhere || vault.OnlineLink != "https://www.dndbeyond.com/sources/dnd/ember-vault" || vault.Cover is null)
            throw new InvalidOperationException($"The library has {cards.Count} cards; the imported book links to {vault.OnlineLink} with cover {vault.Cover}.");
        await AuditAsync(window, "an online library imported");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "UndoOnline"), "Undo the import");
        await WaitUntilAsync(window, () => !page.IsBusy && page.Message is null && page.Groups is [{ Outcome: OnlineOutcome.New }],
            () => $"Undo left “{page.Message}”.");
        cards = await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter(visible)));
        if (cards.Count != books) throw new InvalidOperationException($"After Undo the library has {cards.Count} cards, not {books}.");
        navigation.GoBack();
        await Settle(window);
    }
}
