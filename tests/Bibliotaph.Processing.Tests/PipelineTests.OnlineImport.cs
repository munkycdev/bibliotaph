using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Importing an online library's books as books owned elsewhere (online libraries, O2).</summary>
public sealed partial class PipelineTests
{
    OnlineImports OnlineImports() => new(_metadataStore, _metadata, _entries, _projector, _queries, new SourceFileReader(), new FakeCodec(),
        new CoverCache(_paths));

    /// <summary>The made-up D&amp;D Beyond page, saved with the cover of its second book beside it.</summary>
    string SavedLibraryPage()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "saved")).FullName;
        var page = Path.Combine(folder, "dndbeyond-library.html");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "online", "dndbeyond-library.html"), page);
        var images = Directory.CreateDirectory(Path.Combine(folder, "dndbeyond-library_files")).FullName;
        File.WriteAllBytes(Path.Combine(images, "639000000000000002.jpeg"), FakeCodec.Png(200, 260));
        return page;
    }

    [Fact]
    public async Task A_saved_library_page_is_previewed_imported_undone_and_imported_again_without_changing_the_users_work()
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

        var saved = await import.ReadSavedPageAsync(SavedLibraryPage(), Ct);
        Assert.NotNull(saved);
        Assert.Equal(("dnd-beyond", 2, 1), (saved.Reader.Key, saved.Page.Books.Count, saved.Page.Shared));

        // The preview: the first book goes on its card; the second is new, its publisher being another; the third is missing.
        var plan = await import.PlanAsync(saved.Reader, saved.Page, Ct);
        Assert.Equal([(OnlineOutcome.AddToCard, vault), (OnlineOutcome.New, (EntryId?)null)], plan.Rows.Select(r => (r.Outcome, r.EntryId)));
        Assert.Equal("The Ember Vault", plan.Rows[0].CardTitle);
        Assert.Equal((gone!.Value, "Sold Back"), (Assert.Single(plan.Missing).EntryId, plan.Missing[0].Title));

        var result = await import.ImportAsync(plan, plan.Rows, import.SavedCovers(saved.Images), Ct);
        Assert.Equal((1, 1), (result.Added, result.Changed));

        // The card had D&D Beyond with no link: now it has the link, once.
        Assert.Equal(["dnd-beyond " + Vault], await Values(vault!.Value, MetadataFields.AlsoOwn));
        Assert.Equal(["gilded press"], (await Values(vault.Value, MetadataFields.Publisher)).Select(v => v.ToLowerInvariant()));
        Assert.Empty(await Values(vault.Value, MetadataFields.Year));

        // The new book has every value the page gave, as the user's own, and the cover the saved page had.
        var atlas = (await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Single(c => c.Title == "Atlas of the Drowned Coast" && c.EntryId != other);
        Assert.Equal(("https://www.dndbeyond.com/sources/dnd/drowned-coast", "D&D Beyond"), (atlas.OnlineLink, atlas.OnlineLabel));
        Assert.Equal(["dnd-5e-2024"], await Values(atlas.EntryId, MetadataFields.Edition));
        Assert.Equal(["supplement"], await Values(atlas.EntryId, MetadataFields.Types));
        Assert.Equal(["2025"], await Values(atlas.EntryId, MetadataFields.Year));
        Assert.True(Assert.Single((await _metadata.GetAsync(atlas.EntryId, Ct)).Metadata[MetadataFields.Title].Values).Confirmed);
        Assert.NotNull(atlas.Cover);
        Assert.True(File.Exists(Path.Combine(_paths.Cache, "covers", atlas.Cover)));

        // Undo: the new book and its cover go, and the card is as it was.
        await import.UndoAsync(result.Undo, Ct);
        Assert.DoesNotContain(await search.ListAsync(new LibraryFilter(), ct: Ct), c => c.EntryId == atlas.EntryId);
        Assert.False(File.Exists(Path.Combine(_paths.Cache, "covers", atlas.Cover)));
        Assert.Equal(["dnd-beyond"], await Values(vault.Value, MetadataFields.AlsoOwn));

        // Imported again, then again once the user has set a year: the second time only empty fields are filled.
        await import.ImportAsync(plan, plan.Rows, import.SavedCovers(saved.Images), Ct);
        Assert.Null(await _metadata.SetAsync(vault.Value, MetadataFields.Year, "2020", Ct));
        var again = await import.PlanAsync(saved.Reader, saved.Page, Ct);
        Assert.Equal([OnlineOutcome.Update, OnlineOutcome.Unchanged], again.Rows.Select(r => r.Outcome));
        Assert.Equal([MetadataFields.System, MetadataFields.Edition, MetadataFields.Types], again.Rows[0].Fills);

        var filled = await import.ImportAsync(again, again.Rows, ct: Ct);
        Assert.Equal((0, 1), (filled.Added, filled.Changed));
        Assert.Equal(["dnd-5e"], await Values(vault.Value, MetadataFields.Edition));
        Assert.Equal(["adventure"], await Values(vault.Value, MetadataFields.Types));
        Assert.Equal(["2020"], await Values(vault.Value, MetadataFields.Year));
        Assert.All((await import.PlanAsync(saved.Reader, saved.Page, Ct)).Rows, r => Assert.Equal(OnlineOutcome.Unchanged, r.Outcome));
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

    async Task<IReadOnlyList<string>> Values(EntryId entryId, MetadataField field) =>
        [.. (await _metadata.GetAsync(entryId, Ct)).Metadata[field].Values.Select(v => v.Value).Order(StringComparer.Ordinal)];

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
