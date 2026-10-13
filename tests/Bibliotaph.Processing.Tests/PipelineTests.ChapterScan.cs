using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;
using Dapper;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Reading whole books chapter by chapter: after cataloging, in batches of chapters, out of the way of anything the
/// user asks the AI, carried on after a restart, and kept in catalog.db so a rebuilt index.db has it back.
/// </summary>
public sealed partial class PipelineTests
{
    static readonly Regex ChapterLine = new(@"^Chapter (\d+): (.+) \(pages (\d+) to (\d+)\)\s*$", RegexOptions.Multiline);

    /// <summary>
    /// What a model reading <see cref="SyntheticPdfs.ChapterBook"/> would say, by chapter title: values with real quotes
    /// from the chapter's own pages, and in the appendix a creature with a made-up quote and one from another chapter's
    /// page, which the evidence check drops.
    /// </summary>
    static string ChapterAnswer(ModelRequest request)
    {
        if (request.SchemaName != ChapterPrompt.SchemaName) return "{}";
        var chapters = new StringBuilder();
        foreach (Match m in ChapterLine.Matches(request.User))
        {
            var number = m.Groups[1].Value;
            var title = m.Groups[2].Value;
            var values = title switch
            {
                _ when title.Contains("Drowned Road", StringComparison.Ordinal) =>
                    """
                    "environment": [{"value": "Swamp", "page": 2, "quote": "The road sinks into the marsh where bog goblins wait"}],
                    "creature": [{"value": "bog goblin", "page": 2, "quote": "where bog goblins wait in the reeds"}]
                    """,
                _ when title.Contains("(1 of 2)", StringComparison.Ordinal) =>
                    """
                    "environment": [{"value": "mountains", "page": 10, "quote": "Beneath the mountain the dwarven halls lie full of ash"}]
                    """,
                _ when title.Contains("(2 of 2)", StringComparison.Ordinal) =>
                    """
                    "creature": [{"value": "Red dragon", "page": 31, "quote": "A red dragon sleeps on the hoard of the ash king"}]
                    """,
                _ =>
                    """
                    "creature": [
                      {"value": "bog goblin", "page": 46, "quote": "Bog goblins are small, cruel and always hungry"},
                      {"value": "lich", "page": 46, "quote": "A lich rules the marsh from a tower of bone"},
                      {"value": "red dragon", "page": 31, "quote": "A red dragon sleeps on the hoard of the ash king"}
                    ]
                    """,
            };
            if (chapters.Length > 0) chapters.Append(',');
            chapters.Append(CultureInfo.InvariantCulture, $$"""{"chapter": {{number}}, {{values}}}""");
        }
        return $$"""{"chapters": [{{chapters}}]}""";
    }

    IReadOnlyList<ModelRequest> ChapterRequests() => [.. _model.Requests.Where(r => r.SchemaName == ChapterPrompt.SchemaName)];

    async Task StartChapterBookAsync(bool scan = true)
    {
        Copy(pdfs.ChapterBook, "Adventures/The Hollow Crown.pdf");
        await _roots.AddAsync(_library, Ct);
        _model.Answer = ChapterAnswer;
        await _ai.SetScanWholeBooksAsync(scan, Ct);
        await SwitchAiOnAsync();
        await _service.StartAsync(Ct);
    }

    /// <summary>Waits until no book's whole-book scan is waiting or running.</summary>
    async Task SettleScanAsync()
    {
        await SettleClassifyAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (true)
        {
            var progress = await _queries.GetProgressAsync(timeout.Token);
            await using var c = _index.OpenRead();
            var waiting = await c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM stage_status WHERE stage = 'ChapterScan' AND status IN ('Pending', 'Running')");
            var done = await c.ExecuteScalarAsync<long>("SELECT count(*) FROM stage_status WHERE stage = 'ChapterScan'");
            if (waiting == 0 && done > 0 && progress.ToScanWhole == 0) return;
            await Task.Delay(100, timeout.Token);
        }
    }

    Task<IReadOnlyList<LibraryEntry>> FindAsync(string query) =>
        new LibraryQueries(_index).SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse(query)), new LibraryFilter(), ct: Ct);

    async Task<long> ChapterBookDocumentAsync()
    {
        await using var c = _index.OpenRead();
        return await c.QuerySingleAsync<long>("SELECT document_id FROM doc WHERE display_title = @title", new { title = SyntheticPdfs.ChapterBookTitle });
    }

    [Fact]
    public async Task A_whole_book_is_read_chapter_by_chapter_after_cataloging_and_found_through_its_chapters()
    {
        await StartChapterBookAsync();
        await SettleScanAsync();

        // Its own cataloging came first; then two requests, each about 25 pages of chapters.
        var requests = _model.Requests.ToList();
        var first = requests.FindIndex(r => r.SchemaName == ChapterPrompt.SchemaName);
        Assert.True(first > 0 && requests.Take(first).Any(r => r.SchemaName == "book_metadata"), "The book wasn't catalogued before it was scanned.");
        var batches = ChapterRequests();
        Assert.Equal(2, batches.Count);
        // Lines repeated on page after page, as the halls' are, are left out like a watermark.
        Assert.Contains("(pages 10 to 27)", batches[0].User, StringComparison.Ordinal);
        Assert.DoesNotContain("pages 28 to 45", batches[0].User, StringComparison.Ordinal);
        Assert.Contains("(pages 28 to 45)", batches[1].User, StringComparison.Ordinal);
        Assert.Contains("[Page 31]\nA red dragon", batches[1].User.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("</book-excerpt>", batches[1].User, StringComparison.Ordinal);

        // The chapters: the title page joins the first, the long chapter is read in two parts.
        var documentId = await ChapterBookDocumentAsync();
        var chapters = await _chapterService.GetChaptersAsync(documentId, ct: Ct);
        Assert.Equal(
            [
                ("Chapter 1: The Drowned Road", 0, 8), ("Chapter 2: Halls of the Ash King (1 of 2)", 9, 26),
                ("Chapter 2: Halls of the Ash King (2 of 2)", 27, 44), ("Appendix: Creatures", 45, 49),
            ],
            chapters.Select(c => (c.Title, c.FirstPage, c.LastPage)));
        Assert.All(chapters, c => Assert.Equal((ChapterSource.Outline, ChapterReading.Read), (c.Source, c.Reading)));
        Assert.Equal(["creature:bog goblin", "environment:swamp"], chapters[0].Tags.Select(t => $"{t.Field}:{t.Value}").Order(StringComparer.Ordinal));
        Assert.Equal("mountain", Assert.Single(chapters[1].Tags).Value);
        Assert.Equal(("Red dragon", 30), (Assert.Single(chapters[2].Tags).Label, chapters[2].Tags[0].PdfPage));
        // A made-up quote, and a page from another chapter, are dropped.
        Assert.Equal("bog goblin", Assert.Single(chapters[3].Tags).Value);

        // Search finds the book through its chapters; the book's own environment stays unset.
        Assert.Single(await FindAsync("creature:\"bog goblin\""));
        Assert.Single(await FindAsync("environment:marsh"));
        Assert.Empty(await FindAsync("creature:lich"));
        Assert.Single(await FindAsync("environment:unknown"));
        var entryId = await OnlyEntryAsync();
        Assert.Empty((await _metadataStore.GetAsync(entryId, Ct)).Compute()[MetadataFields.Environments].Values);

        // The chapters and pages with a value, for questions about the whole library.
        var hit = Assert.Single(await new ChapterQueries(_index).FindAsync(ChapterFields.Creature, "red dragons", ct: Ct));
        Assert.Equal((entryId, "Chapter 2: Halls of the Ash King (2 of 2)", 30), (hit.EntryId, hit.Chapter, hit.PdfPage));
        Assert.Equal(45, Assert.Single(await new ChapterQueries(_index).FindAsync(ChapterFields.Creature, "Bog Goblin", ct: Ct), h => h.PdfPage == 45).FirstPage);

        // Kept in catalog.db as finished, so it isn't read again.
        var scan = await _chapterScans.GetAsync(Assert.Single(await _libraryStore.GetDocumentHashesAsync(Ct)).ContentHash, Ct);
        Assert.True(scan is { IsFinished: true, Model: "fake-8b", PromptVersion: ChapterPrompt.Version });
    }

    [Fact]
    public async Task Scanning_gives_way_while_someone_is_waiting_on_the_ai_and_carries_on_after()
    {
        var scanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var inFlight = 0;
        var chapterBusyWhenAsked = -1;
        _model.Hold = async (request, ct) =>
        {
            if (request.SchemaName != ChapterPrompt.SchemaName)
            {
                if (request.User != "Who rules the ash halls?") return;
                // The scan's request was stopped the moment this one started.
                var stopped = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (Volatile.Read(ref inFlight) > 0 && DateTime.UtcNow < stopped) await Task.Delay(20, ct);
                chapterBusyWhenAsked = Volatile.Read(ref inFlight);
                return;
            }
            if (Interlocked.Increment(ref held) > 1) return;
            Interlocked.Increment(ref inFlight);
            try
            {
                scanning.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        };
        await StartChapterBookAsync();
        await scanning.Task.WaitAsync(Patience, Ct);

        // Someone asks the AI something, as Ask does, while the scan is reading.
        var answer = await _ai.Chat!.CompleteAsync(new ModelRequest("fake-8b", "You answer questions.", "Who rules the ash halls?", null, 8_192), Ct);
        Assert.Equal("{}", answer);
        Assert.Equal(0, chapterBusyWhenAsked);

        // Once the AI has been quiet for a moment, the scan carries on from the chapters it was reading.
        await SettleScanAsync();
        var batches = ChapterRequests();
        Assert.Equal(3, batches.Count);
        Assert.Equal(batches[0].User, batches[1].User);
        var scan = await _chapterScans.GetAsync(Assert.Single(await _libraryStore.GetDocumentHashesAsync(Ct)).ContentHash, Ct);
        Assert.True(scan is { IsFinished: true });
        Assert.All(scan!.Chapters, c => Assert.Equal(ChapterReading.Read, c.Reading));
    }

    [Fact]
    public async Task A_scan_interrupted_by_closing_the_app_carries_on_where_it_stopped()
    {
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _model.Hold = async (request, ct) =>
        {
            if (request.SchemaName != ChapterPrompt.SchemaName || !request.User.Contains("Appendix", StringComparison.Ordinal)) return;
            second.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        await StartChapterBookAsync();
        await second.Task.WaitAsync(Patience, Ct);
        await _service.StopAsync(Ct);

        // The first batch is kept, in catalog.db and in the index; the rest waits.
        var hash = Assert.Single(await _libraryStore.GetDocumentHashesAsync(Ct)).ContentHash;
        var saved = await _chapterScans.GetAsync(hash, Ct);
        Assert.NotNull(saved);
        Assert.False(saved.IsFinished);
        Assert.Equal([ChapterReading.Read, ChapterReading.Read, ChapterReading.Waiting, ChapterReading.Waiting], saved.Chapters.Select(c => c.Reading));
        Assert.Single(await FindAsync("creature:\"bog goblin\""));

        _model.Hold = null;
        var before = ChapterRequests().Count;
        await _service.StartAsync(Ct);
        await SettleScanAsync();

        // Only what was left is read.
        var again = Assert.Single(ChapterRequests().Skip(before));
        Assert.Contains("Appendix", again.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Drowned Road", again.User, StringComparison.Ordinal);
        Assert.True((await _chapterScans.GetAsync(hash, Ct))!.IsFinished);
    }

    [Fact]
    public async Task What_the_chapters_hold_survives_index_db_being_built_again_without_asking_the_ai()
    {
        await StartChapterBookAsync();
        await SettleScanAsync();
        var asked = _model.Requests.Count;
        await _service.StopAsync(Ct);

        // As a rebuilt index.db: no chapters, no chapter facets, no scan jobs.
        await _writer.WriteAsync((c, t) => c.Execute(
            """
            DELETE FROM chapter; DELETE FROM chapter_tag; DELETE FROM entry_facet WHERE field LIKE 'inside:%';
            DELETE FROM job WHERE stage = 'ChapterScan'; DELETE FROM stage_status WHERE stage = 'ChapterScan';
            """, transaction: t), Ct);
        Assert.Empty(await FindAsync("creature:\"bog goblin\""));

        // Startup copies them back from catalog.db...
        Assert.Equal(1, await _chapterProjection.RestoreAsync(Ct));
        Assert.Single(await FindAsync("creature:\"bog goblin\""));
        Assert.Equal(4, (await _chapterService.GetChaptersAsync(await ChapterBookDocumentAsync(), ct: Ct)).Count);

        // ...and the scan queued again for the book finds it finished, and asks nothing.
        await _service.StartAsync(Ct);
        await SettleScanAsync();
        Assert.Equal(asked, _model.Requests.Count);
        Assert.Single(await FindAsync("environment:swamp"));
    }

    [Fact]
    public async Task Scanning_switched_off_reads_nothing_until_it_is_switched_on_and_scan_again_reads_the_book_again()
    {
        await StartChapterBookAsync(scan: false);
        await SettleScanAsync();
        Assert.Empty(ChapterRequests());
        await using (var c = _index.OpenRead())
            Assert.Equal("Skipped", StatusOf(c, SyntheticPdfs.ChapterBookTitle, Stage.ChapterScan));

        await _chapterService.SetOnAsync(true, Ct);
        await SettleScanAsync();
        Assert.Equal(2, ChapterRequests().Count);

        // Scan again forgets what was found and reads the book from the start.
        var documentId = await ChapterBookDocumentAsync();
        await _chapterService.RescanAsync(documentId, Ct);
        await SettleScanAsync();
        Assert.Equal(4, ChapterRequests().Count);
        Assert.Equal(4, (await _chapterService.GetChaptersAsync(documentId, ct: Ct)).Count);
    }
}
