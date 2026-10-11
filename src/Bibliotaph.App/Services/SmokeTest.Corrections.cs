using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Easier corrections (scenario review, row 8): Cataloged wrong… and a correction offered to look-alikes.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// Two made-up books in one folder of a library folder of their own, both guessed as Dungeons &amp; Dragons by a
    /// model. Right-click > Cataloged wrong… on one opens its details with the guess marked; Done without a change
    /// leaves a Needs review card, whose Fix it opens them again. System set to Pathfinder offers the change to the
    /// other book, Change them makes it as a bulk edit, and Undo takes it back. The library folder is removed at the end.
    /// </summary>
    static async Task CorrectBooksAsync(IServiceProvider services, Window window, int books)
    {
        var roots = services.GetRequiredService<SourceRootStore>();
        var root = await roots.AddAsync(System.IO.Path.Combine(services.GetRequiredService<AppPaths>().Root, "smoke-corrections"));
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        var corrections = services.GetRequiredService<CorrectionService>();
        try
        {
            var (first, second) = await SeedLookAlikesAsync(services, root.Id);
            navigation.NavigateTo(Route.Library);
            var page = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library route didn't open the Library.");
            await page.RefreshAsync();
            await WaitUntilAsync(window, () => page.Items.Count == books + 2, () => $"The Library shows {page.Items.Count} books, not {books + 2}.");
            // By file, not title: the cards are found by the made-up books' entries.
            var card = page.Items.Single(i => i.EntryId == first);
            var guessed = card.SystemLabel;

            // The card menu's command, as right-click > Cataloged wrong… runs it.
            BookCommands.CatalogedWrong.Execute(card, FindChild<Views.LibraryView>(window));
            await WaitUntilAsync(window, () => page.Inspector is { IsCorrecting: true } inspector && inspector.Item.EntryId == first
                    && inspector.VisibleFields.Any(f => f.Field == MetadataFields.System && f.ShowsGuessed && f.ShowEvidence),
                () => "Cataloged wrong… didn't open the details with the guessed system marked and its evidence open.");
            await WaitUntilAsync(window, () => Descendants<Border>(window).Any(b => b.Name == "GuessedMark" && b.IsVisible),
                () => "The guessed system has no Guessed mark showing.");
            await AuditAsync(window, "the details open for Cataloged wrong…");
            await WaitUntilAsync(window, () => Shown(window, "CorrectionDone"), () => "The Cataloged wrong… banner has no Done.");
            // The card is saved after the banner goes, so the check waits for the end to be reported.
            bool? left = null;
            void OnEnded(object? sender, bool unfinished) => left = unfinished;
            var correcting = page.Inspector!;
            correcting.CorrectionEnded += OnEnded;
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "CorrectionDone"), "Done correcting");
            await WaitUntilAsync(window, () => left is not null, () => "Done didn't end Cataloged wrong….");
            correcting.CorrectionEnded -= OnEnded;
            if (left != true || !(await corrections.GetWaitingAsync()).Any(c => c.EntryId == first))
                throw new InvalidOperationException("Leaving Cataloged wrong… without a change left no Needs review card.");

            // Needs review's card opens the details to correct again.
            page.CloseDetailsCommand.Execute(null);
            navigation.NavigateTo(Route.NeedsReview);
            var review = shell.CurrentPage as NeedsReviewViewModel ?? throw new InvalidOperationException("The Needs review route didn't open Needs review.");
            // A new page loads as it opens, and Reload does nothing while it is loading.
            await WaitUntilAsync(window, () => !review.IsLoading, () => "Needs review didn't finish loading.");
            await review.ReloadCommand.ExecuteAsync(null);
            review.IsSuggestionsTab = true;
            await WaitUntilAsync(window, () => !review.IsLoading && review.Cards.OfType<CorrectionCardViewModel>().Any(c => c.Item.EntryId == first),
                () => "The book left cataloged wrong isn't a card in Needs review.");
            var waiting = review.Cards.OfType<CorrectionCardViewModel>().Single(c => c.Item.EntryId == first);
            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Name == "FixIt" && b.DataContext == waiting && b.IsVisible),
                () => "The Cataloged wrong card has no Fix it showing.");
            await AuditAsync(window, "Needs review with a Cataloged wrong card");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "FixIt" && b.DataContext == waiting), "Fix it");
            await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { Inspector: { IsCorrecting: true } inspector } && inspector.Item.EntryId == first,
                () => "Fix it didn't open the book's details to correct.");
            page = (LibraryViewModel)shell.CurrentPage!;
            var details = page.Inspector!;

            // System set to Pathfinder in the field's own editor.
            var system = details.Fields.Single(f => f.Field == MetadataFields.System);
            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Name == "EditButton" && b.DataContext == system && b.IsVisible),
                () => "The system field has no Edit button showing.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "EditButton" && b.DataContext == system), "Edit System");
            await WaitUntilAsync(window, () => system.IsEditing, () => "Edit didn't open the system field's editor.");
            system.EditText = "Pathfinder";
            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Command == system.SaveCommand && b.IsVisible && b.IsEnabled),
                () => "The system field's editor has no Save showing.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == system.SaveCommand), "Save System");
            const string Question = "Change 1 other book from “Fey Court” too?";
            await WaitUntilAsync(window, () => details.LookAlikeOffer is { Books: [var other] } && other == second && details.LookAlikeQuestion == Question,
                () => $"Correcting the system offered “{details.LookAlikeQuestion}”, not “{Question}”.");
            await WaitUntilAsync(window, () => Shown(window, "ChangeLookAlikes"), () => "The offer has no Change them showing.");
            await AuditAsync(window, "the details offering a correction to look-alikes");
            await WaitUntilAsync(window, () => page.Items.Single(i => i.EntryId == first).SystemLabel != guessed,
                () => $"The corrected card still reads “{guessed}”.");
            var corrected = page.Items.Single(i => i.EntryId == first).SystemLabel;

            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ChangeLookAlikes"), "Change them");
            await WaitUntilAsync(window, () => page.HasBulkUndo && page.BulkMessage == "Changed 1 book from “Fey Court”."
                    && page.Items.Single(i => i.EntryId == second).SystemLabel == corrected,
                () => $"Change them left the other card reading “{page.Items.Single(i => i.EntryId == second).SystemLabel}” ({page.BulkMessage}).");
            if (details.HasLookAlikeOffer) throw new InvalidOperationException("The offer stayed after Change them.");

            await WaitUntilAsync(window, () => Shown(window, "CorrectionDone"), () => "The Cataloged wrong… banner has no Done after Change them.");
            left = null;
            details.CorrectionEnded += OnEnded;
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "CorrectionDone"), "Done correcting");
            await WaitUntilAsync(window, () => left is not null, () => "Done didn't end Cataloged wrong….");
            details.CorrectionEnded -= OnEnded;
            if (left != false || (await corrections.GetWaitingAsync()).Any(c => c.EntryId == first))
                throw new InvalidOperationException("The corrected book still has a Cataloged wrong card.");

            await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Command == page.UndoBulkEditCommand && b.IsVisible && b.IsEnabled),
                () => "The bulk banner has no Undo showing.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == page.UndoBulkEditCommand), "Undo the edit");
            await WaitUntilAsync(window, () => !page.HasBulkUndo && page.Items.Single(i => i.EntryId == second).SystemLabel == guessed,
                () => $"Undo left the other card reading “{page.Items.Single(i => i.EntryId == second).SystemLabel}”, not “{guessed}”.");
            page.CloseDetailsCommand.Execute(null);
        }
        finally
        {
            await roots.RemoveAsync(root.Id);
            services.GetRequiredService<LibraryActivity>().Invalidate();
            // Even after a failure above, later checks count the Library's cards, so they wait for these two to go.
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
        await WaitUntilAsync(window, () => library.Items.Count == books, () => $"Removing the corrections folder left {library.Items.Count} books, not {books}.");
    }

    /// <summary>The two made-up books in “Fey Court”, read only from their names, with a model's guess of Dungeons &amp; Dragons each.</summary>
    static async Task<(EntryId First, EntryId Second)> SeedLookAlikesAsync(IServiceProvider services, long rootId)
    {
        var library = services.GetRequiredService<LibraryStore>();
        var index = services.GetRequiredService<IndexStore>();
        var metadata = services.GetRequiredService<MetadataStore>();
        var entryStore = services.GetRequiredService<EntryStore>();
        var hints = services.GetRequiredService<MetadataHints>();
        string[] names = ["Court of Thorns Primer.pdf", "Glimmerwood Masks.pdf"];
        static string Relative(string name) => System.IO.Path.Combine("Fey Court", name);
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await library.ReconcileRootAsync(rootId, [.. names.Select(n => new ScannedFile(Relative(n), 1000, modified, false))]);
        var entries = new EntryId[names.Length];
        foreach (var file in await library.NextUnhashedAsync(100, includeOnlineOnly: true))
        {
            var at = Array.FindIndex(names, n => file.FullPath.EndsWith(Relative(n), StringComparison.Ordinal));
            if (at < 0) continue;
            // 7 and 8: no other made-up book, and neither real fixture (e and f), has these hashes.
            var hash = ContentHash.Parse(new string((char)('7' + at), ContentHash.HexLength));
            var (documentId, _) = await library.AttachHashAsync(file, hash) ?? throw new InvalidOperationException("A made-up look-alike didn't attach.");
            await index.UpsertDocumentAsync(
                new DocRow
                {
                    DocumentId = documentId,
                    ContentHash = hash.Hex,
                    Format = file.Format,
                    DisplayTitle = System.IO.Path.GetFileNameWithoutExtension(names[at]),
                    PageCount = 1,
                    FolderHint = "Fey Court",
                },
                [new PageRow(0, "1", 612, 792)], []);
            await index.SetPageTextAsync(documentId, [new PageTextRow(0, "The court of the fey meets at midsummer.", "pdf", 1, false)]);
            await hints.ApplyAsync(documentId);
            var entry = (await entryStore.GetEntryAsync(documentId))?.EntryId ?? throw new InvalidOperationException("A made-up look-alike has no card.");
            await metadata.AddSuggestionsAsync(entry, hash.Hex,
                [new MetadataProposal(MetadataFields.System, "dnd", AssertionOrigin.Ai, "the court of the fey", [0])]);
            entries[at] = entry;
        }
        if (entries.Any(e => e.Value == 0)) throw new InvalidOperationException("The made-up look-alikes weren't all added.");
        await services.GetRequiredService<MetadataProjector>().ProjectAsync(entries);
        return (entries[0], entries[1]);
    }
}
