using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// <c>Bibliotaph.exe --smoke-test --data-root &lt;folder&gt; [--smoke-files &lt;folder&gt;]</c>: opens the real window,
/// visits every route and every section of Settings in light and dark, fills a small made-up library and browses and
/// searches it in both themes, then exits 0, or 1 on any exception or binding error. With <c>--smoke-files</c>
/// (tests/fixtures/smoke), it also opens a real PDF from a search hit and reads it, and opens an image. CI runs it on
/// Windows so a broken resource or template fails the build instead of the first launch. It is not a substitute for
/// looking.
/// </summary>
static class SmokeTest
{
    public static async Task<int> RunAsync(IServiceProvider services, Window window, string? smokeFiles)
    {
        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var navigation = services.GetRequiredService<INavigationService>();
        var failed = new List<string>();

        // A failed check is logged and the rest still run, so one CI round shows every broken check, not just the
        // first. The window is put back first: pop-outs and popups closed, navigation back at the start.
        async Task Check(string name, Func<Task> check)
        {
            try
            {
                await check();
                Log.Information("Smoke test: {Check}", name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Smoke test failed: {Check}", name);
                failed.Add(name);
                services.GetRequiredService<ReaderWindows>().CloseAll();
                foreach (var other in Application.Current.Windows.OfType<Window>().Where(w => w != window).ToList()) other.Close();
                while (navigation.GoBack()) await Settle(window);
            }
        }

        try
        {
            if (window.Icon is null) throw new InvalidOperationException("The main window has no icon.");
            // Covers are encoded on indexing threads, so check the codec off the UI thread.
            await Task.Run(CheckImageCodec);
            var theme = services.GetRequiredService<ThemeService>();
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                foreach (var route in Enum.GetValues<Route>())
                {
                    navigation.NavigateTo(route);
                    await Settle(window);
                    if (navigation.Current is SettingsViewModel settings) await ShowEachSectionAsync(window, settings);
                    Log.Information("Smoke test: {Route} rendered in {Theme}", route, preference);
                }
                while (navigation.GoBack()) await Settle(window);
            }

            await Check("the sidebar status opened Settings > Processing", () => OpenProcessingFromStatusAsync(services, window));

            // Every later check needs the library, so a failure here ends the run.
            await SeedLibraryAsync(services);
            (long Pdf, long Image)? real = smokeFiles is null ? null : await SeedRealFilesAsync(services, smokeFiles);
            var books = real is null ? 5 : 7;
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                await Check($"library browsed and searched in {preference}", () => BrowseLibraryAsync(services, window, books));
                await Check($"a book's copies in {preference}", () => ShowCopiesAsync(window, services, books));
                await Check($"a search built from the search guide in {preference}", () => UseSearchGuideAsync(services, window, books));
                await Check($"Needs review and the vocabulary worked through in {preference}",
                    () => ReviewAsync(services, window, decide: preference == ThemePreference.Light));
                await Check($"Settings worked through with a library in {preference}", () => ShowSettingsAsync(services, window));
                await Check($"the About popup in {preference}", () => ShowAboutAsync(services, window));
                await Check($"Settings > AI with no model server in {preference}", () => ShowAiSettingsAsync(services, window));
                if (real is not { } files) continue;
                await Check($"a PDF read in {preference}", () => ReadBookAsync(services, window, files.Pdf));
                await Check($"a PDF popped out in {preference}", () => PopOutAsync(services, window, files.Pdf));
                await Check($"an image viewed in {preference}", () => ViewImageAsync(services, window, files.Image));
            }
            await Check("a book marked a favorite, shown in Favorites, found by favorite:yes and unmarked", () => UseFavoritesAsync(services, window, books));
            await Check("a collection made with a sub-collection, books added and found in it, pinned, renamed, deleted and brought back",
                () => UseCollectionsAsync(services, window, books));
            await Check("a book owned elsewhere added, found by where it is owned, removed and brought back", () => AddElsewhereAsync(services, window, books));
            await Check("a download checked against the library, file by file, without adding it", () => CheckDownloadAsync(services, window, books, smokeFiles));
            await Check("the model pilot in Settings > AI and its blind review page", () => RunPilotAsync(services, window));
            if (real is { } reprocessed)
            {
                await Check("a PDF opened again where it was left, and listed on Home", () => ResumeBookAsync(services, window, reprocessed.Pdf));
                await Check("a PDF reprocessed from its file and found throughout", () => ReprocessBookAsync(services, window, reprocessed.Pdf));
                await Check("a PDF inside a ZIP indexed, read and shown in the inspector", () => ReadZippedBookAsync(services, window, smokeFiles!));
                await Check("a ZIP of images packed, stepped through, split, packed again and found by an image's name", () => UsePackAsync(services, window));
                await Check("a smaller ZIP of images proposed in Needs review, packed, undone and kept separate", () => ProposePackAsync(services, window));
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
        if (failed.Count > 0)
        {
            Log.Error("Smoke test failed {Count} checks: {Checks}", failed.Count, string.Join("; ", failed));
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
        var hashes = new Dictionary<long, string>();
        foreach (var file in files)
        {
            var book = books.Single(b => file.FullPath.EndsWith(Relative(b.Path), StringComparison.Ordinal));
            // Digits, so the made-up books never share a hash with the real fixtures (e and f).
            var hash = ContentHash.Parse(new string((char)('1' + Array.IndexOf(books, book)), ContentHash.HexLength));
            var (documentId, _) = await library.AttachHashAsync(file, hash) ?? throw new InvalidOperationException("A made-up file didn't attach.");
            documents.Add(documentId);
            hashes[documentId] = hash.Hex;
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
        var entries = await services.GetRequiredService<EntryStore>().GetEntriesAsync([lairs, gazetteer]);
        var (lairsEntry, gazetteerEntry) = (entries[lairs].EntryId, entries[gazetteer].EntryId);
        await metadata.AddSuggestionsAsync(lairsEntry, hashes[lairs],
            [new MetadataProposal(MetadataFields.Types, "rulebook", AssertionOrigin.Ai, "the rules for lairs", [1])]);
        var (heist, _) = await services.GetRequiredService<VocabularyStore>().ProposeTermAsync("type", "Heist kit");
        await metadata.AddSuggestionsAsync(gazetteerEntry, hashes[gazetteer],
            [new MetadataProposal(MetadataFields.Types, heist.Key, AssertionOrigin.Ai, "everything a heist needs")]);
        // A model has read the lairs, so its cover has the AI spark and the filter offers "Read by AI".
        await services.GetRequiredService<ClassificationStore>().RecordAsync(new RunRecord(lairsEntry, hashes[lairs], "ollama", "smoke-model",
            ClassifierPrompt.Version, ClassifierPrompt.SchemaVersion, [0], DateTime.UtcNow, ClassificationStore.Complete));
        await services.GetRequiredService<MetadataProjector>().ProjectAsync([lairsEntry, gazetteerEntry]);

        // A second file of the lairs in another folder, joined to its card as a copy, as Match joins one (F2).
        const string BackupPath = "Backup/Dragon Lairs.pdf";
        var (_, lairsTitle, lairsPages) = books.Single(b => b.Title == "Dragon Lairs");
        await library.ReconcileRootAsync(root.Id, [.. books.Select(b => b.Path).Append(BackupPath).Select(p => new ScannedFile(Relative(p), 1000, modified, false))]);
        var backupFile = (await library.NextUnhashedAsync(1, includeOnlineOnly: true)).Single();
        var backupHash = ContentHash.Parse(new string((char)('1' + books.Length), ContentHash.HexLength));
        var (backup, _) = await library.AttachHashAsync(backupFile, backupHash) ?? throw new InvalidOperationException("The made-up copy didn't attach.");
        await index.UpsertDocumentAsync(
            new DocRow
            {
                DocumentId = backup,
                ContentHash = backupHash.Hex,
                Format = SourceFormats.Pdf,
                DisplayTitle = lairsTitle,
                PageCount = lairsPages.Length,
                FolderHint = "Backup",
            },
            [.. lairsPages.Select((_, i) => new PageRow(i, (i + 1).ToString(CultureInfo.InvariantCulture), 612, 792))], []);
        await index.SetPageTextAsync(backup, [.. lairsPages.Select((text, i) => new PageTextRow(i, text, "pdf", 1, false))]);
        await hints.ApplyAsync(backup);
        var entryStore = services.GetRequiredService<EntryStore>();
        var backupEntry = (await entryStore.GetEntryAsync(backup))!.EntryId;
        // The user had given the two cards different years, so the joined card has a "Copies disagree" card.
        await metadata.SetValuesAsync(lairsEntry, MetadataFields.Year, ["2019"]);
        await metadata.SetValuesAsync(backupEntry, MetadataFields.Year, ["2020"]);
        if (await entryStore.JoinAsCopyAsync(lairs, backup) is null) throw new InvalidOperationException("The made-up copy didn't join the lairs.");
        await services.GetRequiredService<MetadataProjector>().ProjectAsync([lairsEntry, backupEntry]);

        // And a "new version?" card, as Match proposes one for a file that shares most of a book's pages.
        var scan = titles.Single(t => t.Title == "IMG_0042").Id;
        var inn = titles.Single(t => t.Title == "Haunted Inn").Id;
        if (!await services.GetRequiredService<VersionStore>().ProposeAsync(scan, inn, VersionEvidence.SharedPages, 1, 1))
            throw new InvalidOperationException("The made-up new version wasn't proposed.");
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

            // Copies that disagree (F2): keep this card's year, then undo, so the card is drawn again in Dark.
            var years = page.Cards.OfType<ReviewCardViewModel>().FirstOrDefault(c => c is { Kind: ReviewKind.CopiesDisagree, Title: "Dragon Lairs" })
                ?? throw new InvalidOperationException("The lairs' two years aren't a Copies disagree card.");
            if (years is not { CurrentText: "2019", ProposedText: "2020", CurrentLabel: "This card", SuggestedLabel: "The other copy" })
                throw new InvalidOperationException($"The Copies disagree card shows {years.CurrentText} against {years.ProposedText}.");
            await Settle(window);
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == years.RejectCommand), "Keep this card's");
            await WaitUntilAsync(window, () => years.IsDone, () => "Keep this card's didn't decide the Copies disagree card.");
            await years.UndoCommand.ExecuteAsync(null);

            // A new version (F2): make it current, then undo, which gives the scan its own card back.
            var version = page.Cards.OfType<VersionCardViewModel>().FirstOrDefault()
                ?? throw new InvalidOperationException("The made-up new version isn't a card.");
            // Whichever of the two files is newer is the one proposed, so the card names either book.
            if (!version.Heading.StartsWith("Looks like a new version of ", StringComparison.Ordinal) || version.Item.Version.Evidence != VersionEvidence.SharedPages)
                throw new InvalidOperationException($"The new version card says {version.Heading} ({version.Evidence}).");
            await Settle(window);
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == version.MakeCurrentCommand), "Make it current");
            await WaitUntilAsync(window, () => version.IsDone, () => "Make it current didn't decide the new version card.");
            await version.UndoCommand.ExecuteAsync(null);
            if (version.IsDone) throw new InvalidOperationException("Undo didn't bring the new version card back.");
        }
        page.IsFilesTab = true;
        await Settle(window);
        page.IsSuggestionsTab = true;
        await Settle(window);

        var vocabulary = (VocabularyViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Vocabulary)).Selected;
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
        await BulkEditAsync(window, page, search, books);

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
        Click(clear, "clear search");
        await WaitUntilAsync(window, () => box.Text.Length == 0 && !search.IsSearching && clear.Visibility == Visibility.Collapsed,
            () => "The search box's × didn't clear the search.");
        page.Tab = ResultsTab.Documents;
        page.Layout = LibraryLayout.Grid;
        page.ShowFilters = false;
        await WaitUntilAsync(window, () => !page.IsSearching && page.Items.Count == books, () => $"Clearing the search didn't bring back all {books} books.");
    }

    /// <summary>
    /// A book in two files (F2): "2 copies" in the list, the Copies filter, and the inspector's Copies section, where
    /// Make current switches which file opens and switches it back.
    /// </summary>
    static async Task ShowCopiesAsync(Window window, IServiceProvider services, int books)
    {
        services.GetRequiredService<INavigationService>().NavigateTo(Route.Library);
        var page = services.GetRequiredService<ShellViewModel>().CurrentPage as LibraryViewModel
            ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        page.Layout = LibraryLayout.List;
        page.ShowFilters = true;
        await WaitUntilAsync(window, () => page.ShowCopiesChoice && page.Items.Count == books,
            () => $"The Library shows {page.Items.Count} books and {(page.ShowCopiesChoice ? "the" : "no")} Copies filter.");

        page.CopiesChoice = page.CopiesChoices.Single(c => c.Value);
        await WaitUntilAsync(window, () => page.Items is [{ Title: "Dragon Lairs", CopiesLabel: "2 copies" }],
            () => $"Filtering by copies shows {string.Join(", ", page.Items.Select(i => $"{i.Title} ({i.CopiesLabel})"))}.");
        var lairs = page.Items[0];
        await page.OpenDetailsCommand.ExecuteAsync(lairs);
        await WaitUntilAsync(window, () => page.Inspector is { Copies.Count: 2 }, () => "The inspector doesn't list the lairs' two copies.");
        var first = page.Inspector!.Copies.Single(c => c.IsCurrent).DocumentId;

        foreach (var expected in new[] { "the backup", "the original" })
        {
            var inspector = page.Inspector!;
            var other = inspector.Copies.Single(c => !c.IsCurrent);
            await Settle(window);
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == inspector.MakeCurrentCommand && Equals(b.CommandParameter, other)), "Make current");
            await WaitUntilAsync(window, () => page.Inspector is { } next && next != inspector && next.Copies.Single(c => c.IsCurrent).DocumentId == other.DocumentId,
                () => $"Make current didn't make {expected} the copy that opens.");
        }
        if (page.Inspector!.Copies.Single(c => c.IsCurrent).DocumentId != first) throw new InvalidOperationException("The lairs don't open their first copy again.");

        page.CloseDetailsCommand.Execute(null);
        page.CopiesChoice = page.CopiesChoices[0];
        page.Layout = LibraryLayout.Grid;
        page.ShowFilters = false;
        await WaitUntilAsync(window, () => page.Items.Count == books, () => $"Clearing the Copies filter didn't bring back all {books} books.");
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

        await WaitUntilAsync(window, () => page.ShowAiChoice && page.Items.Any(i => i is { Title: "Dragon Lairs", IsAiRead: true }),
            () => "The book a model read isn't marked, or the filters don't offer the AI choice.");
        page.AiChoice = page.AiChoices.First(c => c.Value == AiFilter.Read);
        await WaitUntilAsync(window, () => page.Items is [{ Title: "Dragon Lairs" }], () => $"Filtering by Read by AI shows {page.Items.Count} books, not 1.");
        // Alone in the list, its row is surely drawn (a long list draws only the rows in view), so its spark should show.
        await WaitUntilAsync(window, () => Descendants<Border>(window).Any(b => b.Name == "AiBadge" && b.IsVisible),
            () => $"The book read by AI has no spark on its cover ({Descendants<Border>(window).Count(b => b.Name == "AiBadge")} badges drawn, none visible).");
        page.ClearFiltersCommand.Execute(null);
        await WaitUntilAsync(window, () => page.Items.Count == books, () => $"Clearing the AI filter didn't bring back all {books} books.");

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
    /// Select mode and bulk editing (slice 4e): Ctrl+A and Clear, then two made-up books ticked by clicking them in the
    /// list, drawn ticked in the covers too, and still ticked through a search that doesn't show them. Edit metadata
    /// files a name the vocabulary doesn't know as a new type (dropped again), adds a tag to both and shows the summary;
    /// applying finds both by tag:, and Undo finds neither.
    /// </summary>
    static async Task BulkEditAsync(Window window, LibraryViewModel page, SearchState search, int books)
    {
        var gazetteer = page.Items.First(i => i.Title == "Gazetteer of the Marches");
        var lairs = page.Items.First(i => i.Title == "Dragon Lairs");
        page.StartSelectingCommand.Execute(null);
        await Settle(window);
        page.SelectAllCommand.Execute(null);
        if (!page.Selection.IsActive || page.Selection.Count != books) throw new InvalidOperationException($"Ctrl+A ticked {page.Selection.Count} books, not {books}.");
        page.ClearSelectionCommand.Execute(null);
        await page.ActivateCommand.ExecuteAsync(gazetteer);
        await page.ActivateCommand.ExecuteAsync(lairs);
        if (page.Inspector is not null || page.Selection.Count != 2 || !gazetteer.IsSelected || !lairs.IsSelected)
            throw new InvalidOperationException($"Clicking two books in Select mode ticked {page.Selection.Count} (details open: {page.Inspector is not null}).");
        page.Layout = LibraryLayout.Grid;
        await Settle(window);
        page.Layout = LibraryLayout.List;
        await Settle(window);

        // Gathered across searches: the ticks stay while the results don't show them.
        search.Search("system:5e type:adventure");
        await WaitUntilAsync(window, () => page.Items is [{ Title: "Haunted Inn" }] && page.Selection is { Count: 2, NotShown: 2 },
            () => $"Searching while two books were ticked left {page.Selection.Summary}.");

        await page.EditSelectedCommand.ExecuteAsync(null);
        var bulk = page.BulkEdit ?? throw new InvalidOperationException("Edit metadata didn't open the bulk editor.");
        await Settle(window);
        if (bulk.BookCount != 2 || bulk.Fields.Any(f => f.Field == MetadataFields.Title))
            throw new InvalidOperationException($"The bulk editor edits {bulk.BookCount} books, or offers the title.");
        var types = bulk.Fields.OfType<BulkChipsFieldViewModel>().Single(f => f.Field == MetadataFields.Types);
        types.AddText = "Heist almanac";
        types.AddTypedCommand.Execute(null);
        var fresh = types.Chips.SingleOrDefault(c => c.IsNew) ?? throw new InvalidOperationException("A type the vocabulary doesn't know wasn't shown as new.");
        if (fresh is not { NewNote: "new type", IsAdding: true }) throw new InvalidOperationException($"The new type's chip says {fresh.NewNote}, {fresh.CountText}.");
        await Settle(window);
        fresh.RemoveCommand.Execute(null);
        if (types.HasChange) throw new InvalidOperationException("Dropping the new type left a change to Type.");
        var tags = bulk.Fields.OfType<BulkChipsFieldViewModel>().Single(f => f.Field == MetadataFields.Tags);
        tags.AddText = "Shelved";
        tags.AddTypedCommand.Execute(null);
        if (!bulk.ReviewCommand.CanExecute(null)) throw new InvalidOperationException("Adding a tag didn't let the edit be reviewed.");
        bulk.ReviewCommand.Execute(null);
        await Settle(window);
        if (bulk.Summary is not [{ Field: "Your tags", HasWarning: false } line] || !line.Text.Contains("2 books", StringComparison.Ordinal))
            throw new InvalidOperationException($"The summary says {string.Join("; ", bulk.Summary.Select(l => $"{l.Field}: {l.Text}"))}.");

        await bulk.ApplyCommand.ExecuteAsync(null);
        await WaitUntilAsync(window, () => page.BulkEdit is null && page.HasBulkUndo, () => $"Applying the bulk edit didn't finish ({page.BulkMessage}).");
        if (page.BulkMessage != "Edited 2 books.") throw new InvalidOperationException($"The bulk edit says {page.BulkMessage}");
        search.Search("tag:shelved");
        await WaitUntilAsync(window, () => page.Items.Select(i => i.Title).Order(StringComparer.Ordinal).SequenceEqual(["Dragon Lairs", "Gazetteer of the Marches"]),
            () => $"Searching by the new tag found {string.Join(", ", page.Items.Select(i => i.Title))}.");

        await page.UndoBulkEditCommand.ExecuteAsync(null);
        await WaitUntilAsync(window, () => page.BulkMessage is null && !page.HasBulkUndo && page.IsEmpty,
            () => $"After Undo, the tag still finds {page.Items.Count} books ({page.BulkMessage}).");
        search.Search("");
        page.StopSelectingCommand.Execute(null);
        await WaitUntilAsync(window, () => !page.Selection.IsActive && !gazetteer.IsSelected && page.Items.Count == books,
            () => $"Done didn't leave Select mode with all {books} books showing.");
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
        // Indexing is paused, so their cards are listed here, as Probe would list them.
        var entries = await services.GetRequiredService<EntryStore>().GetEntriesAsync([pdf, image]);
        await services.GetRequiredService<MetadataProjector>().ProjectAsync([.. entries.Values.Select(e => e.EntryId)]);
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
        NavigationCommands.IncreaseZoom.Execute(null, pages);
        var zoomedIn = viewer.Zoom;
        if (zoomedIn <= 0) throw new InvalidOperationException($"Zoom in from Fit width left the zoom at {zoomedIn}.");
        NavigationCommands.DecreaseZoom.Execute(null, pages);
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
    /// Reprocess from the inspector, on the smoke PDF whose index rows were written by hand: indexing resumes and runs
    /// it through every stage from the real file. It must stay findable by "dragon" all along, and afterwards also by
    /// "midnight", which only the file's text has.
    /// </summary>
    static async Task ReprocessBookAsync(IServiceProvider services, Window window, long documentId)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var search = services.GetRequiredService<SearchState>();
        var queries = services.GetRequiredService<LibraryQueries>();
        var libraryStore = services.GetRequiredService<LibraryStore>();
        async Task<bool> FoundAsync(string words)
        {
            var filter = new LibraryFilter(await libraryStore.GetVisibleEntryIdsAsync());
            var hits = await Task.Run(() => queries.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse(words)), filter));
            return hits.Entries.Any(d => d.Entry.DocumentId == documentId);
        }
        if (await FoundAsync("midnight")) throw new InvalidOperationException("The smoke PDF was found by a word only its file has before it was reprocessed.");

        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == documentId), () => "The smoke PDF isn't in the Library.");
        await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == documentId));
        await Settle(window);
        var inspector = library.Inspector ?? throw new InvalidOperationException("The inspector didn't open.");
        if (!inspector.ReprocessCommand.CanExecute(null) || !inspector.CanOcrEveryPage || inspector.ReprocessLabel != "Reprocess")
            throw new InvalidOperationException($"Reprocess isn't offered (it says {inspector.ReprocessLabel}).");

        var indexing = services.GetRequiredService<IndexingService>();
        indexing.Resume(Lane.Index);
        indexing.Resume(Lane.Ocr);
        await inspector.ReprocessCommand.ExecuteAsync(null);

        // Polled, not waited for: the PDF worker starts and every stage runs, which can take a while on a slow machine.
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        while (inspector.IsReprocessing || inspector.Stages.Count < 4)
        {
            if (!await FoundAsync("dragon")) throw new InvalidOperationException("The smoke PDF dropped out of search while it was reprocessed.");
            if (Stopwatch.GetTimestamp() > deadline)
                throw new InvalidOperationException($"Reprocessing didn't finish: {string.Join("; ", inspector.Stages.Select(s => $"{s.Name} {s.Status}"))}.");
            await Settle(window);
            await Task.Delay(100);
        }
        if (inspector.Stages.FirstOrDefault(s => s.Name is "Opening" or "Reading text" && s.Status != "Done") is { } stuck)
            throw new InvalidOperationException($"Reprocessing ended with {stuck.Name}: {stuck.Status}.");
        if (!inspector.ReprocessCommand.CanExecute(null)) throw new InvalidOperationException("Reprocess stayed disabled after every stage finished.");
        if (!await FoundAsync("dragon") || !await FoundAsync("midnight"))
            throw new InvalidOperationException("After reprocessing, the smoke PDF isn't found by the words on its page.");

        // And through the search box, as a person would look.
        library.CloseDetailsCommand.Execute(null);
        search.Search("midnight");
        library.Tab = ResultsTab.Pages;
        await WaitUntilAsync(window, () => library.Hits.Any(h => h.Item.DocumentId == documentId),
            () => "Searching for midnight didn't find the reprocessed PDF's page.");
        search.Search("");
        library.Tab = ResultsTab.Documents;
        await Settle(window);
    }

    /// <summary>
    /// A library folder holding a ZIP with the smoke PDF inside (its bytes changed, so it is a book of its own): with
    /// indexing running since Reprocess, the real pipeline reads it from the ZIP, the reader opens it from the extract
    /// cache, and the inspector says it is inside a ZIP. Nothing is written beside the ZIP.
    /// </summary>
    static async Task ReadZippedBookAsync(IServiceProvider services, Window window, string smokeFiles)
    {
        const string Title = "Smoke Zipped Book";
        // Left for the system's temp cleaning: the app may not delete folders.
        var folder = System.IO.Directory.CreateTempSubdirectory("bibliotaph-smoke-zip-").FullName;
        var zipPath = System.IO.Path.Combine(folder, "Smoke Bundle.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        await using (var member = zip.CreateEntry($"Smoke Bundle/{Title}.pdf").Open())
        {
            await member.WriteAsync(await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(smokeFiles, "smoke-book.pdf")));
            await member.WriteAsync("\n% a zipped copy\n"u8.ToArray());
        }
        var root = await services.GetRequiredService<SourceRootStore>().AddAsync(folder);
        services.GetRequiredService<IndexingService>().RequestScan(root.Id);

        var queries = services.GetRequiredService<LibraryQueries>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        LibraryEntry? book = null;
        while ((book = (await Task.Run(() => queries.ListAsync(new LibraryFilter()))).FirstOrDefault(e => e.Title == Title)) is not { Searchable: true })
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"The PDF inside the ZIP wasn't indexed ({(book is null ? "no card" : "not searchable")}).");
            await Settle(window);
            await Task.Delay(200);
        }

        var shell = services.GetRequiredService<ShellViewModel>();
        services.GetRequiredService<ReaderWindows>().OpenInMainWindow(new ViewerRequest(book.DocumentId, Title, 0));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The zipped PDF didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, () => $"The zipped PDF didn't open (still {viewer.Mode}).");
        if (!viewer.IsPdf) throw new InvalidOperationException($"The zipped PDF didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        var pages = FindChild<Bibliotaph.Viewer.PdfPagesView>(window) ?? throw new InvalidOperationException("The viewer has no page surface.");
        await WaitUntilAsync(window, () => pages.PageModels.Count == 2 && pages.PageModels[0] is { Image: not null },
            () => "The zipped PDF's first page wasn't drawn.");
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.GoBack();
        await Settle(window);

        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == book.DocumentId), () => "The zipped PDF isn't in the Library.");
        await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == book.DocumentId));
        await WaitUntilAsync(window, () => library.Inspector is { Locations.Count: > 0 }, () => "The inspector didn't list where the zipped PDF is.");
        if (library.Inspector!.Locations[0] is not { State: "Inside a ZIP" } location || location.ExplorerPath != zipPath)
            throw new InvalidOperationException($"The inspector shows {library.Inspector.Locations[0]} for the zipped PDF.");
        library.CloseDetailsCommand.Execute(null);
        await Settle(window);

        var files = System.IO.Directory.GetFiles(folder, "*", System.IO.SearchOption.AllDirectories);
        if (files is not [var only] || only != zipPath) throw new InvalidOperationException($"The ZIP's folder holds {files.Length} files, not just the ZIP.");
    }

    /// <summary>
    /// Image packs (F4a): twenty token images in a ZIP become one card with a mosaic; its inspector lists them,
    /// one opens in the viewer, which steps to the next; Split gives them their cards back and Undo packs them again;
    /// searching for one image's name finds the pack, which opens at that image (F4b).
    /// </summary>
    static async Task UsePackAsync(IServiceProvider services, Window window)
    {
        const string Name = "Smoke Tokens";
        // Left for the system's temp cleaning: the app may not delete folders.
        // In a ZIP, which counts as one folder however it is laid out, since the app may only write ZIPs.
        var folder = System.IO.Directory.CreateTempSubdirectory("bibliotaph-smoke-pack-").FullName;
        var tokens = System.IO.Path.Combine(folder, $"{Name}.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(tokens, System.IO.Compression.ZipArchiveMode.Create))
            for (var i = 1; i <= PackStore.AutomaticMinimum; i++)
            {
                await using var member = zip.CreateEntry($"{(i % 2 == 0 ? "Even" : "Odd")}/Token {i}.png").Open();
                await member.WriteAsync(TokenPng(i));
            }
        var root = await services.GetRequiredService<SourceRootStore>().AddAsync(folder);
        services.GetRequiredService<IndexingService>().RequestScan(root.Id);

        var queries = services.GetRequiredService<LibraryQueries>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        LibraryEntry? pack = null;
        while ((pack = (await Task.Run(() => queries.ListAsync(new LibraryFilter(Kind: KindFilter.Packs)))).FirstOrDefault(e => e.Title == Name))
            is not { Members: PackStore.AutomaticMinimum, MosaicCovers.Count: 4 })
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new InvalidOperationException($"The ZIP of images wasn't packed ({(pack is null ? "no pack card" : $"{pack.Members} images, {pack.MosaicCovers.Count} covers")}).");
            await Settle(window);
            await Task.Delay(200);
        }

        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        // The Library may already have been open, listed before the covers were all made: list it again.
        await library.RefreshAsync();
        await WaitUntilAsync(window, () => library.Items.Any(i => i.EntryId == pack.EntryId && i is { IsPack: true, SizeLabel: "20 images", Mosaic.Count: 4 }),
            () => library.Items.FirstOrDefault(i => i.EntryId == pack.EntryId) is { } shown
                ? $"The pack's card shows {shown.SizeLabel} and {shown.Mosaic.Count} covers."
                : "The pack isn't in the Library.");
        var card = library.Items.First(i => i.EntryId == pack.EntryId);
        if (library.Items.Any(i => i.Folder.Contains(Name, StringComparison.Ordinal) && !i.IsPack)) throw new InvalidOperationException("A packed image still has a card of its own.");

        await library.OpenDetailsCommand.ExecuteAsync(card);
        await WaitUntilAsync(window, () => library.Inspector is { IsPack: true, PackImages.Count: PackStore.AutomaticMinimum },
            () => $"The pack's inspector didn't list its images ({library.Inspector?.PackImages.Count}).");
        var inspector = library.Inspector!;
        if (inspector.Locations is not [{ Path: var where }] || where != tokens) throw new InvalidOperationException($"The pack's inspector says it is in {string.Join(", ", inspector.Locations)}.");
        var second = inspector.PackImages[1];
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == library.OpenPackImageCommand && b.CommandParameter == second), "Token 2");
        await WaitUntilAsync(window, () => shell.CurrentPage is ViewerViewModel { IsImage: true }, () => "The pack's image didn't open in the viewer.");
        var viewer = (ViewerViewModel)shell.CurrentPage!;
        if (viewer.PackPosition != "2 of 20" || viewer.Title != "Token 2") throw new InvalidOperationException($"The viewer shows {viewer.Title}, {viewer.PackPosition}.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == viewer.NextImageCommand), "Next image");
        await WaitUntilAsync(window, () => viewer is { PackPosition: "3 of 20", IsImage: true, Image: not null }, () => $"Next image didn't step ({viewer.PackPosition}, {viewer.Mode}).");
        navigation.GoBack();
        await Settle(window);

        library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("Back didn't return to the Library.");
        if (library.Inspector is not { IsPack: true }) await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.EntryId == pack.EntryId));
        await WaitUntilAsync(window, () => library.Inspector is { IsPack: true }, () => "The pack's inspector didn't open again.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == library.Inspector!.SplitPackCommand), "Split into separate images");
        await WaitUntilAsync(window, () => library.HasSplitUndo && library.Items.Count(i => i.Folder.Contains(Name, StringComparison.Ordinal)) == PackStore.AutomaticMinimum
            && library.Items.All(i => i.EntryId != pack.EntryId), () => "Split didn't give the images their cards back.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == library.UndoSplitCommand), "Undo the split");
        await WaitUntilAsync(window, () => !library.HasSplitUndo && library.Items.Any(i => i.EntryId == pack.EntryId)
            && !library.Items.Any(i => i.Folder.Contains(Name, StringComparison.Ordinal) && !i.IsPack), () => "Undo didn't pack the images again.");

        var search = services.GetRequiredService<SearchState>();
        search.Search("\"Token 7\"");
        await WaitUntilAsync(window, () => library.IsSearching && library.Items.Any(i => i.EntryId == pack.EntryId && i.Meta == "Token 7.png"),
            () => $"Searching for Token 7 found {string.Join(", ", library.Items.Select(i => $"{i.Title} ({i.Meta})"))}.");
        await library.OpenBookCommand.ExecuteAsync(library.Items.First(i => i.EntryId == pack.EntryId));
        await WaitUntilAsync(window, () => shell.CurrentPage is ViewerViewModel { IsImage: true, PackPosition: "7 of 20" },
            () => "Opening the pack found by Token 7 didn't open that image.");
        navigation.GoBack();
        await Settle(window);
        search.Search("");
        await Settle(window);
    }

    /// <summary>
    /// Pack proposals (F4b): six images in a ZIP are too few to pack by themselves, so Needs review asks. Make a pack
    /// makes them one card, Undo asks again, and Keep separate answers for good.
    /// </summary>
    static async Task ProposePackAsync(IServiceProvider services, Window window)
    {
        const string Name = "Smoke Handouts";
        // Left for the system's temp cleaning, as the tokens' ZIP is.
        var folder = System.IO.Directory.CreateTempSubdirectory("bibliotaph-smoke-proposal-").FullName;
        using (var zip = System.IO.Compression.ZipFile.Open(System.IO.Path.Combine(folder, $"{Name}.zip"), System.IO.Compression.ZipArchiveMode.Create))
            for (var i = 1; i <= 6; i++)
            {
                await using var member = zip.CreateEntry($"Handout {i}.png").Open();
                await member.WriteAsync(TokenPng(PackStore.AutomaticMinimum + i));
            }
        var root = await services.GetRequiredService<SourceRootStore>().AddAsync(folder);
        services.GetRequiredService<IndexingService>().RequestScan(root.Id);

        var packs = services.GetRequiredService<PackService>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        while (!(await packs.GetProposalsAsync()).Any(p => p.Name == Name))
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException("The ZIP of six images wasn't proposed as a pack.");
            await Settle(window);
            await Task.Delay(200);
        }

        var shell = services.GetRequiredService<ShellViewModel>();
        services.GetRequiredService<INavigationService>().NavigateTo(Route.NeedsReview);
        var page = shell.CurrentPage as NeedsReviewViewModel ?? throw new InvalidOperationException("The Needs review route didn't open Needs review.");
        await page.ReloadCommand.ExecuteAsync(null);
        var card = page.Cards.OfType<PackCardViewModel>().FirstOrDefault(c => c.Title == Name)
            ?? throw new InvalidOperationException("The proposal isn't a card in Needs review.");
        if (card.Heading != "Make these 6 images one card?") throw new InvalidOperationException($"The proposal asks \"{card.Heading}\".");
        await Settle(window);

        var queries = services.GetRequiredService<LibraryQueries>();
        async Task<LibraryEntry?> PackAsync() =>
            (await Task.Run(() => queries.ListAsync(new LibraryFilter(Kind: KindFilter.Packs)))).FirstOrDefault(e => e.Title == Name);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == card.MakePackCommand), "Make a pack");
        await WaitUntilAsync(window, () => card.IsDone, () => "Make a pack didn't decide the card.");
        if (await PackAsync() is not { Members: 6 }) throw new InvalidOperationException("Make a pack didn't make the six images one card.");
        await card.UndoCommand.ExecuteAsync(null);
        if (card.IsDone || await PackAsync() is not null) throw new InvalidOperationException("Undo didn't take the pack apart and ask again.");
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == card.KeepSeparateCommand), "Keep separate");
        await WaitUntilAsync(window, () => card.IsDone, () => "Keep separate didn't decide the card.");
        if ((await packs.GetProposalsAsync()).Any(p => p.Name == Name)) throw new InvalidOperationException("Keep separate left the proposal waiting.");
    }

    /// <summary>
    /// Owned elsewhere (F5a): the Library's "Add a book I own elsewhere" dialog makes a card with no file, whose
    /// inspector opens with Also own first and nothing to open; own:print finds it; Remove takes it away and Undo brings
    /// it back. The book is removed again at the end, so later checks see the library as it was.
    /// </summary>
    /// <summary>
    /// Slice 3a: the details' heart marks a book; its cover shows the heart; the sidebar's Favorites opens the Library
    /// scoped to it, with a chip whose × shows the whole library; favorite:yes finds it; Home offers the way in; and the
    /// card menu's command unmarks it, leaving Favorites empty.
    /// </summary>
    static async Task UseFavoritesAsync(IServiceProvider services, Window window, int books)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var search = services.GetRequiredService<SearchState>();
        shell.NavigateCommand.Execute(Route.Library);
        var page = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        await WaitUntilAsync(window, () => page.Items.Count == books, () => $"The Library shows {page.Items.Count} books, not {books}.");
        var book = page.Items.First(i => !i.IsFavorite);

        await page.OpenDetailsCommand.ExecuteAsync(book);
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "FavoriteBook"), "details' heart");
        await WaitUntilAsync(window, () => book.IsFavorite && page.Items.Single(i => i.EntryId == book.EntryId).Entry.Favorite,
            () => "The details' heart didn't mark the book as a favorite in the index.");
        page.CloseDetailsCommand.Execute(null);
        await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Name == "Heart" && b.IsVisible && ReferenceEquals(b.CommandParameter, book)),
            () => "The favorite's cover doesn't show its heart.");

        var favorites = Descendants<ItemsControl>(window).FirstOrDefault(i => i.Name == "SmartViews");
        Click(favorites is null ? null : Descendants<Button>(favorites).FirstOrDefault(), "Favorites in the sidebar");
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { Scope: not null, Items: [var only] } && only.EntryId == book.EntryId,
            () => "Favorites in the sidebar didn't open the Library with just the favorite.");
        var scoped = (LibraryViewModel)shell.CurrentPage!;
        if (scoped.Heading != "Favorites" || !shell.SmartViews[0].IsActive || shell.NavItems.Single(n => n.Route == Route.Library).IsActive)
            throw new InvalidOperationException($"Favorites reads “{scoped.Heading}”, and the sidebar doesn't mark Favorites as the page shown.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ClearScope"), "scope chip's ×");
        await WaitUntilAsync(window, () => !scoped.HasScope && scoped.Items.Count == books && shell.NavItems.Single(n => n.Route == Route.Library).IsActive,
            () => "The scope chip's × didn't show the whole library.");

        search.Search("favorite:yes");
        await WaitUntilAsync(window, () => scoped.IsSearching && scoped.Items is [var found] && found.EntryId == book.EntryId,
            () => "favorite:yes didn't find just the favorite.");
        search.Search("");
        await WaitUntilAsync(window, () => !scoped.IsSearching && scoped.Items.Count == books, () => "Clearing the search didn't bring the library back.");

        shell.NavigateCommand.Execute(Route.Home);
        var home = shell.CurrentPage as HomeViewModel ?? throw new InvalidOperationException("Home didn't open.");
        await WaitUntilAsync(window, () => home.HasBooks && home.HasFavorites && home.RecentlyAdded.Count > 0, () => "Home doesn't show the library and Favorites.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "OpenFavorites"), "Home's Your favorites");
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { Scope: not null, Items.Count: 1 }, () => "Your favorites on Home didn't open Favorites.");
        var fromHome = (LibraryViewModel)shell.CurrentPage!;

        // The card menu's command, as right-click > Remove from favorites runs it.
        BookCommands.ToggleFavorite.Execute(fromHome.Items[0], FindChild<Views.LibraryView>(window));
        await WaitUntilAsync(window, () => fromHome.IsEmpty && fromHome.EmptyTitle == LibraryScope.Favorites.EmptyTitle,
            () => "Unmarking the only favorite didn't leave Favorites empty.");
        shell.NavigateCommand.Execute(Route.Library);
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { Scope: null } whole && whole.Items.Count == books && whole.Items.All(i => !i.IsFavorite),
            () => "The library didn't go back to how it was.");
    }

    /// <summary>
    /// Slice 3b: a collection made on Collections, with a Maps collection inside it; books added from Select mode and
    /// from a card's menu, with Undo; the collection's page showing its books and its sub-collection's, and only its
    /// own with the switch; collection:"name"; the details' chips; Pin (on Home) and Rename; and Delete with Undo.
    /// </summary>
    static async Task UseCollectionsAsync(IServiceProvider services, Window window, int books)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var search = services.GetRequiredService<SearchState>();
        var collections = services.GetRequiredService<CollectionsService>();
        shell.NavigateCommand.Execute(Route.Collections);
        var page = shell.CurrentPage as CollectionsViewModel ?? throw new InvalidOperationException("The Collections route didn't open Collections.");
        await WaitUntilAsync(window, () => page.IsLoaded && !page.HasCollections, () => "Collections didn't open empty.");

        // New collection: named in the dialog, then opened, empty.
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "NewCollection"), "New collection");
        await WaitUntilAsync(window, () => page.Actions.Dialog is { IsNaming: true }, () => "New collection didn't ask for a name.");
        page.Actions.Dialog!.Name = "Winter campaign";
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "SaveButton" && b.IsVisible), "Create");
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { IsCollection: true, IsEmpty: true },
            () => "Create didn't open the new collection, empty.");
        var campaign = (LibraryViewModel)shell.CurrentPage!;
        if (campaign.Heading != "Winter campaign" || !shell.NavItems.Single(n => n.Route == Route.Collections).IsActive || shell.Section != "Collections")
            throw new InvalidOperationException($"The collection reads “{campaign.Heading}” under {shell.Section}, and the sidebar doesn't mark Collections.");
        var campaignId = campaign.Scope!.CollectionId!.Value;

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "NewSubCollection"), "the collection's New collection");
        await WaitUntilAsync(window, () => campaign.Collections.Dialog is { IsNaming: true }, () => "New collection in a collection didn't ask for a name.");
        campaign.Collections.Dialog!.Name = "Maps";
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "SaveButton" && b.IsVisible), "Create");
        await WaitUntilAsync(window, () => campaign.SubCollections is [{ Name: "Maps" }], () => "The new collection didn't show inside the first.");
        var mapsId = campaign.SubCollections[0].Id;

        // Select mode in the whole library: two books into Maps, through the selection's own menu.
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ClearScope"), "scope chip's ×");
        await WaitUntilAsync(window, () => !campaign.HasScope && campaign.Items.Count == books, () => "The scope chip's × didn't show the whole library.");
        campaign.StartSelectingCommand.Execute(null);
        campaign.Selection.Toggle(campaign.Items[0]);
        campaign.Selection.Toggle(campaign.Items[1]);
        await Settle(window);
        var addSelected = Descendants<Button>(window).FirstOrDefault(b => b.Name == "AddSelectedToCollection");
        Click(addSelected, "Select mode's Add to collection");
        await WaitUntilAsync(window, () => addSelected!.ContextMenu is { IsOpen: true, Items.Count: >= 2 }, () => "Add to collection didn't open its menu of collections.");
        addSelected!.ContextMenu!.IsOpen = false;
        var toMaps = CollectionMenu.Choices.Single(c => c.CollectionId == mapsId);
        campaign.AddSelectedToCollectionCommand.Execute(new CollectionRequest(campaign, toMaps));
        await WaitUntilAsync(window, () => campaign.Collections.Message == "Added 2 books to Maps.", () => $"Adding the selection said “{campaign.Collections.Message}”.");
        campaign.StopSelectingCommand.Execute(null);

        // A card's menu: a third book into the campaign itself, undone and done again.
        var third = campaign.Items[2];
        var toCampaign = CollectionMenu.Choices.Single(c => c.CollectionId == campaignId);
        BookCommands.AddToCollection.Execute(new CollectionRequest(third, toCampaign), FindChild<Views.LibraryView>(window));
        await WaitUntilAsync(window, () => campaign.Collections.HasUndo && campaign.Collections.Message!.StartsWith("Added ", StringComparison.Ordinal),
            () => $"The card menu's Add to collection said “{campaign.Collections.Message}”.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "UndoCollection"), "Undo");
        // Undo has finished once its note is gone and the collections have reloaded.
        await WaitUntilAsync(window, () => campaign.Collections is { Message: null, UndoCommand.IsRunning: false }, () => "Undo didn't finish.");
        campaign.AddToCollection(new CollectionRequest(third, toCampaign));
        await WaitUntilAsync(window, () => campaign.Collections.HasUndo, () => $"Adding the third book again said “{campaign.Collections.Message}”.");

        search.Search("collection:\"winter campaign\"");
        await WaitUntilAsync(window, () => campaign.IsSearching && campaign.Items.Count == 3, () => $"collection:\"winter campaign\" found {campaign.Items.Count} books, not 3.");
        search.Search("");
        await WaitUntilAsync(window, () => !campaign.IsSearching && campaign.Items.Count == books, () => "Clearing the search didn't bring the library back.");

        // The collection's card on Collections opens its page: its books and Maps', or only its own.
        shell.NavigateCommand.Execute(Route.Collections);
        page = shell.CurrentPage as CollectionsViewModel ?? throw new InvalidOperationException("Collections didn't open.");
        await WaitUntilAsync(window, () => page.Cards is [{ CountLabel: "3 books · 1 collection" }], () => "Collections doesn't show the campaign's card with its count.");
        var cards = Descendants<ItemsControl>(window).FirstOrDefault(i => i.Name == "CollectionCards");
        Click(cards is null ? null : Descendants<Button>(cards).FirstOrDefault(b => b.Name == "Card"), "the campaign's card");
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { IsCollection: true, Items.Count: 3, HasSubCollections: true },
            () => "The campaign's card didn't open its three books with Maps above them.");
        var opened = (LibraryViewModel)shell.CurrentPage!;
        opened.OnlyAddedHere = true;
        await WaitUntilAsync(window, () => opened.Items is [var own] && own.EntryId == third.EntryId, () => "Only books added here didn't leave just the third book.");
        opened.OnlyAddedHere = false;
        await WaitUntilAsync(window, () => opened.Items.Count == 3, () => "Turning the switch off didn't bring Maps' books back.");

        // The details list the collections the book was added to; a chip's × takes it out.
        await opened.OpenDetailsCommand.ExecuteAsync(opened.Items.Single(i => i.EntryId == third.EntryId));
        await WaitUntilAsync(window, () => opened.Inspector is { Collections: [{ Name: "Winter campaign" }] }, () => "The details don't list the book's collection.");
        var chips = Descendants<ItemsControl>(window).FirstOrDefault(i => i.Name == "BookCollections");
        Click(chips is null ? null : Descendants<Button>(chips).LastOrDefault(), "the chip's ×");
        await WaitUntilAsync(window, () => opened.Inspector is { HasCollections: false } && opened.Items.Count == 2, () => "The chip's × didn't take the book out.");
        opened.CloseDetailsCommand.Execute(null);

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "PinCollection"), "Pin");
        await WaitUntilAsync(window, () => opened.PinLabel == "Unpin", () => "Pin didn't pin the collection.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "RenameCollection"), "Rename");
        await WaitUntilAsync(window, () => opened.Collections.Dialog is { IsNaming: true, Name: "Winter campaign" }, () => "Rename didn't ask for the new name.");
        opened.Collections.Dialog!.Name = "Spring campaign";
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "SaveButton" && b.IsVisible), "Save");
        await WaitUntilAsync(window, () => opened.Heading == "Spring campaign" && shell.Title == "Spring campaign", () => $"Rename left the heading “{opened.Heading}”.");

        shell.NavigateCommand.Execute(Route.Home);
        var home = shell.CurrentPage as HomeViewModel ?? throw new InvalidOperationException("Home didn't open.");
        await WaitUntilAsync(window, () => home.PinnedCollections is [{ Name: "Spring campaign" }], () => "Home doesn't show the pinned collection.");
        var pinned = Descendants<ItemsControl>(window).FirstOrDefault(i => i.Name == "PinnedCollections");
        Click(pinned is null ? null : Descendants<Button>(pinned).FirstOrDefault(b => b.Name == "Card"), "the pinned collection on Home");
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { IsCollection: true, Heading: "Spring campaign" }, () => "The pinned card didn't open the collection.");

        // Delete: Collections again, with Maps moved up and Undo bringing the campaign back around it.
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "DeleteCollection"), "Delete");
        await WaitUntilAsync(window, () => shell.CurrentPage is CollectionsViewModel { Actions.HasUndo: true } c && c.Cards is [{ Name: "Maps" }],
            () => "Delete didn't go to Collections with Maps moved up and an Undo.");
        page = (CollectionsViewModel)shell.CurrentPage!;
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "UndoCollection"), "Undo");
        await WaitUntilAsync(window, () => page.Cards is [{ Name: "Spring campaign", CountLabel: "2 books · 1 collection" }], () => "Undo didn't bring the campaign back around Maps.");

        // Leave the library as it was for the checks after this one.
        await collections.DeleteAsync(mapsId);
        await collections.DeleteAsync(campaignId);
        await WaitUntilAsync(window, () => !page.HasCollections, () => "The smoke test's collections didn't go.");
    }

    /// <summary>
    /// Slice 3a: a PDF opened from the Library, left on its second page, opens there again next time; Home lists it
    /// under Recently opened; and a search hit still opens at its own page.
    /// </summary>
    static async Task ResumeBookAsync(IServiceProvider services, Window window, long documentId)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        var reading = services.GetRequiredService<ReadingService>();
        shell.NavigateCommand.Execute(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == documentId), () => "The smoke PDF isn't in the Library.");
        var book = library.Items.First(i => i.DocumentId == documentId);

        await library.OpenBookCommand.ExecuteAsync(book);
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, () => $"The PDF didn't open (still {viewer.Mode}).");
        if (!viewer.IsPdf) throw new InvalidOperationException($"The PDF didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        viewer.PageEntry = "i";
        viewer.GoToPageCommand.Execute(null);
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == 0, () => "Going to page i didn't show the first page.");
        viewer.PageEntry = "1";
        viewer.GoToPageCommand.Execute(null);
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == 1, () => "Going to page 1 didn't show it.");
        await WaitUntilAsync(window, () => viewer.ToggleFavoriteCommand.CanExecute(null), () => "The reader's heart isn't ready.");
        navigation.GoBack();
        await Settle(window);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await reading.GetPositionAsync(documentId) != 1)
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Leaving the book didn't keep its page.");
            await Task.Delay(100);
        }

        await library.OpenBookCommand.ExecuteAsync(book);
        var again = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("Open didn't open the viewer again.");
        await WaitUntilAsync(window, () => again.IsPdf && again.CurrentPageIndex == 1 && again.Notice is { } notice && notice.Contains("where you left off", StringComparison.Ordinal),
            () => $"The book opened at page index {again.CurrentPageIndex}, not where it was left ({again.Notice}).");
        navigation.GoBack();
        await Settle(window);

        shell.NavigateCommand.Execute(Route.Home);
        var home = shell.CurrentPage as HomeViewModel ?? throw new InvalidOperationException("Home didn't open.");
        await WaitUntilAsync(window, () => home.HasRecentlyOpened && home.RecentlyOpened[0].DocumentId == documentId,
            () => "Home doesn't list the book first under Recently opened.");
        var cover = Descendants<ItemsControl>(window).FirstOrDefault(i => i.Name == "RecentlyOpened") is { } row ? Descendants<Button>(row).FirstOrDefault(b => b.Name == "Card") : null;
        Click(cover, "Recently opened cover");
        await WaitUntilAsync(window, () => shell.CurrentPage is LibraryViewModel { Inspector.Item: var item } && item.DocumentId == documentId,
            () => "A cover on Home didn't open its details in the Library.");
        ((LibraryViewModel)shell.CurrentPage!).CloseDetailsCommand.Execute(null);
        navigation.GoBack();
        await Settle(window);
    }

    static async Task AddElsewhereAsync(IServiceProvider services, Window window, int books)
    {
        const string Title = "Smoke Atlas of the Marches";
        var search = services.GetRequiredService<SearchState>();
        services.GetRequiredService<INavigationService>().NavigateTo(Route.Library);
        var page = services.GetRequiredService<ShellViewModel>().CurrentPage as LibraryViewModel
            ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        await WaitUntilAsync(window, () => page.Items.Count == books, () => $"The Library shows {page.Items.Count} books, not {books}.");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == page.AddElsewhereCommand), "Add a book I own elsewhere");
        await WaitUntilAsync(window, () => page.AddElsewhereDialog is not null, () => "Add a book I own elsewhere didn't open its dialog.");
        var dialog = page.AddElsewhereDialog!;
        dialog.Title = Title;
        dialog.System = dialog.Systems.FirstOrDefault(s => s.Label == "D&D 5e") ?? throw new InvalidOperationException("The dialog doesn't offer D&D 5e.");
        (dialog.Owns.FirstOrDefault(o => o.Term.Key == "print") ?? throw new InvalidOperationException("The dialog doesn't offer Print.")).IsChecked = true;
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == dialog.SaveCommand), "Add to library");
        await WaitUntilAsync(window, () => page.AddElsewhereDialog is null && page.Inspector is { IsElsewhere: true },
            () => "Saving the book didn't open its inspector.");

        var inspector = page.Inspector!;
        if (inspector.Title != Title) throw new InvalidOperationException($"The inspector shows “{inspector.Title}”.");
        if (!inspector.Facts.Any(f => f.Value == "No file in your library")) throw new InvalidOperationException("The inspector doesn't say there's no file.");
        if (inspector.VisibleFields is not [{ Field: var first }, ..] || first != MetadataFields.AlsoOwn) throw new InvalidOperationException("Also own isn't the inspector's first field.");
        if (Descendants<Button>(window).Any(b => b.Name == "OpenBook" && b.IsVisible)) throw new InvalidOperationException("A book with no file offers Open.");
        await WaitUntilAsync(window, () => page.Items.Count == books + 1, () => "The book owned elsewhere isn't in the Library.");
        var card = page.Items.Single(i => i.Title == Title);
        if (!card.IsElsewhere || card.SizeLabel != "Print" || card.SystemLabel != "D&D 5e")
            throw new InvalidOperationException($"The card reads “{card.SizeLabel}”, “{card.SystemLabel}”.");
        if (page.OpenBookCommand.CanExecute(card)) throw new InvalidOperationException("A book with no file can be opened.");

        page.CloseDetailsCommand.Execute(null);
        search.Search("own:print");
        await WaitUntilAsync(window, () => page.Items is [{ Title: Title }], () => "own:print didn't find the book owned elsewhere.");
        search.Search("");
        await WaitUntilAsync(window, () => !page.IsSearching && page.Items.Count == books + 1, () => "Clearing the search didn't bring the library back.");

        await page.OpenDetailsCommand.ExecuteAsync(card);
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "RemoveFromLibrary"), "Remove from library");
        await WaitUntilAsync(window, () => page.HasRemoveUndo && page.Items.Count == books, () => "Remove from library didn't take the book away.");
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == page.UndoRemoveCommand), "Undo the removal");
        await WaitUntilAsync(window, () => page.Items.Any(i => i.Title == Title && i.IsElsewhere), () => "Undo didn't bring the book back.");

        var back = page.Items.Single(i => i.Title == Title);
        if (await services.GetRequiredService<ElsewhereService>().RemoveAsync(back.EntryId) is null)
            throw new InvalidOperationException("The book owned elsewhere couldn't be removed again.");
        await page.RefreshAsync();
        await WaitUntilAsync(window, () => page.Items.Count == books, () => "The library didn't go back to how it was.");
    }

    /// <summary>
    /// Check a download from the Library's button: a ZIP with a new map, a ZIP inside it, and the smoke PDF when there
    /// is one, which the index worker reads from a temporary copy. The results are grouped, the list copies, and the library is as it was.
    /// </summary>
    static async Task CheckDownloadAsync(IServiceProvider services, Window window, int books, string? smokeFiles)
    {
        // A ZIP, as the app may write no other file. Left for the system's temp cleaning: the app may not delete folders.
        var download = System.IO.Path.Combine(System.IO.Directory.CreateTempSubdirectory("bibliotaph-smoke-check-").FullName, "Smoke Download.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(download, System.IO.Compression.ZipArchiveMode.Create))
        {
            await using (var map = zip.CreateEntry("Maps/Smoke New Map.png").Open()) await map.WriteAsync(TokenPng(42));
            zip.CreateEntry("Inner.zip").Open().Dispose();
            if (smokeFiles is not null)
            {
                await using var book = zip.CreateEntry("Smoke Download.pdf").Open();
                await book.WriteAsync(await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(smokeFiles, "smoke-book.pdf")));
                await book.WriteAsync("\n% a downloaded copy\n"u8.ToArray());
            }
        }

        services.GetRequiredService<INavigationService>().NavigateTo(Route.Library);
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "CheckDownload"), "Check a download");
        await WaitUntilAsync(window, () => services.GetRequiredService<ShellViewModel>().CurrentPage is DownloadCheckViewModel,
            () => "Check a download didn't open its page.");
        var page = (DownloadCheckViewModel)services.GetRequiredService<ShellViewModel>().CurrentPage!;
        if (Descendants<Button>(window).FirstOrDefault(b => b.Name == "CheckFolder") is not { IsVisible: true, IsEnabled: true })
            throw new InvalidOperationException("Check a folder isn't offered.");

        // The file picker can't be driven here, so the check starts as the picker would start it.
        await page.StartAsync(download);
        await Settle(window);
        var expected = smokeFiles is null ? 2 : 3;
        if (!page.Progress.StartsWith($"Checked {expected} files", StringComparison.Ordinal))
            throw new InvalidOperationException($"The check reads “{page.Progress}”.");
        if (page.Groups is not [{ Verdict: CheckVerdict.New } fresh, .., { Verdict: CheckVerdict.Unreadable }])
            throw new InvalidOperationException($"The groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");
        if (fresh.Items.Count != expected - 1) throw new InvalidOperationException($"{fresh.Heading}: the new map and download aren't both new.");
        if (!Descendants<TextBlock>(window).Any(t => t.Text == "Smoke New Map.png" && t.IsVisible)) throw new InvalidOperationException("The new map isn't listed.");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "CopyList"), "Copy list");
        await WaitUntilAsync(window, () => page.Copied is not null, () => "Copy list said nothing.");
        if (await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter())) is var cards && cards.Count != books)
            throw new InvalidOperationException($"The library has {cards.Count} cards after a check, not {books}.");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }

    /// <summary>A small PNG of its own colour, so each token is different content.</summary>
    static byte[] TokenPng(int seed)
    {
        const int Size = 16;
        var pixels = new byte[Size * Size * 4];
        for (var p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = (byte)(seed * 37);
            pixels[p + 1] = (byte)(seed * 91);
            pixels[p + 2] = (byte)(255 - seed * 11);
            pixels[p + 3] = 255;
        }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(Size, Size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, Size * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
        var click = Clickable(link, "About Bibliotaph");
        _ = window.Dispatcher.BeginInvoke(click.Invoke);
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

    /// <summary>
    /// Settings > AI with no model server: AI is off, the library's folders are listed, and an endpoint nothing
    /// answers at says so rather than hanging or switching AI on.
    /// </summary>
    static async Task ShowAiSettingsAsync(IServiceProvider services, Window window)
    {
        var page = (AiSettingsViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Ai)).Selected;
        await WaitUntilAsync(window, () => page.HasFolders, () => "Settings > AI doesn't list the library's folders.");
        if (page.IsOn || page.Activity.AiOn) throw new InvalidOperationException("AI is on before anything was set up.");
        if (!page.Activity.Phases[^1].Status.StartsWith("Off", StringComparison.Ordinal))
            throw new InvalidOperationException($"The AI step says {page.Activity.Phases[^1].Status}, not that AI is off.");

        // On before anything is set up springs back and says why, under the switch.
        var on = Descendants<RadioButton>(window).FirstOrDefault(r => r.GroupName == "UseAi" && Equals(r.Content, "On"))
            ?? throw new InvalidOperationException("Settings > AI has no On switch.");
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(on)).Select();
        await WaitUntilAsync(window, () => page.SwitchProblem is not null && on.IsChecked == false && !page.IsOn,
            () => "Switching AI on before setting it up didn't spring back and say why.");

        // Port 9 is discard: nothing listens on it, so connecting fails at once.
        page.Endpoint = "http://127.0.0.1:9";
        await page.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(window, () => page.ConnectionProblem is not null, () => "Connecting to nothing didn't say so.");
        if (page.IsOn) throw new InvalidOperationException("A failed connection switched AI on.");

        // Test with a book opens its popup, which runs the test and says what went wrong.
        page.Model = "smoke-model";
        await Settle(window);
        var test = Descendants<Button>(window).FirstOrDefault(b => b.Command == page.TestCommand)
            ?? throw new InvalidOperationException("Settings > AI has no Test with a book button.");
        // ShowDialog returns only when the popup closes, so click from the queue and go on inside the popup's loop.
        var click = Clickable(test, "Test with a book");
        _ = window.Dispatcher.BeginInvoke(click.Invoke);
        Views.AiTestDialog? dialog = null;
        await WaitUntilAsync(window, () => (dialog = Application.Current.Windows.OfType<Views.AiTestDialog>().FirstOrDefault()) is { IsLoaded: true },
            () => "Test with a book didn't open its popup.");
        await WaitUntilAsync(window, () => dialog!.Test is { IsRunning: false, Problem: not null },
            () => "Testing with nothing at the address didn't finish and say why.");
        dialog!.Close();
        await WaitUntilAsync(window, () => !Application.Current.Windows.OfType<Views.AiTestDialog>().Any(), () => "The test popup didn't close.");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }

    /// <summary>
    /// The model pilot, as with --pilot: Settings > AI shows its section; the made-up library has no book with enough
    /// text to pick; and with a book put on the list and one model's reading of it, the review page offers that model's
    /// value without its name, saves the answers into the catalog too, and the report scores the model.
    /// </summary>
    static async Task RunPilotAsync(IServiceProvider services, Window window)
    {
        var mode = services.GetRequiredService<PilotMode>();
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        mode.IsOn = true;
        try
        {
            var page = (AiSettingsViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Ai)).Selected;
            await WaitUntilAsync(window, () => page.Pilot.BooksText.Length > 0, () => "Settings > AI doesn't show the model pilot.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == page.Pilot.PickBooksCommand), "Pick books");
            await WaitUntilAsync(window, () => page.Pilot.Note is not null, () => "Picking books didn't say what it picked.");
            if (page.Pilot.Books != 0) throw new InvalidOperationException($"The pilot picked {page.Pilot.Books} made-up books, whose pages are too short.");

            var pilot = services.GetRequiredService<PilotService>();
            var inn = (await services.GetRequiredService<LibraryStore>().GetRelativePathsAsync())
                .First(p => p.RelativePath.EndsWith("Haunted Inn.pdf", StringComparison.Ordinal)).DocumentId;
            await pilot.UseBooksAsync([inn]);
            const string Model = "smoke-pilot-model";
            await pilot.Store.RecordAsync(new PilotRun(Model, inn, 12, null),
                [new PilotProposal(Model, inn, "title", "The Haunted Inn", 0, "The inn is haunted", true)]);
            navigation.GoBack();
            await Settle(window);
            page = (AiSettingsViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Ai)).Selected;
            await WaitUntilAsync(window, () => page.Pilot.Books == 1, () => "Settings > AI doesn't count the book on the pilot list.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == page.Pilot.ReviewCommand), "Review answers");
            await Settle(window);
            var review = shell.CurrentPage as PilotReviewViewModel ?? throw new InvalidOperationException("Review answers didn't open the review page.");
            await WaitUntilAsync(window, () => review.Book is not null && review.Fields.Count == PilotFields.Core.Count,
                () => "The review page doesn't show the book and its fields.");

            var title = review.Fields.Single(f => f.Field == MetadataFields.Title);
            var offered = title.Options.FirstOrDefault(o => o.Text == "The Haunted Inn")
                ?? throw new InvalidOperationException("The review page doesn't offer the model's title.");
            if (Descendants<TextBlock>(window).Any(t => t.Text.Contains(Model, StringComparison.Ordinal)))
                throw new InvalidOperationException("The review page names the model, so it isn't blind.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == offered.UseCommand), "Use the model's title");
            // An automation click runs from the dispatcher's queue, so the answer changes once the window has worked.
            await WaitUntilAsync(window, () => title.Answer == "The Haunted Inn", () => $"Use put “{title.Answer}” in the answer.");
            review.Fields.Single(f => f.Field == MetadataFields.Levels).NotInBook = true;
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Command == review.SaveCommand), "Save and next");
            await WaitUntilAsync(window, () => review.PositionText.Contains("answered 1", StringComparison.Ordinal),
                () => $"Saving the answers didn't count them: {review.PositionText}");
            var innEntry = await services.GetRequiredService<EntryStore>().GetEntryAsync(inn) ?? throw new InvalidOperationException("The inn has no entry.");
            var (effective, _) = await services.GetRequiredService<MetadataService>().GetAsync(innEntry.EntryId);
            if (effective[MetadataFields.Title].First is not { Value: "The Haunted Inn", Confirmed: true })
                throw new InvalidOperationException("The answer didn't become the book's title in the catalog.");

            var scores = await pilot.ScoreAsync();
            if (scores is not [{ Answered: 1, Fields: var fields }] || fields.Single(f => f.Field == "title") is not { Proposed: 1, Correct: 1 })
                throw new InvalidOperationException("The pilot didn't score the model's title as right.");
            var report = await pilot.WriteReportAsync();
            if (!System.IO.File.ReadAllText(report).Contains(Model, StringComparison.Ordinal))
                throw new InvalidOperationException("The pilot report doesn't list the model.");
            navigation.GoBack();
            await Settle(window);
            navigation.GoBack();
            await Settle(window);
        }
        finally
        {
            mode.IsOn = false;
        }
    }

    /// <summary>Clicks <paramref name="button"/> the way a person would, failing clearly if they couldn't.</summary>
    static void Click(Button? button, string name) => Clickable(button, name).Invoke();

    /// <summary>
    /// The automation click for <paramref name="button"/>, once it is shown and enabled. Automation refuses a disabled
    /// button with an exception that names neither the button nor the step, and clicks a hidden one when no person can.
    /// </summary>
    static IInvokeProvider Clickable(Button? button, string name)
    {
        if (button is null) throw new InvalidOperationException($"There is no {name} button.");
        if (!button.IsVisible) throw new InvalidOperationException($"The {name} button isn't showing, so it can't be clicked.");
        if (!button.IsEnabled) throw new InvalidOperationException($"The {name} button is disabled, so it can't be clicked.");
        return (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
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

    /// <summary>Chooses each section of Settings in turn from its list, as a click does, and checks the list marks it.</summary>
    static async Task ShowEachSectionAsync(Window window, SettingsViewModel settings)
    {
        foreach (var section in settings.Sections)
        {
            section.IsSelected = true;
            await Settle(window);
            var entry = Descendants<RadioButton>(window).SingleOrDefault(r => r.GroupName == "SettingsSection" && r.IsChecked == true);
            if (!ReferenceEquals(settings.Selected, section) || !ReferenceEquals(entry?.DataContext, section))
                throw new InvalidOperationException($"Choosing {section.Label} in the section list didn't show it.");
        }
    }

    /// <summary>
    /// The sidebar's status line opens Settings at Processing, both from another page and from another section of
    /// Settings, under the breadcrumb "Workspace / Settings".
    /// </summary>
    static async Task OpenProcessingFromStatusAsync(IServiceProvider services, Window window)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        void ClickStatus() => Click(window.FindName("StatusButton") as Button, "sidebar status");

        ClickStatus();
        var settings = await ExpectSettingsAsync(window, shell, SettingsSection.Processing, "The sidebar status");
        if (shell.Section != "Workspace" || shell.Title != "Settings" || shell.OpenSectionCommand.CanExecute(null))
            throw new InvalidOperationException($"The breadcrumb reads {shell.Section} / {shell.Title}, not Workspace / Settings.");
        settings.Show(SettingsSection.Appearance);
        await Settle(window);
        ClickStatus();
        await WaitUntilAsync(window, () => settings.Selected.Section == SettingsSection.Processing,
            () => $"The sidebar status left Settings at {settings.Selected.Label}, not Processing.");
        if (!ReferenceEquals(shell.CurrentPage, settings)) throw new InvalidOperationException("The sidebar status opened a second Settings page.");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }

    /// <summary>
    /// Settings with a library in it, from the Library page and back to it: the Library's and Home's Manage folders
    /// open the Library section, which describes each folder and lists the folder names read as labels; Processing
    /// shows each step of indexing; and Back returns to the section that was showing.
    /// </summary>
    static async Task ShowSettingsAsync(IServiceProvider services, Window window)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var libraryPage = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library isn't showing.");
        libraryPage.ManageFoldersCommand.Execute(null);
        var settings = await ExpectSettingsAsync(window, shell, SettingsSection.Library, "The Library's Manage folders");
        var library = (LibrarySectionViewModel)settings.Selected;
        await WaitUntilAsync(window, () => library.Folders.Count > 0 && library.Folders.All(f => f.Detail.Length > 0),
            () => "Settings > Library didn't describe its folders.");
        await WaitUntilAsync(window, () => library.FolderLabels.Any(l => l is { Folder: "Adventures", IsEnabled: true }),
            () => "Settings > Library doesn't list the Adventures folder name.");
        await ShowEachSectionAsync(window, settings);

        settings.Show(SettingsSection.Processing);
        var processing = (ProcessingSectionViewModel)settings.Selected;
        await WaitUntilAsync(window, () => processing.HasFolders, () => "Settings > Processing says there are no folders.");
        if (processing.Activity.Phases.Count != 5 || processing.Activity.Phases.Any(p => p.Status.Length == 0))
            throw new InvalidOperationException("The indexing steps aren't all described.");

        navigation.NavigateTo(Route.Home);
        var home = shell.CurrentPage as HomeViewModel ?? throw new InvalidOperationException("The Home route didn't open Home.");
        home.ManageFoldersCommand.Execute(null);
        var fromHome = await ExpectSettingsAsync(window, shell, SettingsSection.Library, "Home's Manage folders");
        if (ReferenceEquals(fromHome, settings)) throw new InvalidOperationException("Home's Manage folders didn't open a new Settings page.");

        navigation.GoBack();
        await Settle(window);
        navigation.GoBack();
        await Settle(window);
        if (!ReferenceEquals(shell.CurrentPage, settings) || !ReferenceEquals(settings.Selected, processing))
            throw new InvalidOperationException("Back didn't return to Settings at Processing.");
        navigation.GoBack();
        await Settle(window);
        if (!ReferenceEquals(shell.CurrentPage, libraryPage)) throw new InvalidOperationException("Back didn't return from Settings to the Library.");
    }

    /// <summary>Opens Settings at <paramref name="section"/>, as a link into it does.</summary>
    static Task<SettingsViewModel> OpenSettingsAsync(IServiceProvider services, Window window, SettingsSection section)
    {
        services.GetRequiredService<SettingsLinks>().Open(section);
        return ExpectSettingsAsync(window, services.GetRequiredService<ShellViewModel>(), section, $"Opening Settings at {section}");
    }

    /// <summary>Waits for Settings to show at <paramref name="section"/> after <paramref name="link"/> was followed.</summary>
    static async Task<SettingsViewModel> ExpectSettingsAsync(Window window, ShellViewModel shell, SettingsSection section, string link)
    {
        await WaitUntilAsync(window, () => shell.CurrentPage is SettingsViewModel settings && settings.Selected.Section == section,
            () => $"{link} opened {(shell.CurrentPage as SettingsViewModel)?.Selected.Label ?? shell.CurrentPage?.Title}, not Settings > {section}.");
        return (SettingsViewModel)shell.CurrentPage!;
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

    /// <summary>
    /// The search box's field guide, by keyboard alone: Ctrl+K's command focuses the empty box and the guide lists
    /// every field in the theme's text colour; Down and Enter pick system:, which lists the library's systems, and a
    /// value; Down reopens it, and Tab and Enter add type:adventure. The search finds the Haunted Inn. Then Esc closes
    /// the guide and a second Esc clears the search.
    /// </summary>
    static async Task UseSearchGuideAsync(IServiceProvider services, Window window, int books)
    {
        var guide = services.GetRequiredService<SearchGuideViewModel>();
        var search = services.GetRequiredService<SearchState>();
        var page = services.GetRequiredService<ShellViewModel>().CurrentPage as LibraryViewModel
            ?? throw new InvalidOperationException("The search guide check expects to start in the Library.");
        var box = (TextBox)window.FindName("Search");
        var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("SearchGuide");
        if (box.Text.Length > 0 || guide.IsOpen) throw new InvalidOperationException($"The search box isn't empty and closed before the guide check: \"{box.Text}\".");

        // Focus must arrive, not already be there, for the box to open the guide.
        if (box.IsKeyboardFocused) Keyboard.ClearFocus();
        window.Activate();
        ShellCommands.FocusSearch.Execute(null, window);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!box.IsKeyboardFocused && Stopwatch.GetTimestamp() < deadline)
        {
            await Settle(window);
            await Task.Delay(50);
        }
        var focused = box.IsKeyboardFocused;
        if (!focused)
        {
            // A window that isn't in the foreground may not get keyboard focus; drive the same handler focus would.
            Log.Warning("Smoke test: the search box didn't get keyboard focus, so the guide is opened as focus would open it");
            guide.Focused(box.Text, box.CaretIndex);
        }
        var rows = (DependencyObject)window.FindName("SearchGuideRows");
        TextBlock? row = null;
        await WaitUntilAsync(window, () => popup.IsOpen && guide.Suggestions.Count == SearchFields.All.Count && popup.Child is { IsVisible: true }
            && (row = FindChild<TextBlock>(rows)) is not null,
            () => $"Focusing the empty search box didn't show the guide's rows for every field ({guide.Suggestions.Count} rows, open {popup.IsOpen}).");

        // A popup doesn't take the window's text colour, so the guide sets the theme's own.
        var text = ((System.Windows.Media.SolidColorBrush)window.FindResource("Bt.Text")).Color;
        if (popup.Child is not Border panel || System.Windows.Documents.TextElement.GetForeground(panel) is not System.Windows.Media.SolidColorBrush { } brush
            || brush.Color != text || row?.Foreground is not System.Windows.Media.SolidColorBrush { } rowBrush || rowBrush.Color != text)
            throw new InvalidOperationException("The search guide's text isn't in the theme's text colour.");

        await HighlightAsync(window, box, guide, s => s is FieldSuggestionViewModel { Field.Field: SearchField.System }, "system:");
        Press(box, Key.Enter);
        await WaitUntilAsync(window, () => box.Text == "system:" && box.CaretIndex == 7 && guide.Suggestions.Any(s => s is ValueSuggestionViewModel { Value.Value: "dnd" }),
            () => $"Picking system: left \"{box.Text}\" with {guide.Suggestions.Count} rows, not the library's systems.");
        if (focused && !box.IsKeyboardFocused) throw new InvalidOperationException("Picking a field took focus out of the search box.");

        await HighlightAsync(window, box, guide, s => s is ValueSuggestionViewModel { Value.Value: "dnd" }, "system:dnd");
        Press(box, Key.Enter);
        await WaitUntilAsync(window, () => box.Text == "system:dnd " && !guide.IsOpen, () => $"Picking D&D left \"{box.Text}\" (guide open {guide.IsOpen}).");

        // Down opens the guide again after a finished value; Tab picks like Enter.
        Press(box, Key.Down);
        await WaitUntilAsync(window, () => guide.IsOpen && guide.Suggestions.Count == SearchFields.All.Count, () => "Down didn't reopen the search guide.");
        await HighlightAsync(window, box, guide, s => s is FieldSuggestionViewModel { Field.Field: SearchField.Type }, "type:");
        Press(box, Key.Tab);
        await WaitUntilAsync(window, () => box.Text == "system:dnd type:" && guide.Suggestions.Any(s => s is ValueSuggestionViewModel { Value.Value: "adventure" }),
            () => $"Picking type: left \"{box.Text}\" with {guide.Suggestions.Count} rows, not the library's types.");
        await HighlightAsync(window, box, guide, s => s is ValueSuggestionViewModel { Value.Value: "adventure" }, "type:adventure");
        Press(box, Key.Enter);
        await WaitUntilAsync(window, () => box.Text == "system:dnd type:adventure " && search.Text == "system:dnd type:adventure" && page.Items is [{ Title: "Haunted Inn" }],
            () => $"The search built from the guide, \"{box.Text}\", didn't find just the Haunted Inn ({page.Items.Count} found).");

        // Esc closes the guide and keeps the search; a second Esc clears it, as before the guide.
        Press(box, Key.Down);
        await WaitUntilAsync(window, () => guide.IsOpen, () => "Down didn't open the search guide.");
        Press(box, Key.Escape);
        await WaitUntilAsync(window, () => !guide.IsOpen && !popup.IsOpen && box.Text.Length > 0, () => "Esc didn't just close the search guide.");
        Press(box, Key.Escape);
        await WaitUntilAsync(window, () => box.Text.Length == 0 && !search.IsSearching && !guide.IsOpen && page.Items.Count == books,
            () => "A second Esc didn't clear the search.");
    }

    /// <summary>Presses Down until the guide highlights the row <paramref name="wanted"/> picks, and the box names it for screen readers.</summary>
    static async Task HighlightAsync(Window window, TextBox box, SearchGuideViewModel guide, Func<GuideSuggestionViewModel, bool> wanted, string name)
    {
        for (var presses = 0; guide.Highlighted is not { } row || !wanted(row); presses++)
        {
            if (presses > guide.Suggestions.Count) throw new InvalidOperationException($"Down never reached {name} in the search guide.");
            Press(box, Key.Down);
            await Settle(window);
        }
        if (System.Windows.Automation.AutomationProperties.GetHelpText(box) != guide.Highlighted.Spoken || !guide.Highlighted.Spoken.Contains(name, StringComparison.Ordinal))
            throw new InvalidOperationException($"The search box doesn't tell screen readers that {name} is highlighted.");
    }

    /// <summary>A key press as the search box sees it first. Each key the guide uses must be handled there.</summary>
    static void Press(TextBox box, Key key)
    {
        var source = PresentationSource.FromVisual(box) ?? throw new InvalidOperationException("The search box isn't on screen.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        box.RaiseEvent(args);
        if (!args.Handled) throw new InvalidOperationException($"The search box didn't handle {key}.");
    }
}
