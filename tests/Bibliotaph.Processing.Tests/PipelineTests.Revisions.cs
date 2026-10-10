using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Slice 4h: a PDF replaced in place by a revised edition (A08), and the places in it that follow.</summary>
public sealed partial class PipelineTests
{
    /// <summary>
    /// A stage that waits while its gate is held, as a slow Match would, so a test can look at a new version before
    /// its pages have fingerprints.
    /// </summary>
    sealed class HeldStage(IStage inner, HeldStage.Gate gate) : IStage
    {
        public sealed class Gate
        {
            public volatile bool Held;
        }

        public Stage Stage => inner.Stage;

        public Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct) =>
            gate.Held ? Task.FromResult<StageOutcome>(new StageOutcome.Later(TimeSpan.FromMilliseconds(200), "Held by the test.")) : inner.RunAsync(job, ct);
    }

    /// <summary>Polls until <paramref name="done"/>, without waiting for the queue to empty, which a held stage keeps from happening.</summary>
    static async Task PollAsync(Func<Task<bool>> done)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (!await done()) await Task.Delay(100, timeout.Token);
    }

    [Fact]
    public async Task A_revised_edition_replacing_a_book_in_place_moves_its_places_and_lists_the_rest_for_a_look()
    {
        var path = Path.Combine(_library, "Purchases", "Drowned Abbey.pdf");
        Copy(pdfs.WatermarkedForAna, Path.Combine("Purchases", "Drowned Abbey.pdf"));
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var book = Assert.Single(await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct));
        var sessions = new SessionsService(_sessions, _entries, _libraryStore, _queries, _projector, _places);
        var notes = new NotesService(_notes, _entries, _projector, _places);
        var reading = new ReadingService(_reading, _projector, _places);
        var review = new ReviewService(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
            new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs, places: _places);

        // A session item and a page note on a page the new edition keeps, and on the one it corrects, and the
        // book left at the bell tower.
        var pack = await sessions.CreateAsync("The midnight bell", ct: Ct);
        var appendix = (await sessions.AddPagesAsync(pack.Id, book.DocumentId, 4, 4, ct: Ct))!.Value;
        var crypt = (await sessions.AddPagesAsync(pack.Id, book.DocumentId, 3, 3, "Crypt", ct: Ct))!.Value;
        var bellNote = (await notes.AddPageNoteAsync(book.DocumentId, 2, 2, "Ring the bell at low tide", Ct))!;
        var cryptNote = (await notes.AddPageNoteAsync(book.DocumentId, 3, 3, "The ghost lies about the tomb", Ct))!;
        await reading.RecordOpenAsync(book.DocumentId, Ct);
        await reading.SavePositionAsync(book.DocumentId, 2, Ct);

        async Task<IReadOnlyDictionary<long, SessionItemTarget>> ItemsAsync() =>
            await sessions.ResolveAsync((await sessions.GetAsync(pack.Id, Ct))!.Items, Ct);
        async Task<IReadOnlyDictionary<long, PagePlace>> NotesAsync() =>
            await notes.ResolveAsync(await notes.GetPageNotesAsync(book.EntryId, Ct), Ct);

        // The revised edition is saved over the file while Match is held, so it is read but has no fingerprints yet.
        _matchGate.Held = true;
        File.Copy(pdfs.ExpandedForAna, path, overwrite: true);
        _service.RequestScan();
        long revised = 0;
        await PollAsync(async () =>
            await _entries.GetCurrentDocumentAsync(book.EntryId, Ct) is { } current && current != book.DocumentId
            && await _queries.GetStageStatusAsync(revised = current, Stage.Text, Ct) == StageStatus.Complete);

        // It is the book's current version at once, but nothing is judged before its fingerprints exist (choice 1).
        Assert.Equal(StageStatus.Pending, await _queries.GetStageStatusAsync(revised, Stage.Match, Ct));
        var items = await ItemsAsync();
        Assert.Equal(new SessionItemTarget(revised, 4, 4, SessionItemState.Pending, PagePlaces.PendingReason), items[appendix]);
        Assert.Equal(new SessionItemTarget(revised, 3, 3, SessionItemState.Pending, PagePlaces.PendingReason), items[crypt]);
        var placed = await NotesAsync();
        Assert.All(placed.Values, p => Assert.Equal(PagePlaceState.Pending, p.State));
        Assert.Empty(await _pageRefs.GetCheckedAsync(ct: Ct));
        Assert.Empty((await review.GetQueueAsync(Ct)).Revisions!);
        Assert.Equal(0, await _places.CountRevisionsAsync(Ct));
        Assert.Equal((book.DocumentId, 2), await _reading.GetPlaceAsync(revised, Ct));

        // Match reads it: what kept its page is found there, one page on; the corrected crypt needs a look, and
        // opens at the same page number meanwhile, marked changed.
        _matchGate.Held = false;
        await SettleAsync();
        var checks = (await _pageRefs.GetForEntryAsync(book.EntryId, Ct)).ToDictionary(p => (p.Kind, p.OwnerId), p => p.Range.Check);
        Assert.Equal(new PageRefCheck(PageCheck.Found, revised, 5, 5), checks[(PlaceKind.SessionItem, appendix)]);
        Assert.Equal(new PageRefCheck(PageCheck.NeedsLook, revised, 3, 3), checks[(PlaceKind.SessionItem, crypt)]);
        Assert.Equal(new PageRefCheck(PageCheck.Found, revised, 3, 3), checks[(PlaceKind.PageNote, bellNote.Id)]);
        Assert.Equal(new PageRefCheck(PageCheck.NeedsLook, revised, 3, 3), checks[(PlaceKind.PageNote, cryptNote.Id)]);
        items = await ItemsAsync();
        Assert.Equal(new SessionItemTarget(revised, 5, 5, SessionItemState.OtherCopy), items[appendix]);
        Assert.Equal(new SessionItemTarget(revised, 3, 3, SessionItemState.Changed, PagePlaces.ChangedReason), items[crypt]);
        placed = await NotesAsync();
        Assert.Equal(new PagePlace(revised, 3, 3, PagePlaceState.OtherCopy), placed[bellNote.Id]);
        Assert.Equal(new PagePlace(revised, 3, 3, PagePlaceState.Changed, PagePlaces.ChangedReason), placed[cryptNote.Id]);

        // One card for the new version: two places found their page, two need a look, those first.
        var card = Assert.Single((await review.GetQueueAsync(Ct)).Revisions!);
        Assert.Equal((book.EntryId, revised, 2, 2), (card.EntryId, card.DocumentId, card.Found, card.NeedLook));
        Assert.Equal([(PlaceKind.SessionItem, crypt), (PlaceKind.PageNote, cryptNote.Id), (PlaceKind.SessionItem, appendix), (PlaceKind.PageNote, bellNote.Id)],
            card.Places.Select(p => (p.Place.Kind, p.Place.OwnerId)));
        Assert.Equal(1, await _places.CountRevisionsAsync(Ct));

        // Where the book was left moved to the bell tower's page in the new version (choice 5).
        Assert.Equal((revised, 3), await _reading.GetPlaceAsync(revised, Ct));
        Assert.Equal(3, await reading.GetPositionAsync(revised, Ct));

        // Use this page on the card for the item, and in the reader for the note, at the page the user went to.
        var undo = await review.UsePlaceAsync(card.Places[0], Ct);
        Assert.NotNull(undo);
        Assert.Equal(new SessionItemTarget(revised, 3, 3, SessionItemState.Ready), (await ItemsAsync())[crypt]);
        card = Assert.Single((await review.GetQueueAsync(Ct)).Revisions!);
        Assert.Equal((2, 1), (card.Found, card.NeedLook));
        Assert.Equal(PageCheck.Checked, card.Places.Single(p => p.Place.OwnerId == crypt && p.Place.Kind == PlaceKind.SessionItem).Outcome);
        Assert.NotNull(await notes.UsePageAsync(cryptNote.Id, revised, 4, 4, Ct));
        Assert.Equal(new PagePlace(revised, 4, 4, PagePlaceState.Ready), (await NotesAsync())[cryptNote.Id]);
        Assert.Empty((await review.GetQueueAsync(Ct)).Revisions!);
        Assert.Equal(0, await _places.CountRevisionsAsync(Ct));

        // Undo puts the item back as it was, needing a look, and the card with it.
        await undo();
        Assert.Equal(new SessionItemTarget(revised, 3, 3, SessionItemState.Changed, PagePlaces.ChangedReason), (await ItemsAsync())[crypt]);
        card = Assert.Single((await review.GetQueueAsync(Ct)).Revisions!);
        Assert.Equal((2, 1), (card.Found, card.NeedLook));

        // Looking again, as when the version is made current once more, keeps what the user chose.
        await _places.CheckAsync(revised, ct: Ct);
        Assert.Equal(new PagePlace(revised, 4, 4, PagePlaceState.Ready), (await NotesAsync())[cryptNote.Id]);
        Assert.Equal((revised, 3), await _reading.GetPlaceAsync(revised, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_replaced_with_the_same_size_and_date_is_read_again_when_its_id_changed_on_a_local_drive(bool network)
    {
        var path = Path.Combine(_library, "Handout Map.png");
        var date = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(_library);
        File.WriteAllBytes(path, FakeCodec.Png(640, 480));
        File.SetLastWriteTimeUtc(path, date);
        _identity.Disk(_library, "1234ABCD", network);
        var root = (await _roots.AddAsync(_library, Ct)).Id;
        await _service.StartAsync(Ct);
        await SettleAsync();
        var first = Assert.Single(await FileLocationsAsync());
        Assert.NotNull(first.NtfsFileId);

        // Other content written into the same file, keeping its size and date, looks unchanged: the file is the
        // same one, so nothing says to read it again.
        File.WriteAllBytes(path, FakeCodec.Png(480, 640));
        File.SetLastWriteTimeUtc(path, date);
        Assert.Equal(first.SizeBytes, new FileInfo(path).Length);
        await ScanAsync(root);
        await SettleAsync();
        var kept = Assert.Single(await FileLocationsAsync());
        Assert.Equal((first.DocumentId, first.ContentHash), (kept.DocumentId, kept.ContentHash));

        // Another file saved over it, with the same size and date but a new ID, is new content on a local drive
        // (choice 6). On a network share known files' IDs aren't read on every scan, so it goes unseen there.
        _identity.Renew(path);
        if (network)
        {
            await ScanAsync(root);
            await SettleAsync();
            var after = Assert.Single(await FileLocationsAsync());
            Assert.Equal((first.DocumentId, first.ContentHash, first.NtfsFileId), (after.DocumentId, after.ContentHash, after.NtfsFileId));
            return;
        }
        await ScanAsync(root);
        await SettleUntilAsync(async () => Assert.Single(await FileLocationsAsync()).DocumentId is { } d && d != first.DocumentId);
        var replaced = Assert.Single(await FileLocationsAsync());
        Assert.NotEqual(first.ContentHash, replaced.ContentHash);
        Assert.NotEqual(first.NtfsFileId, replaced.NtfsFileId);
        Assert.Equal((first.SizeBytes, first.ModifiedUtc), (replaced.SizeBytes, replaced.ModifiedUtc));
    }
}
