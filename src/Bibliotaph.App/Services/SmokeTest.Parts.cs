using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Slice 5: an adventure inside a file, marked by hand in the reader, as a card of its own.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// The smoke PDF's Chapter One bookmark indexed as its own adventure from the reader's Contents panel: its card
    /// says which file and page it is on and the file's card counts it; a page in it is found under it, and one outside
    /// it under the file. Opened from that hit, the reader names both, shades its bookmark, says where in it the page
    /// is, and Find looks in it unless Whole file is ticked. Its details remove it, Undo brings it back, and it is
    /// removed again so later checks see the book as it was.
    /// </summary>
    static async Task UsePartsAsync(IServiceProvider services, Window window, long documentId)
    {
        const string Title = "The Mill Wheel";
        var shell = services.GetRequiredService<ShellViewModel>();
        var search = services.GetRequiredService<SearchState>();
        var navigation = services.GetRequiredService<INavigationService>();
        shell.NavigateCommand.Execute(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        LibraryItemViewModel? File() => library.Items.FirstOrDefault(i => i.DocumentId == documentId && !i.IsPart);
        LibraryItemViewModel? Part() => library.Items.FirstOrDefault(i => i.DocumentId == documentId && i.IsPart);
        await WaitUntilAsync(window, () => File() is not null && Part() is null, () => "The smoke PDF isn't in the Library, or already has an adventure.");

        // Index as its own adventure, from the Chapter One bookmark's menu: it runs to the end of the file.
        await library.OpenBookCommand.ExecuteAsync(File()!);
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf && viewer.Outline.Any(o => o.Title == "Chapter One") && Shown(window, "MarkPart"),
            () => "The PDF didn't open with its bookmarks and Mark as adventure.");
        viewer.IndexBookmarkCommand.Execute(viewer.Outline.First(o => o.Title == "Chapter One"));
        await WaitUntilAsync(window, () => viewer.PartEditor is not null && Shown(window, "SavePartButton"), () => "Index as its own adventure didn't ask for its pages.");
        if (viewer.PartEditor is not { Title: "Chapter One", From: "1", To: "" } editor)
            throw new InvalidOperationException($"The adventure starts as “{viewer.PartEditor?.Title}”, {viewer.PartEditor?.From} to {viewer.PartEditor?.To}.");
        editor.Title = Title;
        await Settle(window);
        await AuditAsync(window, "the reader's adventure dialog");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "SavePartButton"), "the dialog's Make its card");
        await WaitUntilAsync(window, () => viewer.PartEditor is null && viewer.Notice?.Contains(Title, StringComparison.Ordinal) == true,
            () => $"Saving didn't make the adventure's card (the reader says “{viewer.Notice}”).");
        navigation.GoBack();
        await Settle(window);

        // Its card, beside its file's, and the file's card counting it.
        library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("Back didn't return to the Library.");
        await WaitUntilAsync(window, () => Part() is { Title: Title } && File() is { PartsLabel: "1 adventure" },
            () => "The Library doesn't show the adventure's card and its file counting it.");
        var file = File()!;
        if (Part()!.Meta != $"in {file.Title} · p. 1" || !file.Meta.Contains("1 adventure", StringComparison.Ordinal))
            throw new InvalidOperationException($"The adventure's card says “{Part()!.Meta}” and its file's “{file.Meta}”.");

        // A page in it is found under it; one outside it under the file.
        search.Search("dragon");
        library.Tab = ResultsTab.Pages;
        await WaitUntilAsync(window, () => library.Hits.Any(h => h.Item.DocumentId == documentId && h.Item.IsPart && h.Pages.Any(p => p.Hit.PdfPage == 1))
                && !library.Hits.Any(h => h.Item.DocumentId == documentId && !h.Item.IsPart),
            () => "Searching for dragon didn't find its page under the adventure.");
        var hit = library.Hits.First(h => h.Item.IsPart && h.Item.DocumentId == documentId).Pages.First(p => p.Hit.PdfPage == 1);
        search.Search("front matter");
        await WaitUntilAsync(window, () => library.Hits.Any(h => h.Item.DocumentId == documentId && !h.Item.IsPart),
            () => "Searching for the front matter didn't find its page under the file.");

        // Opened from its page: the reader names both, shades its bookmark, and Find looks in its pages.
        library.OpenPageCommand.Execute(hit);
        viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open page didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf && viewer.IsPart && viewer.CurrentPageIndex == 1 && viewer.Outline.Any(o => o.InPart),
            () => "The adventure's page didn't open in the reader as the adventure.");
        if (viewer.Title != $"{Title} · in {file.Title}" || viewer.PartPosition != "page 1 of 1 of this adventure"
            || viewer.Outline.FirstOrDefault(o => o.InPart)?.Title != "Chapter One" || viewer.Outline.Count(o => o.InPart) != 1)
            throw new InvalidOperationException($"The reader says “{viewer.Title}”, “{viewer.PartPosition}”.");
        var wholeFile = Descendants<CheckBox>(window).FirstOrDefault(c => c.Name == "FindWholeFile");
        if (wholeFile is not { IsVisible: true, IsEnabled: true }) throw new InvalidOperationException("The reader doesn't offer Find in the whole file.");
        viewer.FindText = "front";
        await viewer.FindCommand.ExecuteAsync(null);
        if (viewer.FindStatus != "No matches in this adventure") throw new InvalidOperationException($"Find in the adventure said {viewer.FindStatus}.");
        wholeFile.IsChecked = true;
        await WaitUntilAsync(window, () => viewer.FindStatus.StartsWith("1 of ", StringComparison.Ordinal), () => $"Find in the whole file said {viewer.FindStatus}.");
        await AuditAsync(window, "the reader with an adventure inside a file");
        navigation.GoBack();
        await Settle(window);
        search.Search("");
        library.Tab = ResultsTab.Documents;

        // Its details: Remove adventure shows its file's, with Undo, which brings it back.
        library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("Back didn't return to the Library.");
        await WaitUntilAsync(window, () => Part() is not null, () => "The Library doesn't show the adventure after the reader.");
        await library.OpenDetailsCommand.ExecuteAsync(Part()!);
        await WaitUntilAsync(window, () => library.Inspector is { IsPart: true } && Shown(window, "RemoveThisPart"), () => "The adventure's details don't offer Remove adventure.");
        if (!library.Inspector!.PartOf.StartsWith($"in {file.Title}", StringComparison.Ordinal))
            throw new InvalidOperationException($"The adventure's details say “{library.Inspector.PartOf}”.");
        await AuditAsync(window, "an adventure's details");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "RemoveThisPart"), "Remove adventure");
        await WaitUntilAsync(window, () => library.Inspector is { IsPart: false, HasPartUndo: true, HasParts: false } && Part() is null && Shown(window, "UndoPart"),
            () => "Remove adventure didn't take its card away and show its file's details with Undo.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "UndoPart"), "Undo the removal");
        await WaitUntilAsync(window, () => library.Inspector is { Parts: [{ Title: Title }] } && Part() is not null, () => "Undo didn't bring the adventure back.");
        await AuditAsync(window, "a file's details with its adventures");

        // Removed again, so later checks see the book as it was.
        var inspector = library.Inspector!;
        await inspector.RemovePartCommand.ExecuteAsync(inspector.Parts[0]);
        library.CloseDetailsCommand.Execute(null);
        await WaitUntilAsync(window, () => Part() is null && File() is { PartsLabel: "" }, () => "Removing the adventure left its card.");
    }

    /// <summary>
    /// Slice 5b: a proposal that the smoke PDF holds two adventures, as the rules would make from bookmarks and blurbs,
    /// is one Needs review card. Not split folds it and Undo brings it back; Edit renames one, the other is unticked,
    /// and Accept makes one card, which Undo takes away. Not split at the end leaves the book as it was.
    /// </summary>
    static async Task ReviewPartProposalAsync(IServiceProvider services, Window window, long documentId)
    {
        const string Renamed = "The Mill Wheel at Midnight";
        var entry = await services.GetRequiredService<EntryStore>().GetEntryAsync(documentId) ?? throw new InvalidOperationException("The smoke PDF has no card.");
        var proposals = services.GetRequiredService<PartProposalService>();
        var parts = services.GetRequiredService<PartsService>();
        await services.GetRequiredService<PartProposalStore>().SaveAsync(entry.ContentHash, PartProposalSource.Rules,
        [
            new ProposedPart("The Front Matter", new PartPages(0, 0)),
            new ProposedPart("The Mill Wheel", new PartPages(1, 1), "2", "for four characters of 2nd level"),
        ]);
        if (await proposals.CountPendingAsync() != 1) throw new InvalidOperationException("The proposal isn't counted for Needs review.");

        var shell = services.GetRequiredService<ShellViewModel>();
        services.GetRequiredService<INavigationService>().NavigateTo(Route.NeedsReview);
        var page = shell.CurrentPage as NeedsReviewViewModel ?? throw new InvalidOperationException("The Needs review route didn't open Needs review.");
        // Opening the page may still be loading it, and Reload does nothing while it is; the card is for the file's
        // card, through whichever copy of the file the proposal names.
        await WaitUntilAsync(window, () => !page.IsLoading, () => "Needs review didn't finish loading.");
        await page.ReloadCommand.ExecuteAsync(null);
        PartProposalCardViewModel? Proposal() => page.Cards.OfType<PartProposalCardViewModel>().SingleOrDefault(c => c.Item.FileEntryId == entry.EntryId);
        await WaitUntilAsync(window, () => !page.IsLoading && Proposal() is not null,
            () => $"The proposal isn't a card in Needs review (proposal cards: {string.Join(", ", page.Cards.OfType<PartProposalCardViewModel>().Select(c => c.Item.FileTitle))}).");
        var card = Proposal()!;
        if (card.Heading != "This file looks like it holds 2 adventures." || card.Rows is not [{ Summary: "p. i" }, { Summary: "p. 1 · Level 2" }])
            throw new InvalidOperationException($"The card says “{card.Heading}”, {string.Join(", ", card.Rows.Select(r => r.Summary))}.");
        Button? On(string name, System.Windows.Input.ICommand command) => Descendants<Button>(window).FirstOrDefault(b => b.Name == name && b.Command == command);

        await WaitUntilAsync(window, () => Shown(window, "NotSplit"), () => "The proposal has no Not split.");
        Click(On("NotSplit", card.NotSplitCommand), "Not split");
        await WaitUntilAsync(window, () => card.IsDone, () => "Not split didn't fold the card.");
        if (await proposals.CountPendingAsync() != 0) throw new InvalidOperationException("A file kept as one book is still proposed.");
        await card.UndoCommand.ExecuteAsync(null);
        await WaitUntilAsync(window, () => !card.IsDone && Shown(window, "EditParts"), () => "Undo didn't bring the proposal back.");

        Click(On("EditParts", card.EditCommand), "Edit");
        await WaitUntilAsync(window, () => card.IsEditing, () => "Edit didn't make the titles and pages editable.");
        card.Rows[0].IsChecked = false;
        card.Rows[1].Title = Renamed;
        await Settle(window);
        if (card.AcceptLabel != "Make 1 card") throw new InvalidOperationException($"With one ticked, Accept reads “{card.AcceptLabel}”.");
        await AuditAsync(window, "a proposal of adventures in Needs review");
        Click(On("AcceptParts", card.AcceptCommand), "Accept");
        await WaitUntilAsync(window, () => card.IsDone, () => $"Accept didn't make the card ({card.Problem}).");
        if (await parts.GetAsync(entry.EntryId) is not [{ Title: Renamed, Pages: { First: 1, Last: 1 } }])
            throw new InvalidOperationException("Accept didn't make one card for the renamed adventure.");

        await card.UndoCommand.ExecuteAsync(null);
        await WaitUntilAsync(window, () => !card.IsDone, () => "Undo didn't bring the proposal back.");
        if ((await parts.GetAsync(entry.EntryId)).Count != 0) throw new InvalidOperationException("Undo left the adventure's card.");
        await card.NotSplitCommand.ExecuteAsync(null);
        if (await proposals.CountPendingAsync() != 0) throw new InvalidOperationException("Not split left the proposal waiting.");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }
}
