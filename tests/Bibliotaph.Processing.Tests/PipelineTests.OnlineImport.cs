using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    OnlineImports OnlineImports() => new(_metadataStore, _metadata, _entries, _projector, _queries, new FakeCodec(), new CoverCache(_paths));

    /// <summary>A made-up library page, read as Read this page or Send to Bibliotaph reads it.</summary>
    static (IOnlineLibraryReader Reader, OnlinePage Page) LibraryPage(string name)
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "online", name));
        return Processing.OnlineImports.Services.Select(r => (Reader: r, Page: r.Read(html))).First(r => r.Page is not null)!;
    }

    /// <summary>Covers as Send to Bibliotaph gives them: a picture for each of these file names, found by the cover's address.</summary>
    static Func<string, CancellationToken, Task<byte[]?>> SentCovers(params string[] names) => (url, _) =>
        Task.FromResult(Processing.OnlineImports.CoverFileName(url) is { } name && names.Contains(name) ? FakeCodec.Png(200, 260) : null);

    async Task<IReadOnlyList<string>> Values(EntryId entryId, MetadataField field) =>
        [.. (await _metadata.GetAsync(entryId, Ct)).Metadata[field].Values.Select(v => v.Value).Order(StringComparer.Ordinal)];

    /// <summary>Importing an online library's books as books owned elsewhere.</summary>
    public sealed class OnlineImportTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_library_page_is_previewed_imported_undone_and_imported_again_without_changing_the_users_work()
        {
            await _service.StartAsync(Ct);
            var search = new LibraryQueries(_index);
            var import = OnlineImports();
            const string Vault = "https://www.dndbeyond.com/sources/dnd/ember-vault";

            // Already here: the first book, said to be on D&D Beyond with no link; a book of the same name as the second by
            // another publisher; and a book imported before that the page doesn't list any more.
            var (vault, _) = await _elsewhere.AddAsync(new ElsewhereDraft("The Ember Vault", Publisher: "Gilded Press", AlsoOwn: ["D&D Beyond"]), Ct);
            var (other, _) = await _elsewhere.AddAsync(new ElsewhereDraft("Atlas of the Drowned Coast", Publisher: "Kobold Works"), Ct);
            var (gone, _) = await _elsewhere.AddAsync(new ElsewhereDraft("Sold Back", AlsoOwn: ["https://www.dndbeyond.com/sources/dnd/sold-back/"]), Ct);

            var saved = LibraryPage("dndbeyond-library.html");
            var covers = SentCovers("639000000000000002.jpeg");
            Assert.Equal(("dnd-beyond", 2, 1), (saved.Reader.Key, saved.Page.Books.Count, saved.Page.Shared));

            // The preview: the first book goes on its card; the second is new, its publisher being another; the third is missing.
            var plan = await import.PlanAsync(saved.Reader, saved.Page, Ct);
            Assert.Equal([(OnlineOutcome.AddToCard, vault), (OnlineOutcome.New, (EntryId?)null)], plan.Rows.Select(r => (r.Outcome, r.EntryId)));
            Assert.Equal("The Ember Vault", plan.Rows[0].CardTitle);
            Assert.Equal((gone!.Value, "Sold Back"), (Assert.Single(plan.Missing).EntryId, plan.Missing[0].Title));

            var result = await import.ImportAsync(plan, plan.Rows, covers, Ct);
            Assert.Equal((1, 1), (result.Added, result.Changed));

            // The card had D&D Beyond with no link: now it has the link, once.
            Assert.Equal(["dnd-beyond " + Vault], await Values(vault!.Value, MetadataFields.AlsoOwn));
            Assert.Equal(["gilded press"], (await Values(vault.Value, MetadataFields.Publisher)).Select(v => v.ToLowerInvariant()));
            Assert.Empty(await Values(vault.Value, MetadataFields.Year));

            // The new book has every value the page gave, as the user's own, and the cover sent with the page.
            var atlas = (await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Single(c => c.Title == "Atlas of the Drowned Coast" && c.EntryId != other);
            Assert.Equal(("https://www.dndbeyond.com/sources/dnd/drowned-coast", "D&D Beyond"), (atlas.OnlineLink, atlas.OnlineLabel));
            Assert.Equal(["dnd-5e-2024"], await Values(atlas.EntryId, MetadataFields.Edition));
            Assert.Equal(["supplement"], await Values(atlas.EntryId, MetadataFields.Types));
            Assert.Equal(["2025"], await Values(atlas.EntryId, MetadataFields.Year));
            Assert.True(Assert.Single((await _metadata.GetAsync(atlas.EntryId, Ct)).Metadata[MetadataFields.Title].Values).Confirmed);
            Assert.NotNull(atlas.Cover);
            Assert.True(File.Exists(Path.Combine(_paths.Covers, atlas.Cover)));

            // Undo: the new book and its cover go, and the card is as it was.
            await import.UndoAsync(result.Undo, Ct);
            Assert.DoesNotContain(await search.ListAsync(new LibraryFilter(), ct: Ct), c => c.EntryId == atlas.EntryId);
            Assert.False(File.Exists(Path.Combine(_paths.Covers, atlas.Cover)));
            Assert.Equal(["dnd-beyond"], await Values(vault.Value, MetadataFields.AlsoOwn));

            // Imported again, then again once the user has set a year: the second time only empty fields are filled.
            await import.ImportAsync(plan, plan.Rows, covers, Ct);
            Assert.Null(await _metadata.SetAsync(vault.Value, MetadataFields.Year, "2020", Ct));
            var again = await import.PlanAsync(saved.Reader, saved.Page, Ct);
            Assert.Equal([OnlineOutcome.Update, OnlineOutcome.Unchanged], again.Rows.Select(r => r.Outcome));
            Assert.Equal([MetadataFields.System, MetadataFields.Edition, MetadataFields.Types], again.Rows[0].Fills);
            // The card never had a cover, and the page has one for it.
            Assert.Equal("Fills Game system, Edition, Type, and gets its cover back.", OnlineImportText.Detail(again.Rows[0]));

            var filled = await import.ImportAsync(again, again.Rows, ct: Ct);
            Assert.Equal((0, 1), (filled.Added, filled.Changed));
            Assert.Equal(["dnd-5e"], await Values(vault.Value, MetadataFields.Edition));
            Assert.Equal(["adventure"], await Values(vault.Value, MetadataFields.Types));
            Assert.Equal(["2020"], await Values(vault.Value, MetadataFields.Year));

            // Nothing left to fill, but no picture came with the page for its cover, so it is still offered.
            var cover = await import.PlanAsync(saved.Reader, saved.Page, Ct);
            Assert.Equal([(OnlineOutcome.Update, true), (OnlineOutcome.Unchanged, false)], cover.Rows.Select(r => (r.Outcome, r.Cover)));
            Assert.Equal("Gets its cover back.", OnlineImportText.Detail(cover.Rows[0]));
            var back = await import.ImportAsync(cover, cover.Rows, SentCovers("639000000000000001.jpeg"), Ct);
            Assert.Equal((0, 0, 1), (back.Added, back.Changed, back.Covers));
            Assert.Equal("1 book got its cover back.", OnlineImportText.Imported(saved.Reader, back.Added, back.Changed, back.Covers));
            Assert.NotNull((await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Single(c => c.EntryId == vault).Cover);
            Assert.All((await import.PlanAsync(saved.Reader, saved.Page, Ct)).Rows, r => Assert.Equal(OnlineOutcome.Unchanged, r.Outcome));
        }

        [Fact]
        public async Task A_cover_kept_by_an_earlier_version_in_the_cache_moves_to_the_users_covers_and_a_lost_one_comes_back_on_import()
        {
            await _service.StartAsync(Ct);
            var search = new LibraryQueries(_index);
            var import = OnlineImports();
            var saved = LibraryPage("dndbeyond-library.html");
            var plan = await import.PlanAsync(saved.Reader, saved.Page, Ct);
            await import.ImportAsync(plan, plan.Rows, SentCovers("639000000000000001.jpeg", "639000000000000002.jpeg"), Ct);
            var cards = await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct);
            Assert.All(cards, c => Assert.StartsWith("online-", c.Cover));
            var covers = new CoverCache(_paths);

            // One kept as earlier versions kept it, in the cache: moved to the user's covers, and its card shows it.
            var first = (await _entries.GetCurrentAsync([cards[0].EntryId], Ct)).Single().SyncId!.Value;
            var jpeg = await File.ReadAllBytesAsync(Path.Combine(_paths.Covers, covers.FindImported(first)!), Ct);
            covers.DeleteImported(first);
            Directory.CreateDirectory(covers.Folder);
            await File.WriteAllBytesAsync(Path.Combine(covers.Folder, $"elsewhere-{first:N}.jpg"), jpeg, Ct);
            Assert.Equal(1, covers.MoveOldImported());
            Assert.Empty(Directory.EnumerateFiles(covers.Folder, "elsewhere-*"));
            Assert.Equal(jpeg, covers.ReadAllImported()[first]);
            await _projector.ProjectElsewhereAsync(Ct);
            Assert.Equal(covers.FindImported(first), (await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Single(c => c.EntryId == cards[0].EntryId).Cover);

            // Both lost, as on a new PC: reading the page again offers them back, and nothing else.
            covers.DeleteImported(first);
            covers.DeleteImported((await _entries.GetCurrentAsync([cards[1].EntryId], Ct)).Single().SyncId!.Value);
            var again = await import.PlanAsync(saved.Reader, saved.Page, Ct);
            Assert.All(again.Rows, r => Assert.Equal((OnlineOutcome.Update, true, 0), (r.Outcome, r.Cover, r.Fills.Count)));
            var result = await import.ImportAsync(again, again.Rows, SentCovers("639000000000000001.jpeg", "639000000000000002.jpeg"), Ct);
            Assert.Equal((0, 0, 2), (result.Added, result.Changed, result.Covers));
            Assert.All(await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct), c => Assert.StartsWith("online-", c.Cover));
        }

        [Fact]
        public async Task Only_a_page_that_is_the_whole_library_lists_the_books_missing_from_it()
        {
            await _service.StartAsync(Ct);
            var import = OnlineImports();
            var (gone, _) = await _elsewhere.AddAsync(new ElsewhereDraft("Sold Back", AlsoOwn: ["https://www.dndbeyond.com/sources/dnd/sold-back"]), Ct);
            var reader = new DndBeyondReader();

            var whole = await import.PlanAsync(reader, new OnlinePage([], 0, Complete: true), Ct);
            Assert.Equal([gone!.Value], whole.Missing.Select(m => m.EntryId));
            Assert.Empty((await import.PlanAsync(reader, new OnlinePage([], 0, Complete: false), Ct)).Missing);
        }

        [Fact]
        public async Task A_stores_library_puts_its_books_in_get_it_from_and_knows_each_by_its_link_next_time()
        {
            await _service.StartAsync(Ct);
            var import = OnlineImports();
            var reader = new HumbleBundleReader();
            var html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "online", "humble-library.html"), Ct);
            const string Bestiary = "bundle https://www.humblebundle.com/home/library#the-clockwork-bestiary-vol-2";

            // A card already says the first book is from Humble Bundle, with no link.
            var (lanterns, _) = await _elsewhere.AddAsync(new ElsewhereDraft("Nine Crooked Lanterns"), Ct);
            Assert.Null(await _metadata.SetAsync(lanterns!.Value, MetadataFields.GetItFrom, "Humble Bundle", Ct));

            var plan = await import.PlanAsync(reader, reader.Read(html)!, Ct);
            Assert.Equal([OnlineOutcome.AddToCard, OnlineOutcome.New], plan.Rows.Select(r => r.Outcome));
            await import.ImportAsync(plan, plan.Rows, ct: Ct);

            Assert.Equal(["bundle https://www.humblebundle.com/home/library#nine-crooked-lanterns"], await Values(lanterns.Value, MetadataFields.GetItFrom));
            Assert.Empty(await Values(lanterns.Value, MetadataFields.AlsoOwn));
            var search = new LibraryQueries(_index);
            var bestiary = (await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Single(c => c.Title == "The Clockwork Bestiary: Vol. 2");
            Assert.Equal([Bestiary], await Values(bestiary.EntryId, MetadataFields.GetItFrom));
            // A book to download doesn't open online.
            Assert.Null(bestiary.OnlineLink);

            // The links differ only after the #, and each book is still known by its own: the card that had no publisher is
            // offered the page's, and both, imported with no pictures, their covers.
            var again = await import.PlanAsync(reader, reader.Read(html)!, Ct);
            Assert.Equal([(OnlineOutcome.Update, lanterns, true), (OnlineOutcome.Update, bestiary.EntryId, true)],
                again.Rows.Select(r => (r.Outcome, r.EntryId, r.Cover)));
            Assert.Equal([MetadataFields.Publisher], again.Rows[0].Fills);
            Assert.Empty(again.Rows[1].Fills);
        }

        [Fact]
        public async Task A_page_whose_covers_are_given_as_saved_files_still_finds_the_pictures_sent_for_them()
        {
            await _service.StartAsync(Ct);
            var import = OnlineImports();
            var (reader, page) = LibraryPage("demiplane-sources.html");
            Assert.Equal("demiplane", reader.Key);
            var plan = await import.PlanAsync(reader, page, Ct);
            await import.ImportAsync(plan, plan.Rows, SentCovers("cover(1).jpg"), Ct);

            var cards = (await new LibraryQueries(_index).ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).OrderBy(c => c.Title, StringComparer.Ordinal).ToList();
            Assert.Equal([("Ember Isles Core Rules", false), ("Tides of Ash & Salt", true)], cards.Select(c => (c.Title, c.Cover is not null)));
            Assert.Equal(("https://app.demiplane.com/nexus/emberisles/sources/tides-of-ash", "Demiplane"), (cards[1].OnlineLink, cards[1].OnlineLabel));
        }

        [Fact]
        public async Task A_page_owned_online_in_a_binder_opens_online_and_its_book_alone_has_no_file()
        {
            await _service.StartAsync(Ct);
            var (book, _) = await _elsewhere.AddAsync(new ElsewhereDraft("The Ember Vault", AlsoOwn: ["https://www.dndbeyond.com/sources/dnd/ember-vault"]), Ct);
            var pack = await _sessions.CreateAsync("Session 1", ct: Ct);
            const string Page = "https://www.dndbeyond.com/sources/dnd/ember-vault/the-vault#door";
            await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(book!.Value), new NewSessionItem(book.Value, Label: "The door", Link: Page)], ct: Ct);
            var sessions = new SessionsService(_sessions, _entries, _libraryStore, _queries, _projector, _places);

            var items = (await _sessions.GetAsync(pack.Id, Ct))!.Items;
            var targets = await sessions.ResolveAsync(items, Ct);
            Assert.Equal([SessionItemState.NoFile, SessionItemState.Online], items.Select(i => targets[i.Id].State));
            Assert.All(targets.Values, t => Assert.False(t.CanOpen));
            Assert.Equal(Page, items[1].Link);
        }
    }
}
