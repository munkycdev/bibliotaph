using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Collections from folders: a folder of books proposed as a collection in Needs review.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// Three made-up books in one folder of a library folder of their own. Needs review asks to make a collection from
    /// them, listed under its own entry in Show; Accept makes it with the three books, and Undo takes it away again. The
    /// library folder is removed at the end.
    /// </summary>
    static async Task MakeFolderCollectionAsync(IServiceProvider services, Window window, int books)
    {
        const string Folder = "Ashen Vale";
        var roots = services.GetRequiredService<SourceRootStore>();
        var root = await roots.AddAsync(System.IO.Path.Combine(services.GetRequiredService<AppPaths>().Root, "smoke-folders"));
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        var collections = services.GetRequiredService<CollectionsService>();
        try
        {
            await SeedFolderBooksAsync(services, root.Id, Folder);
            navigation.NavigateTo(Route.NeedsReview);
            var review = shell.CurrentPage as NeedsReviewViewModel ?? throw new InvalidOperationException("The Needs review route didn't open Needs review.");
            // A new page loads as it opens, and Reload does nothing while it is loading.
            await WaitUntilAsync(window, () => !review.IsLoading, () => "Needs review didn't finish loading.");
            await review.ReloadCommand.ExecuteAsync(null);
            review.IsSuggestionsTab = true;
            await WaitUntilAsync(window, () => !review.IsLoading && review.Cards.OfType<FolderCollectionCardViewModel>().Any(c => c.Title == Folder),
                () => "The folder of three books isn't a card in Needs review.");
            if (!review.ShowChoices.Any(c => c.Name == "Collections from folders"))
                throw new InvalidOperationException("Needs review's Show menu has no Collections from folders.");
            var card = review.Cards.OfType<FolderCollectionCardViewModel>().Single(c => c.Title == Folder);
            if (card.Heading != $"Make a collection “{Folder}” from these 3 books?") throw new InvalidOperationException($"The folder's card asks “{card.Heading}”.");
            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Name == "AcceptFolderCollection" && b.DataContext == card && b.IsVisible),
                () => "The folder's card has no Accept showing.");
            await AuditAsync(window, "Needs review with a folder that could be a collection");

            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "AcceptFolderCollection" && b.DataContext == card), "Accept");
            await WaitUntilAsync(window, () => card.IsDone, () => "Accept didn't decide the folder's card.");
            if ((await collections.ListAsync()).SingleOrDefault(c => c.Name == Folder) is not { OwnCount: 3 })
                throw new InvalidOperationException("Accept didn't make a collection of the folder's three books.");
            await card.UndoCommand.ExecuteAsync(null);
            if (card.IsDone || (await collections.ListAsync()).Any(c => c.Name == Folder))
                throw new InvalidOperationException("Undo didn't take the folder's collection away and ask again.");
        }
        finally
        {
            await roots.RemoveAsync(root.Id);
            services.GetRequiredService<LibraryActivity>().Invalidate();
            // Even after a failure above, later checks count the Library's cards, so they wait for these three to go.
            navigation.NavigateTo(Route.Library);
            if (shell.CurrentPage is LibraryViewModel shown)
            {
                await shown.RefreshAsync();
                for (var tries = 0; tries < 50 && shown.Items.Count != books; tries++)
                {
                    await Task.Delay(200);
                    await shown.RefreshAsync();
                }
            }
        }
        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        await library.RefreshAsync();
        await WaitUntilAsync(window, () => library.Items.Count == books, () => $"Removing the folders' library folder left {library.Items.Count} books, not {books}.");
    }

    /// <summary>Three made-up books in <paramref name="folder"/>, read only from their names.</summary>
    static async Task SeedFolderBooksAsync(IServiceProvider services, long rootId, string folder)
    {
        var library = services.GetRequiredService<LibraryStore>();
        var index = services.GetRequiredService<IndexStore>();
        var entryStore = services.GetRequiredService<EntryStore>();
        var hints = services.GetRequiredService<MetadataHints>();
        string[] names = ["Cinder Ford.pdf", "Mill of Ash.pdf", "Grey Tower.pdf"];
        string Relative(string name) => System.IO.Path.Combine(folder, name);
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await library.ReconcileRootAsync(rootId, [.. names.Select(n => new ScannedFile(Relative(n), 1000, modified, false))]);
        var entries = new List<EntryId>();
        foreach (var file in await library.NextUnhashedAsync(100, includeOnlineOnly: true))
        {
            var at = Array.FindIndex(names, n => file.FullPath.EndsWith(Relative(n), StringComparison.Ordinal));
            if (at < 0) continue;
            // 9, a and b: no other made-up book, and neither real fixture (e and f), has these hashes.
            var hash = ContentHash.Parse(new string("9ab"[at], ContentHash.HexLength));
            var (documentId, _) = await library.AttachHashAsync(file, hash) ?? throw new InvalidOperationException("A made-up book in the folder didn't attach.");
            await index.UpsertDocumentAsync(
                new DocRow
                {
                    DocumentId = documentId,
                    ContentHash = hash.Hex,
                    Format = file.Format,
                    DisplayTitle = System.IO.Path.GetFileNameWithoutExtension(names[at]),
                    PageCount = 1,
                    FolderHint = folder,
                },
                [new PageRow(0, "1", 612, 792)], []);
            await hints.ApplyAsync(documentId);
            entries.Add((await entryStore.GetEntryAsync(documentId))?.EntryId ?? throw new InvalidOperationException("A made-up book in the folder has no card."));
        }
        if (entries.Count != names.Length) throw new InvalidOperationException("The made-up books in the folder weren't all added.");
        await services.GetRequiredService<MetadataProjector>().ProjectAsync(entries);
    }
}
