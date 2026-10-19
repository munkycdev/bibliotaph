using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>A pack of files made by hand: a PDF packed with an image, as a party's character sheets are.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// Pack together with a PDF among the ticked cards: the scanned handout and a handout image make a pack of files,
    /// whose card counts files and whose details call it a pack, list the PDF marked as one, and offer Split into
    /// separate files. A word on the scan's page is found under the pack, not under a card of the scan's own; the split
    /// gives both their cards back, and the page is found under the scan again. Shown with each file on a card of its
    /// own, the pack's two files have their cards back while it stays one pack: the scan's page is found under the scan's
    /// card, and its details are the pack's.
    /// </summary>
    static async Task PackFilesTogetherAsync(IServiceProvider services, Window window)
    {
        const string Name = "Smoke Box Set";
        var shell = services.GetRequiredService<ShellViewModel>();
        var search = services.GetRequiredService<SearchState>();
        services.GetRequiredService<INavigationService>().NavigateTo(Route.Library);
        var page = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await page.RefreshAsync();
        // Cards are found by file: earlier checks give the scan a title of its own.
        var paths = await services.GetRequiredService<LibraryStore>().GetRelativePathsAsync();
        long DocumentOf(string name) => paths.First(p => p.RelativePath.EndsWith(name, StringComparison.Ordinal)).DocumentId;
        var (scan, handout) = (DocumentOf("IMG_0042.pdf"), DocumentOf("Handout 4.png"));
        LibraryItemViewModel? Own(long documentId) => page.Items.FirstOrDefault(i => !i.IsPack && i.DocumentId == documentId);
        await WaitUntilAsync(window, () => Own(scan) is not null && Own(handout) is not null,
            () => "The Library doesn't show the scan and a handout as cards of their own.");

        page.Selection.StartSelectingCommand.Execute(null);
        page.Selection.ClearSelectionCommand.Execute(null);
        await page.ActivateCommand.ExecuteAsync(Own(scan)!);
        await page.ActivateCommand.ExecuteAsync(Own(handout)!);
        await Settle(window);
        if (!page.Selection.CanPackSelected || Descendants<Button>(window).FirstOrDefault(b => b.Name == "PackSelected") is not { IsVisible: true } button)
            throw new InvalidOperationException("Pack together isn't offered for a PDF and an image.");
        Click(button, "Pack together");
        await WaitUntilAsync(window, () => page.Selection.PackDialog is not null, () => "Pack together didn't open its dialog.");
        var dialog = page.Selection.PackDialog!;
        if (dialog.Heading != "Make a pack of 2 files" || dialog.Eyebrow != "PACK")
            throw new InvalidOperationException($"The dialog says “{dialog.Eyebrow}: {dialog.Heading}”.");
        dialog.Name = Name;
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == dialog.PackCommand), "Pack together");
        await WaitUntilAsync(window, () => page.Selection.PackDialog is null && page.Selection.Undo.HasUndo, () => $"Packing didn't finish ({dialog.Problem}).");
        if (page.Selection.Undo.Message != $"Made a pack of 2 files: {Name}.") throw new InvalidOperationException($"Pack together says “{page.Selection.Undo.Message}”.");
        page.Selection.StopSelectingCommand.Execute(null);
        await WaitUntilAsync(window, () => page.Items.Any(i => i is { Title: Name, IsPack: true, HasPdfMembers: true, SizeLabel: "2 files" }) && Own(scan) is null,
            () => $"The pack of files isn't in the Library ({string.Join(", ", page.Items.Select(i => $"{i.Title} ({i.SizeLabel})"))}).");
        var pack = page.Items.First(i => i.Title == Name);

        // The scan's page is found under the pack, its card.
        search.Search("box set");
        page.Tab = ResultsTab.Pages;
        await WaitUntilAsync(window, () => page.Hits.Any(h => h.Item.EntryId == pack.EntryId && h.Pages.Any(p => p.Hit.DocumentId == scan))
            && page.Hits.All(h => h.Item.IsPack || h.Item.DocumentId != scan),
            () => $"Searching the scan's page didn't find it under the pack ({string.Join(", ", page.Hits.Select(h => h.Item.Title))}).");
        search.Search("");
        page.Tab = ResultsTab.Documents;

        await ShowEachFileAsync(window, page, search, pack, scan, handout);

        await page.OpenDetailsCommand.ExecuteAsync(pack);
        await WaitUntilAsync(window, () => page.Inspector is { IsPack: true, Eyebrow: "PACK", SplitPackLabel: "Split into separate files", PackImages.Count: 2 } inspector
            && inspector.PackImages.Count(i => i.IsPdf) == 1, () => "The pack's details don't list its PDF and its image.");
        await AuditAsync(window, "a pack of files' details");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == page.Inspector!.SplitPackCommand), "Split into separate files");
        await WaitUntilAsync(window, () => Own(scan) is not null && Own(handout) is not null && page.Items.All(i => i.Title != Name),
            () => "Splitting the pack didn't give the scan and the handout their cards back.");
        if (page.Selection.Undo.Message != $"Split {Name} into 2 files.") throw new InvalidOperationException($"The split says “{page.Selection.Undo.Message}”.");
        var scanCard = Own(scan)!.EntryId;

        search.Search("box set");
        page.Tab = ResultsTab.Pages;
        await WaitUntilAsync(window, () => page.Hits.Any(h => h.Item.EntryId == scanCard && h.Pages.Any(p => p.Hit.DocumentId == scan)),
            () => "After the split, the scan's page isn't found under its own card.");
        search.Search("");
        page.Tab = ResultsTab.Documents;
    }

    /// <summary>The toolbar's Each file: the pack's files on cards of their own, then Packs again, as later checks expect.</summary>
    static async Task ShowEachFileAsync(Window window, LibraryViewModel page, SearchState search, LibraryItemViewModel pack, long scan, long handout)
    {
        var each = Descendants<RadioButton>(window).FirstOrDefault(r => r.Name == "FilesViewButton");
        if (each is not { IsVisible: true, IsEnabled: true }) throw new InvalidOperationException("The toolbar has no Each file button.");
        try
        {
            each.IsChecked = true;
            LibraryItemViewModel? File(long documentId) => page.Items.FirstOrDefault(i => i is { IsPackFile: true } && i.DocumentId == documentId);
            await WaitUntilAsync(window, () => page.ShowPackFiles && File(scan) is not null && File(handout) is not null && page.Items.All(i => i.EntryId != pack.EntryId || i.IsPackFile),
                () => $"Each file doesn't show the pack's files ({string.Join(", ", page.Items.Select(i => $"{i.Title} ({i.Meta})"))}).");
            if (File(handout)!.Meta != $"in {pack.Title}") throw new InvalidOperationException($"A file in the pack says “{File(handout)!.Meta}”.");
            await AuditAsync(window, "the Library with each file on a card");

            search.Search("box set");
            page.Tab = ResultsTab.Pages;
            await WaitUntilAsync(window, () => page.Hits.Any(h => h.Item.IsPackFile && h.Item.DocumentId == scan && h.Pages.Any(p => p.Hit.DocumentId == scan)),
                () => $"With each file shown, the scan's page isn't found under the scan ({string.Join(", ", page.Hits.Select(h => h.Item.Title))}).");
            search.Search("");
            page.Tab = ResultsTab.Documents;

            await WaitUntilAsync(window, () => File(handout) is not null, () => "The handout's own card didn't come back after the search.");
            await page.OpenDetailsCommand.ExecuteAsync(File(handout)!);
            await WaitUntilAsync(window, () => page.Inspector is { IsPack: true, PackImages.Count: 2 }, () => "A file's details aren't its pack's.");
            page.CloseDetailsCommand.Execute(null);
        }
        finally
        {
            search.Search("");
            page.Tab = ResultsTab.Documents;
            page.IsPacksView = true;
        }
        await WaitUntilAsync(window, () => !page.ShowPackFiles && page.Items.Any(i => i.EntryId == pack.EntryId && i.IsPack),
            () => "Packs didn't show the pack as one card again.");
    }
}
