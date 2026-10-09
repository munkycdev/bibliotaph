using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// <c>Bibliotaph.exe --smoke-test --data-root &lt;folder&gt; [--smoke-files &lt;folder&gt;]</c>: opens the real window,
/// visits every route in light and dark, fills a small made-up library and browses and searches it in both themes,
/// then exits 0, or 1 on any exception or binding error. With <c>--smoke-files</c> (tests/fixtures/smoke), it also
/// opens a real PDF from a search hit and reads it, and opens an image. CI runs it on Windows so a broken resource or
/// template fails the build instead of the first launch. It is not a substitute for looking.
/// </summary>
static class SmokeTest
{
    public static async Task<int> RunAsync(IServiceProvider services, Window window, string? smokeFiles)
    {
        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            if (window.Icon is null) throw new InvalidOperationException("The main window has no icon.");
            // Covers are encoded on indexing threads, so check the codec off the UI thread.
            await Task.Run(CheckImageCodec);
            var navigation = services.GetRequiredService<INavigationService>();
            var theme = services.GetRequiredService<ThemeService>();
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                foreach (var route in Enum.GetValues<Route>())
                {
                    navigation.NavigateTo(route);
                    await Settle(window);
                    Log.Information("Smoke test: {Route} rendered in {Theme}", route, preference);
                }
                while (navigation.GoBack()) await Settle(window);
            }

            // The sidebar's status line opens Library folders.
            var status = (Button)window.FindName("StatusButton");
            ((IInvokeProvider)new ButtonAutomationPeer(status).GetPattern(PatternInterface.Invoke)).Invoke();
            await Settle(window);
            if (services.GetRequiredService<ShellViewModel>().CurrentPage?.Route != Route.LibraryFolders)
                throw new InvalidOperationException("The sidebar status didn't open Library folders.");
            navigation.GoBack();
            await Settle(window);

            await SeedLibraryAsync(services);
            (long Pdf, long Image)? real = smokeFiles is null ? null : await SeedRealFilesAsync(services, smokeFiles);
            var books = real is null ? 5 : 7;
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                await BrowseLibraryAsync(services, window, books);
                Log.Information("Smoke test: library browsed and searched in {Theme}", preference);
                await ReviewAsync(services, window, decide: preference == ThemePreference.Light);
                Log.Information("Smoke test: Needs review and the vocabulary worked through in {Theme}", preference);
                await ShowFolderProgressAsync(services, window);
                await ShowAboutAsync(services, window);
                if (real is not { } files) continue;
                await ReadBookAsync(services, window, files.Pdf);
                await PopOutAsync(services, window, files.Pdf);
                await ViewImageAsync(services, window, files.Image);
                Log.Information("Smoke test: a PDF read and an image viewed in {Theme}", preference);
            }
            await theme.SetPreferenceAsync(ThemePreference.System);
            await Settle(window);
            if (Application.Current.Windows.Count != 1 || services.GetRequiredService<ReaderWindows>().Windows.Count > 0)
                throw new InvalidOperationException($"The smoke test left {Application.Current.Windows.Count - 1} other windows open.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Smoke test failed");
            return 1;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
            // A failed run leaves no pop-out behind either.
            services.GetRequiredService<ReaderWindows>().CloseAll();
        }

        if (bindingErrors.Errors.Count > 0)
        {
            foreach (var error in bindingErrors.Errors) Log.Error("Smoke test binding error: {Error}", error);
            return 1;
        }
        Log.Information("Smoke test passed");
        return 0;
    }

    /// <summary>
    /// A made-up library written straight to the catalog and index, so the Library has covers, a list, results and
    /// an inspector to draw. Its folder doesn't exist and indexing is paused, so nothing tries to read the files.
    /// </summary>
    static async Task SeedLibraryAsync(IServiceProvider services)
    {
        var indexing = services.GetRequiredService<IndexingService>();
        indexing.Pause(Lane.Index);
        indexing.Pause(Lane.Ocr);
        var root = await services.GetRequiredService<SourceRootStore>()
            .AddAsync(System.IO.Path.Combine(services.GetRequiredService<AppPaths>().Root, "smoke-library"));
        var library = services.GetRequiredService<LibraryStore>();
        var index = services.GetRequiredService<IndexStore>();
        (string Path, string Title, string[] Pages)[] books =
        [
            ("Setting/Gazetteer of the Marches.pdf", "Gazetteer of the Marches", ["Contents", "The red dragon sleeps beneath the mill.", "Goblins raid the tavern."]),
            ("Monsters/Dragon Lairs.pdf", "Dragon Lairs", ["A lich keeps a tavern ledger.", "Red dragon lair maps."]),
            ("D&D 5e/Adventures/Haunted Inn.pdf", "Haunted Inn", ["The inn is haunted; a secret door hides behind the bar."]),
            ("Handouts/Tavern Map.png", "Tavern Map", []),
            ("Scans/IMG_0042.pdf", "IMG_0042", ["A handout scanned from a box set."]),
        ];
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // The scanner reports paths with the platform's separator.
        static string Relative(string path) => path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        await library.ReconcileRootAsync(root.Id, [.. books.Select(b => new ScannedFile(Relative(b.Path), 1000, modified, false))]);
        var files = await library.NextUnhashedAsync(books.Length, includeOnlineOnly: true);
        var documents = new List<long>();
        foreach (var file in files)
        {
            var book = books.Single(b => file.FullPath.EndsWith(Relative(b.Path), StringComparison.Ordinal));
            // Digits, so the made-up books never share a hash with the real fixtures (e and f).
            var hash = ContentHash.Parse(new string((char)('1' + Array.IndexOf(books, book)), ContentHash.HexLength));
            var (documentId, _) = await library.AttachHashAsync(file, hash) ?? throw new InvalidOperationException("A made-up file didn't attach.");
            documents.Add(documentId);
            var isImage = file.Format != SourceFormats.Pdf;
            await index.UpsertDocumentAsync(
                new DocRow
                {
                    DocumentId = documentId,
                    ContentHash = hash.Hex,
                    Format = file.Format,
                    DisplayTitle = book.Title,
                    PageCount = isImage ? null : book.Pages.Length,
                    WidthPx = isImage ? 1600 : null,
                    HeightPx = isImage ? 1200 : null,
                    FolderHint = System.IO.Path.GetDirectoryName(Relative(book.Path)),
                },
                [.. book.Pages.Select((_, i) => new PageRow(i, (i + 1).ToString(CultureInfo.InvariantCulture), 612, 792))], []);
            if (!isImage) await index.SetPageTextAsync(documentId, [.. book.Pages.Select((text, i) => new PageTextRow(i, text, "pdf", 1, false))]);
        }
        // Indexing is paused, so the hints from folder and file names are read here.
        var hints = services.GetRequiredService<MetadataHints>();
        foreach (var documentId in documents) await hints.ApplyAsync(documentId);

        // Something for Needs review besides the scan's missing title: a classifier that disagrees with a folder name,
        // and a type the vocabulary doesn't know yet.
        var metadata = services.GetRequiredService<MetadataStore>();
        var titles = await Task.WhenAll(documents.Select(async id => (Id: id, Title: await services.GetRequiredService<IndexQueries>().GetTitleAsync(id))));
        var lairs = titles.Single(t => t.Title == "Dragon Lairs").Id;
        var gazetteer = titles.Single(t => t.Title == "Gazetteer of the Marches").Id;
        await metadata.AddSuggestionsAsync(lairs, [new MetadataProposal(MetadataFields.Types, "rulebook", AssertionOrigin.Ai, "the rules for lairs", [1])]);
        var (heist, _) = await services.GetRequiredService<VocabularyStore>().ProposeTermAsync("type", "Heist kit");
        await metadata.AddSuggestionsAsync(gazetteer, [new MetadataProposal(MetadataFields.Types, heist.Key, AssertionOrigin.Ai, "everything a heist needs")]);
        await services.GetRequiredService<MetadataProjector>().ProjectAsync([lairs, gazetteer]);
    }

    /// <summary>
    /// Needs review's cards, the sidebar count, and Settings > Vocabulary. With <paramref name="decide"/>, it also
    /// accepts and undoes a conflict, rejects, undoes and adds a new term, types a title, and splits One-shot out of
    /// Adventure; otherwise it only draws them (the decisions are made by then).
    /// </summary>
    static async Task ReviewAsync(IServiceProvider services, Window window, bool decide)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var activity = services.GetRequiredService<LibraryActivity>();
        activity.Invalidate();
        navigation.NavigateTo(Route.NeedsReview);
        var page = shell.CurrentPage as NeedsReviewViewModel ?? throw new InvalidOperationException("The Needs review route didn't open Needs review.");
        await WaitUntilAsync(window, () => !page.IsLoading && page.Cards.Count > 0, () => "Needs review shows no cards.");
        if (decide)
        {
            var nav = shell.NavItems.Single(n => n.Route == Route.NeedsReview);
            await WaitUntilAsync(window, () => activity.SuggestionCount == page.Remaining && nav.Count == activity.ReviewCount.ToString("N0", CultureInfo.CurrentCulture),
                () => $"The sidebar says {nav.Count} need review ({activity.SuggestionCount} suggestions); the page says {page.Remaining}.");

            var conflict = page.Cards.OfType<ReviewCardViewModel>().FirstOrDefault(c => c is { Kind: ReviewKind.Conflict, Title: "Dragon Lairs" })
                ?? throw new InvalidOperationException("The classifier's disagreement with the Monsters folder isn't a card.");
            if (conflict is not { CurrentText: "Rulebook", ProposedText: "Bestiary", HasEvidence: true } || !conflict.CurrentSource.Contains("page 2", StringComparison.Ordinal))
                throw new InvalidOperationException($"The conflict card shows {conflict.CurrentText} ({conflict.CurrentSource}) against {conflict.ProposedText} ({conflict.Evidence}).");
            await conflict.AcceptCommand.ExecuteAsync(null);
            if (!conflict.IsDone) throw new InvalidOperationException("Accept didn't decide the card.");
            await conflict.UndoCommand.ExecuteAsync(null);
            if (conflict.IsDone) throw new InvalidOperationException("Undo didn't bring the card back.");
            await Settle(window);

            var term = page.Cards.OfType<TermCardViewModel>().FirstOrDefault(c => c.Label == "Heist kit")
                ?? throw new InvalidOperationException("The new type isn't a card.");
            await term.RejectCommand.ExecuteAsync(null);
            await term.UndoCommand.ExecuteAsync(null);
            await term.AddCommand.ExecuteAsync(null);
            if (!term.IsDone) throw new InvalidOperationException("Adding the new type didn't decide its card.");

            var title = page.Cards.OfType<ReviewCardViewModel>().FirstOrDefault(c => c.Kind == ReviewKind.MissingTitle)
                ?? throw new InvalidOperationException("The scan's file name isn't a missing-title card.");
            title.EditCommand.Execute(null);
            await Settle(window);
            title.EditText = "Box Set Handout";
            await title.SaveCommand.ExecuteAsync(null);
            if (!title.IsDone) throw new InvalidOperationException($"Typing a title didn't decide its card: {title.Problem}");
        }
        page.IsFilesTab = true;
        await Settle(window);
        page.IsSuggestionsTab = true;
        await Settle(window);

        navigation.NavigateTo(Route.Vocabulary);
        var vocabulary = shell.CurrentPage as VocabularyViewModel ?? throw new InvalidOperationException("The Vocabulary route didn't open the vocabulary.");
        await WaitUntilAsync(window, () => vocabulary.Terms.Any(t => t.Label == "Adventure"), () => "The vocabulary doesn't list Adventure.");
        if (decide)
        {
            vocabulary.NewTerm = "One-shot";
            await vocabulary.AddTermCommand.ExecuteAsync(null);
            if (vocabulary.AddNote?.Contains("Adventure", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"Adding One-shot said {vocabulary.AddProblem ?? vocabulary.AddNote}, not that it took a name from Adventure.");
            var oneShot = vocabulary.Terms.Single(t => t.Label == "One-shot");
            oneShot.NewAlias = "one shots";
            await oneShot.AddAliasCommand.ExecuteAsync(null);
            await WaitUntilAsync(window, () => vocabulary.Terms.Any(t => t is { Label: "One-shot", IsEditing: true, HasAliases: true }),
                () => "One-shot didn't get its other name.");
        }
        vocabulary.Terms.First().EditCommand.Execute(null);
        await Settle(window);
        while (shell.CurrentPage is not LibraryViewModel && navigation.GoBack()) await Settle(window);
    }

    /// <summary>The Library in covers and as a list, both search tabs, a query with a problem, and the inspector.</summary>
    static async Task BrowseLibraryAsync(IServiceProvider services, Window window, int books)
    {
        var search = services.GetRequiredService<SearchState>();
        services.GetRequiredService<INavigationService>().NavigateTo(Route.Library);
        var page = services.GetRequiredService<ShellViewModel>().CurrentPage as LibraryViewModel
            ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        // Queries run off the UI thread, so an idle dispatcher doesn't mean the results are in: wait for them.
        await WaitUntilAsync(window, () => page.Items.Count == books, () => $"The Library shows {page.Items.Count} books, not {books}.");

        page.Layout = LibraryLayout.List;
        page.ShowFilters = true;
        await Settle(window);
        await EditMetadataAsync(window, page, books);

        search.Search("dragon");
        await WaitUntilAsync(window, () => page.IsSearching && page.Items.Count > 0 && !page.IsEmpty, () => "Searching for dragon found no documents.");
        page.Tab = ResultsTab.Pages;
        await WaitUntilAsync(window, () => page.Hits.Count > 0, () => "Searching for dragon found no pages.");

        await page.OpenDetailsCommand.ExecuteAsync(page.Hits[0].Item);
        await Settle(window);
        if (page.Inspector is null) throw new InvalidOperationException("The inspector didn't open.");
        page.CloseDetailsCommand.Execute(null);

        // Fields find documents, which the Documents tab lists.
        page.Tab = ResultsTab.Documents;
        search.Search("system:5e type:adventure");
        await WaitUntilAsync(window, () => page.Items is [{ Title: "Haunted Inn" }], () => "Searching by system and type didn't find the Haunted Inn.");

        search.Search("length:short tavern");
        await WaitUntilAsync(window, () => page.IssueText is not null, () => "A field that isn't searchable yet didn't say so.");

        search.Search("nothing-matches-this");
        await WaitUntilAsync(window, () => page.IsEmpty, () => "A search with no results didn't show the empty state.");

        // The search box's × ends the search; the box gets the text through SearchState like any search.
        var box = (TextBox)window.FindName("Search");
        var clear = box.Template.FindName("Clear", box) as Button ?? throw new InvalidOperationException("The search box has no clear button.");
        if (clear.Visibility != Visibility.Visible) throw new InvalidOperationException("The search box's × is hidden while it has text.");
        ((IInvokeProvider)new ButtonAutomationPeer(clear).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitUntilAsync(window, () => box.Text.Length == 0 && !search.IsSearching && clear.Visibility == Visibility.Collapsed,
            () => "The search box's × didn't clear the search.");
        page.Tab = ResultsTab.Documents;
        page.Layout = LibraryLayout.Grid;
        page.ShowFilters = false;
        await WaitUntilAsync(window, () => !page.IsSearching && page.Items.Count == books, () => $"Clearing the search didn't bring back all {books} books.");
    }

    /// <summary>
    /// Metadata from folder names in the list and the filters, then the inspector's Details: edit a title, see it in the
    /// list, open the evidence, and reset it to the suggestion.
    /// </summary>
    static async Task EditMetadataAsync(Window window, LibraryViewModel page, int books)
    {
        await WaitUntilAsync(window, () => page.Items.Any(i => i is { Title: "Haunted Inn", SystemLabel: "D&D 5e", KindLabel: "Adventure" }),
            () => "The Haunted Inn's folders didn't make it a D&D 5e adventure.");
        await WaitUntilAsync(window, () => page.SystemChoices.Any(c => c.Value == "dnd"), () => "The game system filter doesn't offer D&D.");
        page.SystemChoice = page.SystemChoices.First(c => c.Value == "dnd");
        await WaitUntilAsync(window, () => page.Items is [{ Title: "Haunted Inn" }], () => $"Filtering by D&D shows {page.Items.Count} books, not 1.");
        page.ClearFiltersCommand.Execute(null);
        await WaitUntilAsync(window, () => page.Items.Count == books, () => $"Clearing the filters didn't bring back all {books} books.");

        await page.OpenDetailsCommand.ExecuteAsync(page.Items.First(i => i.Title == "Haunted Inn"));
        await Settle(window);
        var inspector = page.Inspector ?? throw new InvalidOperationException("The inspector didn't open.");
        MetadataFieldViewModel Field(MetadataField field) => inspector.Fields.First(f => f.Field == field);
        if (Field(MetadataFields.System) is not { IsSuggested: true, Display: "Dungeons & Dragons" })
            throw new InvalidOperationException($"The inspector's game system says {Field(MetadataFields.System).Display}, not a suggested Dungeons & Dragons.");
        Field(MetadataFields.System).ShowEvidence = true;
        inspector.ShowAllFields = true;
        await Settle(window);

        var title = Field(MetadataFields.Title);
        title.EditCommand.Execute(null);
        await Settle(window);
        title.EditText = "Haunted Inn Revised";
        await title.SaveCommand.ExecuteAsync(null);
        if (title.Problem is not null) throw new InvalidOperationException($"Saving a title said: {title.Problem}");
        await WaitUntilAsync(window, () => page.Items.Any(i => i.Title == "Haunted Inn Revised"), () => "The edited title didn't reach the list.");
        if (!Field(MetadataFields.Title).CanReset || !Field(MetadataFields.System).ShowEvidence)
            throw new InvalidOperationException("After saving, the title can't be reset or the evidence closed.");
        await Field(MetadataFields.Title).ResetCommand.ExecuteAsync(null);
        await WaitUntilAsync(window, () => page.Items.Any(i => i.Title == "Haunted Inn"), () => "Resetting the title didn't bring back the suggestion.");
        page.CloseDetailsCommand.Execute(null);
        await Settle(window);
    }

    /// <summary>
    /// The synthetic PDF and PNG in tests/fixtures/smoke, added as a second folder and indexed by hand (indexing is
    /// paused), so the viewer has real files to open.
    /// </summary>
    static async Task<(long Pdf, long Image)> SeedRealFilesAsync(IServiceProvider services, string folder)
    {
        var root = await services.GetRequiredService<SourceRootStore>().AddAsync(folder);
        var library = services.GetRequiredService<LibraryStore>();
        var index = services.GetRequiredService<IndexStore>();
        string[] names = ["smoke-book.pdf", "smoke-map.png"];
        await library.ReconcileRootAsync(root.Id, [.. names.Select(name =>
        {
            var info = new System.IO.FileInfo(System.IO.Path.Combine(folder, name));
            if (!info.Exists) throw new InvalidOperationException($"The smoke fixture {name} is missing from {folder}.");
            return new ScannedFile(name, info.Length, info.LastWriteTimeUtc, false);
        })]);
        long pdf = 0, image = 0;
        foreach (var file in await library.NextUnhashedAsync(10, includeOnlineOnly: true))
        {
            var isPdf = file.Format == SourceFormats.Pdf;
            var hash = ContentHash.Parse(new string(isPdf ? 'e' : 'f', ContentHash.HexLength));
            var (documentId, _) = await library.AttachHashAsync(file, hash) ?? throw new InvalidOperationException("A smoke fixture didn't attach.");
            await index.UpsertDocumentAsync(
                new DocRow
                {
                    DocumentId = documentId,
                    ContentHash = hash.Hex,
                    Format = file.Format,
                    DisplayTitle = isPdf ? "Smoke Test Book" : "Smoke Test Map",
                    PageCount = isPdf ? 2 : null,
                    WidthPx = isPdf ? null : 320,
                    HeightPx = isPdf ? null : 200,
                },
                isPdf ? [new PageRow(0, "i", 612, 792), new PageRow(1, "1", 612, 792)] : [], []);
            if (isPdf)
            {
                await index.SetPageTextAsync(documentId,
                [
                    new PageTextRow(0, "Smoke Test Book This page is the front matter.", "pdf", 1, false),
                    new PageTextRow(1, "Chapter One The red dragon sleeps beneath the mill. A secret door hides behind the bar.", "pdf", 1, false),
                ]);
                pdf = documentId;
            }
            else image = documentId;
        }
        if (pdf == 0 || image == 0) throw new InvalidOperationException("The smoke fixtures weren't both added.");
        return (pdf, image);
    }

    /// <summary>
    /// Opens the PDF from a search hit: it must open at the hit's page with the word marked, draw the page, select
    /// its text, find in the book, follow a bookmark at Fit page, draw tiles at 300%, and go Back to the results.
    /// </summary>
    static async Task ReadBookAsync(IServiceProvider services, Window window, long documentId)
    {
        var search = services.GetRequiredService<SearchState>();
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        search.Search("dragon");
        library.Tab = ResultsTab.Pages;
        PageHitViewModel? hit = null;
        await WaitUntilAsync(window, () => (hit = library.Hits.SelectMany(h => h.Pages).FirstOrDefault(p => p.Hit.DocumentId == documentId)) is not null,
            () => "Searching for dragon didn't find the smoke PDF's page.");

        library.OpenPageCommand.Execute(hit);
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open page didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, () => $"The PDF didn't open (still {viewer.Mode}).");
        if (!viewer.IsPdf) throw new InvalidOperationException($"The PDF didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        if (viewer.Highlights.Items.Count == 0) throw new InvalidOperationException("The search's word wasn't marked on the page.");

        var pages = FindChild<Bibliotaph.Viewer.PdfPagesView>(window) ?? throw new InvalidOperationException("The viewer has no page surface.");
        await WaitUntilAsync(window, () => pages.CurrentPageIndex == 1 && pages.PageModels[1] is { Image: not null, IsPreview: false },
            () => $"Page 1 wasn't drawn at full quality (page in view {pages.CurrentPageIndex}).");
        if (viewer.PageEntry != "1" || !pages.PositionText.Contains("PDF page 2 of 2", StringComparison.Ordinal))
            throw new InvalidOperationException($"The page box says {viewer.PageEntry} and the footer {pages.PositionText}.");

        await pages.SelectAllOnCurrentPageAsync();
        if (!pages.HasSelection) throw new InvalidOperationException("Select all found no text on the page.");

        viewer.FindText = "mill";
        await viewer.FindCommand.ExecuteAsync(null);
        if (!viewer.FindStatus.StartsWith("1 of ", StringComparison.Ordinal)) throw new InvalidOperationException($"Find said {viewer.FindStatus}.");

        viewer.PageEntry = "i";
        viewer.GoToPageCommand.Execute(null);
        await WaitUntilAsync(window, () => pages.CurrentPageIndex == 0, () => "Going to page i didn't show the first page.");

        if (viewer.Outline is not [{ Title: "Front matter", Page: "i" }, { Title: "Chapter One", Page: "1" } chapter])
            throw new InvalidOperationException($"The contents panel shows {viewer.Outline.Count} bookmarks, not the fixture's two.");
        viewer.Zoom = Bibliotaph.Viewer.PdfPagesView.FitPage;
        viewer.OpenOutlineEntryCommand.Execute(chapter);
        await WaitUntilAsync(window, () => pages.CurrentPageIndex == 1, () => "The Chapter One bookmark didn't go to its page.");
        viewer.PageEntry = "i";
        viewer.GoToPageCommand.Execute(null);
        await WaitUntilAsync(window, () => pages.CurrentPageIndex == 0, () => "Going back to page i at Fit page didn't show the first page.");

        viewer.Zoom = 3;
        await WaitUntilAsync(window, () => pages.PageModels[pages.CurrentPageIndex].Tiles.Count > 0, () => "No tiles were drawn at 300%.");
        viewer.Zoom = 0;

        // Zoom in and out step from the fitted size, as the + and - buttons and Ctrl+plus and Ctrl+minus do; Ctrl+0 fits again.
        await Settle(window);
        System.Windows.Input.NavigationCommands.IncreaseZoom.Execute(null, pages);
        var zoomedIn = viewer.Zoom;
        if (zoomedIn <= 0) throw new InvalidOperationException($"Zoom in from Fit width left the zoom at {zoomedIn}.");
        System.Windows.Input.NavigationCommands.DecreaseZoom.Execute(null, pages);
        if (viewer.Zoom <= 0 || viewer.Zoom >= zoomedIn) throw new InvalidOperationException($"Zoom out from {zoomedIn:P0} gave {viewer.Zoom}.");
        viewer.ResetZoomCommand.Execute(null);
        if (viewer.Zoom != Bibliotaph.Viewer.PdfPagesView.FitWidth) throw new InvalidOperationException($"Ctrl+0 left the zoom at {viewer.Zoom}.");

        navigation.GoBack();
        await Settle(window);
        if (shell.CurrentPage is not LibraryViewModel) throw new InvalidOperationException("Back didn't return to the Library.");
        search.Search("");
        library.Tab = ResultsTab.Documents;
        await Settle(window);
    }

    /// <summary>Opens the PNG from the inspector, then a book whose file isn't there, which must say so.</summary>
    static async Task ViewImageAsync(IServiceProvider services, Window window, long documentId)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library isn't showing.");
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == documentId), () => "The smoke image isn't in the Library.");

        library.OpenBookCommand.Execute(library.Items.First(i => i.DocumentId == documentId));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsImage || viewer.Mode == ViewerMode.Problem, () => $"The image didn't open (still {viewer.Mode}).");
        if (viewer.Image is not { Width: > 0 }) throw new InvalidOperationException($"The image didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        viewer.Zoom = 2;
        await Settle(window);
        // The breadcrumb's Library goes back to the Library as it was, not a new one.
        shell.OpenSectionCommand.Execute(null);
        await Settle(window);
        if (!ReferenceEquals(shell.CurrentPage, library)) throw new InvalidOperationException("The Library breadcrumb didn't return to the Library.");

        // The made-up books' folder doesn't exist, so opening one shows why it can't be read.
        library.OpenBookCommand.Execute(library.Items.First(i => i.Title == "Tavern Map"));
        viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.Mode == ViewerMode.Problem, () => $"A missing file didn't show a problem (it shows {viewer.Mode}).");
        navigation.GoBack();
        await Settle(window);
    }

    /// <summary>
    /// Pops the PDF out of the main reader: the main window goes Back, and the pop-out, a top-level window with no
    /// owner, draws page 1 at full quality at the same zoom on a viewer worker of its own. Return to main window
    /// brings the book back at the same page and gives the worker back. Then the same book opens straight into two
    /// new windows from the Library: Ctrl+W's command closes one, and what closing the main window runs closes the rest.
    /// </summary>
    static async Task PopOutAsync(IServiceProvider services, Window window, long documentId)
    {
        const double Zoom = 1.25;
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var readers = services.GetRequiredService<ReaderWindows>();
        var workers = services.GetRequiredService<Bibliotaph.Pdf.Host.PdfWorkerPool>();
        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == documentId), () => "The smoke PDF isn't in the Library.");
        var book = library.Items.First(i => i.DocumentId == documentId);

        library.OpenBookCommand.Execute(book);
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, () => $"The PDF didn't open (still {viewer.Mode}).");
        if (!viewer.IsPdf) throw new InvalidOperationException($"The PDF didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        viewer.Zoom = Zoom;
        viewer.PageEntry = "1";
        viewer.GoToPageCommand.Execute(null);
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == 1, () => "Going to page 1 didn't show it before popping out.");

        await viewer.PopOutCommand.ExecuteAsync(null);
        if (shell.CurrentPage is not LibraryViewModel) throw new InvalidOperationException("Popping out didn't take the main window Back to the Library.");
        if (readers.Windows is not [var popOut]) throw new InvalidOperationException($"Pop out opened {readers.Windows.Count} windows, not one.");
        var reader = popOut.Model;
        if (popOut.Owner is not null || !popOut.ShowInTaskbar || popOut.Title != book.Title || !reader.IsPoppedOut || reader.Zoom != Zoom)
            throw new InvalidOperationException($"The pop-out is “{popOut.Title}” at {reader.Zoom}, owned: {popOut.Owner is not null}.");
        await WaitUntilAsync(popOut, () => reader.IsPdf || reader.Mode == ViewerMode.Problem, () => $"The pop-out's PDF didn't open (still {reader.Mode}).");
        if (!reader.IsPdf) throw new InvalidOperationException($"The pop-out's PDF didn't open: {reader.EmptyTitle} {reader.EmptyMessage}");
        var popPages = FindChild<Bibliotaph.Viewer.PdfPagesView>(popOut) ?? throw new InvalidOperationException("The pop-out has no page surface.");
        await WaitUntilAsync(popOut, () => popPages.CurrentPageIndex == 1 && popPages.PageModels[1] is { Image: not null, IsPreview: false },
            () => $"The pop-out didn't draw page 1 at full quality (page in view {popPages.CurrentPageIndex}).");
        // The main window's readers keep theirs; the pop-out has its own, so neither waits on the other.
        if (workers.ViewerWorkerCount != 2) throw new InvalidOperationException($"{workers.ViewerWorkerCount} viewer workers run with one pop-out, not 2.");

        reader.ReturnToMainWindowCommand.Execute(null);
        await WaitUntilAsync(window, () => readers.Windows.Count == 0 && shell.CurrentPage is ViewerViewModel { IsPdf: true },
            () => "Return to main window didn't close the pop-out and open the book in the main window.");
        var returned = (ViewerViewModel)shell.CurrentPage!;
        if (returned.IsPoppedOut || returned.Zoom != Zoom) throw new InvalidOperationException($"The returned book is at zoom {returned.Zoom}.");
        await WaitUntilAsync(window, () => FindChild<Bibliotaph.Viewer.PdfPagesView>(window) is { CurrentPageIndex: 1 } pages
                && pages.PageModels[1] is { Image: not null, IsPreview: false },
            () => "The returned book didn't draw page 1 in the main window.");
        await WaitUntilAsync(window, () => workers.ViewerWorkerCount == 1, () => "Closing the pop-out didn't stop its viewer worker.");
        navigation.GoBack();
        await Settle(window);
        if (!ReferenceEquals(shell.CurrentPage, library)) throw new InvalidOperationException("Back didn't return to the Library.");

        // The same book twice, straight from the Library, as from its card's menu.
        await library.OpenBookInNewWindowCommand.ExecuteAsync(book);
        await library.OpenBookInNewWindowCommand.ExecuteAsync(book);
        if (readers.Windows is not [var first, var second]) throw new InvalidOperationException($"{readers.Windows.Count} windows opened, not two.");
        await WaitUntilAsync(second, () => first.Model.IsPdf && second.Model.IsPdf, () => "The book didn't open in both new windows.");
        if (workers.ViewerWorkerCount != 3) throw new InvalidOperationException($"{workers.ViewerWorkerCount} viewer workers run with two pop-outs, not 3.");
        System.Windows.Input.ApplicationCommands.Close.Execute(null, first);
        await WaitUntilAsync(window, () => readers.Windows is [var left] && ReferenceEquals(left, second), () => "Ctrl+W's command didn't close the pop-out.");
        readers.CloseAll();
        await WaitUntilAsync(window, () => readers.Windows.Count == 0 && Application.Current.Windows.Count == 1 && workers.ViewerWorkerCount == 1,
            () => $"Closing every pop-out left {Application.Current.Windows.Count - 1} windows and {workers.ViewerWorkerCount} viewer workers.");
        Log.Information("Smoke test: a PDF popped out, returned, and opened in two new windows");
    }

    /// <summary>
    /// The About popup from the Settings link: the version with its commit, the PDFium build, and the licence reader
    /// showing the GPL and the third-party notices from the licenses folder.
    /// </summary>
    static async Task ShowAboutAsync(IServiceProvider services, Window window)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.NavigateTo(Route.Settings);
        await Settle(window);
        var settings = services.GetRequiredService<ShellViewModel>().CurrentPage as SettingsViewModel
            ?? throw new InvalidOperationException("Settings didn't open.");
        var link = Descendants<Button>(window).FirstOrDefault(b => b.Command == settings.ShowAboutCommand)
            ?? throw new InvalidOperationException("Settings has no About Bibliotaph link.");
        // ShowDialog returns only when the popup closes, so click from the queue and go on inside the popup's loop.
        _ = window.Dispatcher.BeginInvoke(() => ((IInvokeProvider)new ButtonAutomationPeer(link).GetPattern(PatternInterface.Invoke)).Invoke());
        Views.AboutDialog? about = null;
        await WaitUntilAsync(window, () => (about = Application.Current.Windows.OfType<Views.AboutDialog>().FirstOrDefault()) is { IsLoaded: true },
            () => "The About popup didn't open.");
        var info = about!.Info;
        if (!info.Version.StartsWith("0.", StringComparison.Ordinal) || info.Commit is not { Length: 7 })
            throw new InvalidOperationException($"About shows version {info.VersionText}, not 0.N.0 with a commit.");
        if (info.Pdfium is null) throw new InvalidOperationException("About couldn't find the PDFium build.");

        about.ShowLicence(Views.AboutDialog.LicenceFile);
        await Settle(about);
        if (!about.ShowingLicences || !about.LicenceTextShown.Contains("GNU GENERAL PUBLIC LICENSE", StringComparison.Ordinal))
            throw new InvalidOperationException("The licence reader didn't show the GPL.");
        about.ShowLicence(Views.AboutDialog.NoticesFile);
        await Settle(about);
        if (!about.LicenceTextShown.StartsWith("# Third-party notices", StringComparison.Ordinal))
            throw new InvalidOperationException("The licence reader didn't show the third-party notices.");

        about.Close();
        await WaitUntilAsync(window, () => !Application.Current.Windows.OfType<Views.AboutDialog>().Any(), () => "The About popup didn't close.");
        navigation.GoBack();
        await Settle(window);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Library folders with a library in it: the step-by-step progress and a card per folder.</summary>
    static async Task ShowFolderProgressAsync(IServiceProvider services, Window window)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.NavigateTo(Route.LibraryFolders);
        var page = services.GetRequiredService<ShellViewModel>().CurrentPage as LibraryFoldersViewModel
            ?? throw new InvalidOperationException("The Library folders route didn't open Library folders.");
        await WaitUntilAsync(window, () => page.Folders.Count > 0 && page.Folders.All(f => f.Detail.Length > 0),
            () => "Library folders didn't describe its folders.");
        if (page.Activity.Phases.Count != 4 || page.Activity.Phases.Any(p => p.Status.Length == 0))
            throw new InvalidOperationException("The indexing steps aren't all described.");
        await WaitUntilAsync(window, () => page.FolderLabels.Any(l => l is { Folder: "Adventures", IsEnabled: true }),
            () => "Library folders doesn't list the Adventures folder name.");
        // Library folders came from the Library, so the Settings breadcrumb opens a new Settings page.
        var shell = services.GetRequiredService<ShellViewModel>();
        shell.OpenSectionCommand.Execute(null);
        await Settle(window);
        if (shell.CurrentPage is not SettingsViewModel) throw new InvalidOperationException("The Settings breadcrumb didn't open Settings.");
        navigation.GoBack();
        await Settle(window);
        navigation.GoBack();
        await Settle(window);
    }

    /// <summary>Lets the window work until <paramref name="condition"/> holds, or fails after a few seconds.</summary>
    static async Task WaitUntilAsync(Window window, Func<bool> condition, Func<string> failure)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (true)
        {
            await Settle(window);
            if (condition()) return;
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException(failure());
            await Task.Delay(50);
        }
    }

    /// <summary>WIC is only on Windows, so the cover codec is checked here rather than in the unit tests.</summary>
    static void CheckImageCodec()
    {
        var codec = new WpfImageCodec();
        var pixels = new byte[16 * 8 * 4];
        Array.Fill(pixels, (byte)200);
        using var jpeg = new System.IO.MemoryStream(codec.EncodeJpeg(pixels, 16, 8, 16 * 4));
        if (codec.ReadSize(jpeg) != (16, 8)) throw new InvalidOperationException("A JPEG cover did not read back at 16x8.");
        jpeg.Position = 0;
        using var thumbnail = new System.IO.MemoryStream(codec.Thumbnail(jpeg, 4) ?? throw new InvalidOperationException("No thumbnail was made."));
        if (codec.ReadSize(thumbnail)?.Width != 4) throw new InvalidOperationException("The thumbnail is not 4 px wide.");
        if (codec.ReadSize(new System.IO.MemoryStream([1, 2, 3])) is not null) throw new InvalidOperationException("Garbage read as an image.");
        Log.Information("Smoke test: image codec works off the UI thread");
    }

    static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Errors.Add(message);
        }
    }
}
