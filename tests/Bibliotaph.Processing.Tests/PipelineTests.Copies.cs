using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Dapper;

namespace Bibliotaph.Processing.Tests;

/// <summary>Copies (F2): two files of one book, watermarked for different buyers, become one card with two copies.</summary>
public sealed partial class PipelineTests
{
    [Fact]
    public async Task Watermarked_copies_of_a_book_become_one_card_and_not_the_same_book_parts_them_for_good()
    {
        Copy(pdfs.WatermarkedForAna, "Purchases/Drowned Abbey.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var card = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        // The user sets a tag and a title on the first copy's card before the second file arrives.
        Assert.Null(await _metadata.SetAsync(card.EntryId, MetadataFields.Tags, "Friday game", Ct));
        Assert.Null(await _metadata.SetAsync(card.EntryId, MetadataFields.Title, "The Drowned Abbey", Ct));

        Copy(pdfs.WatermarkedForDale, "Backup/Drowned Abbey (1).pdf");
        _service.RequestScan();
        await SettleUntilAsync(async () => (await search.ListAsync(new LibraryFilter(), ct: Ct)) is [{ Copies: 2 }]);

        // One card, still showing the first copy, with the user's work; page hits come from that copy only.
        var joined = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal((card.EntryId, card.DocumentId, "The Drowned Abbey", 2), (joined.EntryId, joined.DocumentId, joined.Title, joined.Copies));
        var hits = await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("bells")), new LibraryFilter(), ct: Ct);
        Assert.Equal(card.DocumentId, Assert.Single(Assert.Single(hits.Entries).Pages).DocumentId);
        Assert.Equal([card.EntryId], (await search.ListAsync(new LibraryFilter(OnlyWithCopies: true), ct: Ct)).Select(e => e.EntryId));
        var copies = new CopiesService(_entries, _libraryStore, _projector);
        var list = await copies.GetAsync(card.EntryId, Ct);
        Assert.Equal([true, false], list.Select(c => c.Copy.IsCurrent));
        var backup = list[1].Copy;
        Assert.True(backup.Joined);
        Assert.EndsWith("Drowned Abbey (1).pdf", Assert.Single(list[1].Locations).FullPath, StringComparison.Ordinal);

        // Make current opens the backup instead, without touching any file.
        await copies.MakeCurrentAsync(card.EntryId, backup.DocumentId, Ct);
        Assert.Equal(backup.DocumentId, Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).DocumentId);

        // Not the same book: the backup gets its own card back, and Match doesn't join them again.
        var split = await copies.NotSameBookAsync(card.EntryId, backup.DocumentId, Ct);
        Assert.NotNull(split);
        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        Assert.Equal(2, cards.Count);
        Assert.Equal(card.DocumentId, cards.Single(c => c.EntryId == card.EntryId).DocumentId);
        Assert.Equal(backup.DocumentId, cards.Single(c => c.EntryId == split.Value).DocumentId);
        Assert.All(cards, c => Assert.Equal(1, c.Copies));
        Assert.Equal("The Drowned Abbey", cards.Single(c => c.EntryId == card.EntryId).Title);
        Assert.True(File.Exists(Path.Combine(_library, "Backup", "Drowned Abbey (1).pdf")));

        await _service.RerunAsync(Stage.Match, Ct);
        await SettleAsync();
        Assert.Equal(2, (await search.ListAsync(new LibraryFilter(), ct: Ct)).Count);
    }

    [Fact]
    public async Task Books_indexed_before_copies_existed_are_matched_from_their_stored_text()
    {
        Copy(pdfs.WatermarkedForAna, "Purchases/Drowned Abbey.pdf");
        Copy(pdfs.WatermarkedForDale, "Backup/Drowned Abbey (1).pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        Assert.Equal(2, Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Copies);

        // As a library from before F2 looks: no Match jobs, no fingerprints, a card per file.
        await _service.StopAsync(Ct);
        await _writer.WriteAsync((c, t) => c.Execute(
            "DELETE FROM job WHERE stage = 'Match'; DELETE FROM stage_status WHERE stage = 'Match'; UPDATE page SET fingerprint = NULL;", transaction: t), Ct);
        var only = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        var backup = (await new CopiesService(_entries, _libraryStore, _projector).GetAsync(only.EntryId, Ct)).Single(c => !c.Copy.IsCurrent).Copy.DocumentId;
        await using (var db = _catalog.CreateContext())
        {
            // Undo the join by hand rather than with Not the same book, which would also remember the pair.
            var join = db.EntryJoins.Single();
            var source = db.EntrySources.Single(s => s.DocumentId == backup);
            source.EntryId = join.JoinedEntryId;
            source.IsCurrent = true;
            db.Entries.Single(e => e.Id == join.JoinedEntryId).MergedIntoEntryId = null;
            db.EntryJoins.Remove(join);
            await db.SaveChangesAsync(Ct);
        }
        await _projector.ProjectAllAsync(Ct);
        Assert.Equal(2, (await search.ListAsync(new LibraryFilter(), ct: Ct)).Count);

        await _service.StartAsync(Ct);
        await SettleUntilAsync(async () => (await search.ListAsync(new LibraryFilter(), ct: Ct)).Count == 1);

        Assert.Equal(2, Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Copies);
        await using var read = _index.OpenRead();
        Assert.Equal(0, read.ExecuteScalar<long>("SELECT count(*) FROM page WHERE text <> '' AND fingerprint IS NULL"));
    }

    /// <summary>Waits until the queue is idle and <paramref name="done"/> holds.</summary>
    async Task SettleUntilAsync(Func<Task<bool>> done)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (true)
        {
            await SettleAsync();
            if (await done()) return;
            await Task.Delay(200, timeout.Token);
        }
    }
}
