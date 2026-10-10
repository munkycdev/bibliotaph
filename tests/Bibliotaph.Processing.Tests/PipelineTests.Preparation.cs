using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Slice 3a to 3f: hearts, opens, collections, session packs and notes reach the Library through index.db, and come back after it is rebuilt.</summary>
public sealed partial class PipelineTests
{
    [Fact]
    public async Task Hearts_and_opens_are_projected_at_once_and_again_after_a_rebuild()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        Copy(pdfs.Scanned, "Scans/Goblin Scan.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        Assert.Equal(2, cards.Count);
        var (known, scan) = (cards[0], cards[1]);
        var favorites = new FavoritesService(_favorites, _entries, _projector);
        var reading = new ReadingService(_reading, _projector);
        var projected = new List<EntryId>();
        _projector.Projected += (_, ids) => projected.AddRange(ids);

        Assert.Equal([known.EntryId], await favorites.SetAsync([known.EntryId], favorite: true, Ct));
        Assert.Equal(known.EntryId, await favorites.GetCardAsync(known.DocumentId, Ct));
        Assert.Equal(scan.EntryId, await reading.RecordOpenAsync(scan.DocumentId, Ct));
        await reading.SavePositionAsync(scan.DocumentId, 1, Ct);

        Assert.Equal([known.EntryId, scan.EntryId], projected);
        Assert.Equal([known.EntryId], (await search.ListAsync(new LibraryFilter(Group: ScopeKeys.Favorites), ct: Ct)).Select(c => c.EntryId));
        Assert.Equal([known.EntryId], (await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("favorite:yes")), new LibraryFilter(), ct: Ct)).Select(c => c.EntryId));
        var opened = Assert.Single(await search.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyOpened, OnlyOpened: true), ct: Ct));
        Assert.Equal(scan.EntryId, opened.EntryId);
        Assert.NotNull(opened.OpenedUtc);
        Assert.Equal(1, await reading.GetPositionAsync(scan.DocumentId, Ct));

        // index.db is derived: emptied and projected again, the marks come back from catalog.db.
        await new IndexStore(_writer).SetScopesAsync(null, [], Ct);
        await new IndexStore(_writer).SetOpenedAsync(null, new Dictionary<EntryId, DateTime>(), Ct);
        Assert.Empty(await search.ListAsync(new LibraryFilter(Group: ScopeKeys.Favorites), ct: Ct));
        await _projector.ProjectAllAsync(Ct);
        Assert.True((await search.ListAsync(new LibraryFilter(), ct: Ct)).Single(c => c.EntryId == known.EntryId).Favorite);
        Assert.Equal([scan.EntryId], (await search.ListAsync(new LibraryFilter(OnlyOpened: true), ct: Ct)).Select(c => c.EntryId));

        await favorites.SetAsync([known.EntryId], favorite: false, Ct);
        Assert.Empty(await search.ListAsync(new LibraryFilter(Group: ScopeKeys.Favorites), ct: Ct));
    }

    [Fact]
    public async Task Collections_are_projected_with_their_names_and_follow_moves_renames_and_deletes()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        Copy(pdfs.Scanned, "Scans/Goblin Scan.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        var (known, scan) = (cards[0], cards[1]);
        var collections = new CollectionsService(_collections, _projector);
        var changes = 0;
        collections.Changed += (_, _) => changes++;
        static List<EntryId> Ids(params EntryId[] ids) => [.. ids.OrderBy(e => e.Value)];
        async Task<IReadOnlyList<EntryId>> FindAsync(string query) =>
            [.. (await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse(query)), new LibraryFilter(), ct: Ct)).Select(c => c.EntryId).OrderBy(e => e.Value)];
        async Task<IReadOnlyList<EntryId>> InAsync(string scope) =>
            [.. (await search.ListAsync(new LibraryFilter(Group: scope), ct: Ct)).Select(c => c.EntryId).OrderBy(e => e.Value)];

        var campaign = await collections.CreateAsync("Winter campaign", ct: Ct);
        var maps = await collections.CreateAsync("Maps", campaign.Id, ct: Ct);
        Assert.Equal([known.EntryId], await collections.AddAsync(campaign.Id, [known.EntryId], Ct));
        await collections.AddAsync(maps.Id, [scan.EntryId], Ct);

        Assert.Equal(Ids(known.EntryId, scan.EntryId), await InAsync(ScopeKeys.Collection(campaign.Id)));
        Assert.Equal(Ids(known.EntryId), await InAsync(ScopeKeys.CollectionOwn(campaign.Id)));
        Assert.Equal(Ids(known.EntryId, scan.EntryId), await FindAsync("collection:\"winter campaign\""));
        Assert.Equal(2, (await search.CountGroupsAsync("collection:", null, Ct))[ScopeKeys.Collection(campaign.Id)]);

        await collections.RenameAsync(campaign.Id, "Spring campaign", null, Ct);
        Assert.Empty(await FindAsync("collection:\"winter campaign\""));
        Assert.Equal(Ids(known.EntryId, scan.EntryId), await FindAsync("collection:\"spring campaign\""));

        await collections.MoveAsync(maps.Id, null, Ct);
        Assert.Equal(Ids(known.EntryId), await InAsync(ScopeKeys.Collection(campaign.Id)));

        // index.db is derived: emptied and projected again, the collections come back from catalog.db.
        await new IndexStore(_writer).SetScopesAsync(null, [], Ct);
        await new IndexStore(_writer).SetScopeNamesAsync([], Ct);
        await _projector.ProjectAllAsync(Ct);
        Assert.Equal(Ids(scan.EntryId), await FindAsync("collection:maps"));

        var deleted = await collections.DeleteAsync(campaign.Id, Ct);
        Assert.Empty(await InAsync(ScopeKeys.Collection(campaign.Id)));
        await collections.RestoreAsync(deleted!, Ct);
        Assert.Equal(Ids(known.EntryId), await FindAsync("collection:\"spring campaign\""));
        Assert.Equal([known.EntryId], await collections.RemoveAsync(campaign.Id, [known.EntryId], Ct));
        Assert.Empty(await InAsync(ScopeKeys.Collection(campaign.Id)));
        Assert.Equal(9, changes);
    }

    [Fact]
    public async Task Session_items_find_their_pages_in_a_new_version_and_never_jump_silently()
    {
        Copy(pdfs.WatermarkedForAna, "Purchases/Drowned Abbey.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var book = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        var sessions = new SessionsService(_sessions, _entries, _libraryStore, _queries, _projector);
        var pack = await sessions.CreateAsync("The midnight bell", ct: Ct);
        var appendix = await sessions.AddPagesAsync(pack.Id, book.DocumentId, 4, 4, ct: Ct);
        var crypt = await sessions.AddPagesAsync(pack.Id, book.DocumentId, 3, 3, "Crypt", ct: Ct);
        await sessions.AddAsync(pack.Id, [new NewSessionItem(book.EntryId, Label: "The whole book")], ct: Ct);
        var contents = (await sessions.GetAsync(pack.Id, Ct))!;
        Assert.Equal([appendix!.Value, crypt!.Value], contents.Items.Take(2).Select(i => i.Id));
        Assert.NotNull(contents.Items[0].Range!.FirstFingerprint);
        Assert.Equal([book.EntryId], (await search.ListAsync(new LibraryFilter(Group: ScopeKeys.Session(pack.Id)), ct: Ct)).Select(e => e.EntryId));
        async Task<SessionItemTarget[]> TargetsAsync()
        {
            var items = (await sessions.GetAsync(pack.Id, Ct))!.Items;
            var targets = await sessions.ResolveAsync(items, Ct);
            return [.. items.Select(i => targets[i.Id])];
        }

        Assert.Equal([new(book.DocumentId, 4, 4, SessionItemState.Ready), new(book.DocumentId, 3, 3, SessionItemState.Ready), new SessionItemTarget(book.DocumentId, 0, 0, SessionItemState.Ready)],
            await TargetsAsync());

        // A second printing becomes the book's current copy: the appendix is found in it; the corrected crypt isn't,
        // so it opens the first printing it was added from.
        Copy(pdfs.RevisedForAna, "Purchases/Drowned Abbey, second printing.pdf");
        _service.RequestScan();
        await SettleUntilAsync(async () => (await _copies.GetVersionsAsync(Ct)).Count == 1);
        var version = Assert.Single(await _copies.GetVersionsAsync(Ct)).Version;
        Assert.NotNull(await _copies.AnswerVersionAsync(version, VersionAnswer.MakeCurrent, Ct));
        var revised = version.DocumentId;
        var targets = await TargetsAsync();
        Assert.Equal(new SessionItemTarget(revised, 4, 4, SessionItemState.OtherCopy), targets[0]);
        Assert.Equal((book.DocumentId, 3, SessionItemState.Original), (targets[1].DocumentId, targets[1].FirstPage, targets[1].State));
        Assert.Equal(new SessionItemTarget(revised, 0, 0, SessionItemState.Ready), targets[2]);

        // Once the first printing's file is gone, the crypt opens at its page in the second printing, to be checked.
        File.Delete(Path.Combine(_library, "Purchases", "Drowned Abbey.pdf"));
        _service.RequestScan();
        await SettleUntilAsync(async () => (await _libraryStore.GetReadableAsync([book.DocumentId], Ct)).Count == 0);
        targets = await TargetsAsync();
        Assert.Equal((revised, 3, SessionItemState.Changed), (targets[1].DocumentId, targets[1].FirstPage, targets[1].State));
        Assert.StartsWith("This page changed", targets[1].Reason, StringComparison.Ordinal);

        // Use this page: from now on it is that page of the second printing.
        await sessions.RepointAsync(crypt.Value, revised, 3, 3, Ct);
        Assert.Equal(new SessionItemTarget(revised, 3, 3, SessionItemState.Ready), (await TargetsAsync())[1]);

        // With no file left, every item stays, saying why. (Another book keeps the library from being empty.)
        Copy(pdfs.KnownText, "Other/Known Text.pdf");
        File.Delete(Path.Combine(_library, "Purchases", "Drowned Abbey, second printing.pdf"));
        _service.RequestScan();
        await SettleUntilAsync(async () => (await _libraryStore.GetReadableAsync([revised], Ct)).Count == 0);
        targets = await TargetsAsync();
        Assert.All(targets, t => Assert.Equal(SessionItemState.Unreachable, t.State));
        Assert.False(targets[0].CanOpen);
        Assert.EndsWith("second printing.pdf", targets[2].LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_book_is_found_by_its_note_and_page_notes_follow_it()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        Copy(pdfs.Scanned, "Scans/Goblin Scan.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        var (known, scan) = (cards[0], cards[1]);
        var notes = new NotesService(_notes, _entries, _queries, _projector);
        var changed = new List<EntryId>();
        notes.PageNotesChanged += (_, id) => changed.Add(id);
        async Task<IReadOnlyList<EntryId>> FindAsync(string query) =>
            [.. (await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse(query)), new LibraryFilter(), ct: Ct)).Select(c => c.EntryId)];

        await notes.SetEntryNoteAsync(scan.EntryId, "Use the lighthouse keeper as the patron", Ct);
        Assert.Equal([scan.EntryId], await FindAsync("lighthouse"));
        Assert.Equal("Use the lighthouse keeper as the patron", await notes.GetEntryNoteAsync(scan.EntryId, Ct));

        // index.db is derived: emptied and projected again, the note is found again.
        await new IndexStore(_writer).SetNotesAsync(null, new Dictionary<EntryId, string>(), Ct);
        Assert.Empty(await FindAsync("lighthouse"));
        await _projector.ProjectAllAsync(Ct);
        Assert.Equal([scan.EntryId], await FindAsync("lighthouse"));

        await notes.SetEntryNoteAsync(scan.EntryId, "", Ct);
        Assert.Empty(await FindAsync("lighthouse"));

        var note = await notes.AddPageNoteAsync(known.DocumentId, 1, 0, "Read this aloud", Ct);
        Assert.Equal((0, 1, known.EntryId), (note!.Range.FirstPdfPage, note.Range.LastPdfPage, note.EntryId));
        Assert.Equal([note], await notes.GetPageNotesForDocumentAsync(known.DocumentId, Ct));
        var updated = await notes.UpdatePageNoteAsync(note, "Read this aloud, slowly", 1, 1, Ct);
        Assert.Equal((1, "Read this aloud, slowly"), (updated!.Range.FirstPdfPage, updated.Text));
        var deleted = await notes.DeletePageNoteAsync(note.Id, Ct);
        Assert.Empty(await notes.GetPageNotesAsync(known.EntryId, Ct));
        Assert.NotNull(await notes.RestorePageNoteAsync(deleted!, Ct));
        Assert.Equal([known.EntryId, known.EntryId, known.EntryId, known.EntryId], changed);
        // Page notes aren't searched yet.
        Assert.Empty(await FindAsync("aloud"));
    }
}
