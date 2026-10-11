using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
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
}
