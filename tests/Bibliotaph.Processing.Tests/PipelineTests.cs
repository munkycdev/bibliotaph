using System.Buffers.Binary;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Pdf.Host.Tests;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// A library folder of synthetic files through the real pipeline: scan, hash, Probe, Text, Covers and OCR, with
/// the real PDF worker, catalog.db and index.db on disk. Only image encoding is faked.
/// </summary>
public sealed partial class PipelineTests(SyntheticPdfs pdfs) : IAsyncLifetime
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-pipeline-").FullName;
    AppPaths _paths = null!;
    string _library = null!;
    CatalogDatabase _catalog = null!;
    IndexDatabase _index = null!;
    IndexWriter _writer = null!;
    PdfWorkerPool _workers = null!;
    IndexQueries _queries = null!;
    SourceRootStore _roots = null!;
    LibraryStore _libraryStore = null!;
    EntryStore _entries = null!;
    IndexingService _service = null!;
    MetadataService _metadata = null!;
    MetadataStore _metadataStore = null!;
    MetadataProjector _projector = null!;
    PackService _packs = null!;
    MetadataHints _hints = null!;
    VocabularyStore _vocabulary = null!;
    SettingsStore _settings = null!;
    readonly FakeModel _model = new();
    AiSettings _ai = null!;
    ClassificationStore _runs = null!;
    CopiesService _copies = null!;
    ElsewhereService _elsewhere = null!;
    SourceFiles _sources = null!;
    FavoriteStore _favorites = null!;
    ReadingStore _reading = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _paths = new AppPaths(Path.Combine(_dir, "data"));
        foreach (var directory in _paths.Directories) Directory.CreateDirectory(directory);
        _library = Path.Combine(_dir, "RPG Library");

        _catalog = new CatalogDatabase(_paths.CatalogDatabase, _paths.Backups);
        await _catalog.MigrateAsync(Ct);
        var contexts = new Factory(_catalog);
        _index = IndexDatabase.ForFile(_paths.IndexDatabase);
        await _index.InitializeAsync(Ct);
        _writer = new IndexWriter(_index);
        await _writer.StartAsync(Ct);

        _workers = new PdfWorkerPool(new PdfWorkerPoolOptions());
        _queries = new IndexQueries(_index);
        _roots = new SourceRootStore(contexts);
        var library = _libraryStore = new LibraryStore(contexts);
        var entries = _entries = new EntryStore(contexts);
        var queue = new JobBoard(_writer, _index);
        var reader = new SourceFileReader();
        var index = new IndexStore(_writer);
        var archives = new ArchiveReader(reader);
        _sources = new SourceFiles(_paths, archives, new DiskSpace());
        var vocabulary = _vocabulary = new VocabularyStore(contexts);
        await vocabulary.SeedAsync(Ct);
        _metadataStore = new MetadataStore(contexts);
        _settings = new SettingsStore(contexts);
        _runs = new ClassificationStore(contexts);
        _favorites = new FavoriteStore(contexts);
        _reading = new ReadingStore(contexts, entries);
        var projector = _projector = new MetadataProjector(_metadataStore, entries, vocabulary, index, _queries, _settings, runs: _runs,
            favorites: _favorites, reading: _reading);
        var services = new StageServices(library, entries, index, _queries, _workers, reader, new FakeCodec(), new CoverCache(_paths), new NoPasswords(), _sources, projector);
        _metadata = new MetadataService(_metadataStore, vocabulary, projector);
        await projector.ProjectAllAsync(Ct); // as the app does at startup
        var versions = new VersionStore(contexts, entries);
        _copies = new CopiesService(entries, versions, library, _queries, projector);
        var elsewhere = new ElsewhereStore(contexts, entries);
        _elsewhere = new ElsewhereService(entries, elsewhere, _metadata, vocabulary, projector, library, _queries, new LibraryQueries(_index));
        var packStore = new PackStore(contexts);
        var hints = _hints = new MetadataHints(library, entries, _queries, _metadataStore, vocabulary, projector, packStore);
        // AI is off, as it is until someone sets it up; the model is a fake that answers as each test says.
        _ai = new AiSettings(_settings, new NoApiKeys(), _ => _model);
        await _ai.LoadAsync(Ct);
        var classify = new ClassifyStage(_ai, library, _queries, new ClassifierInputs(library, _queries, vocabulary), _runs,
            new ClassificationResults(_runs, entries, _metadataStore, vocabulary, projector));
        _service = new IndexingService(_roots, library, queue,
            [
                new ProbeStage(services), new TextStage(services), new CoversStage(services), new RuleHintsStage(hints), new OcrStage(services), classify,
                new MatchStage(entries, versions, index, _queries, projector, elsewhere: elsewhere),
            ],
            new FileHasher(reader), new DiskSpace(), archives,
            new IndexingOptions { WatchFolders = false, IdleRecheck = TimeSpan.FromSeconds(1), UnavailableRetry = TimeSpan.FromMilliseconds(300) },
            packs: _packs = new PackService(packStore, projector, hints));
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        await _writer.StopAsync(CancellationToken.None);
        _writer.Dispose();
        await _workers.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    void Copy(string source, string relative)
    {
        var target = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }

    /// <summary>Waits until nothing is waiting to be scanned, hashed or processed.</summary>
    async Task<IndexProgress> SettleAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (true)
        {
            await Task.Delay(200, timeout.Token);
            var summary = await _queries.GetQueueSummaryAsync(timeout.Token);
            var progress = await _queries.GetProgressAsync(timeout.Token);
            var counts = await _libraryStore.GetCountsAsync(timeout.Token);
            // Hashing attaches a document in catalog.db before it queues the document's jobs in index.db, so idle
            // also means every catalog document has reached index.db.
            if (!_service.IsScanning && counts.Files > 0 && counts.Unhashed == 0 && progress.Documents == counts.Documents
                && summary.IsIdle && progress.Processing == 0) return progress;
        }
    }

    static string? StatusOf(SqliteConnection c, string title, Stage stage) => c.ExecuteScalar<string?>(
        "SELECT s.status FROM stage_status s JOIN doc d ON d.document_id = s.document_id WHERE d.display_title = @title AND s.stage = @stage",
        new { title, stage = stage.ToString() });

    [Fact]
    public async Task A_new_library_folder_becomes_searchable()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        Copy(pdfs.KnownText, "Backup/known text copy.pdf");
        Copy(pdfs.Scanned, "Scans/Goblin Scan.pdf");
        Copy(pdfs.PasswordProtected, "Locked Book.pdf");
        Copy(pdfs.Truncated, "Damaged Download.pdf");
        File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
        File.WriteAllText(Path.Combine(_library, "notes.txt"), "not indexed");
        await _roots.AddAsync(_library, Ct);

        await _service.StartAsync(Ct);
        var progress = await SettleAsync();

        // Five distinct contents; the copy is the same document.
        Assert.Equal(5, (await _queries.GetProgressAsync(Ct)).Documents);
        await using var c = _index.OpenRead();

        // Text: the phrase is found on its page, and the title comes from the file name.
        var (title, page) = c.QuerySingle<(string Title, long Page)>(
            "SELECT d.display_title, p.pdf_page FROM page_fts JOIN page p ON p.id = page_fts.rowid JOIN doc d ON d.document_id = p.document_id WHERE page_fts MATCH @q",
            new { q = "\"owlbear waits\"" });
        Assert.Equal(SyntheticPdfs.KnownPhrasePage, page);
        Assert.Contains(title, new[] { "Known Text", "Known Text Copy" });
        Assert.Equal(2, c.ExecuteScalar<long>("SELECT count(*) FROM outline JOIN doc USING (document_id) WHERE display_title = @title", new { title }));

        // The Library's own search finds it too, over the documents catalog.db says are visible.
        var search = new LibraryQueries(_index);
        var filter = new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
        var hits = await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("owlbear")), filter, ct: Ct);
        var hit = Assert.Single(Assert.Single(hits.Entries).Pages);
        Assert.Equal(SyntheticPdfs.KnownPhrasePage, hit.PdfPage);
        Assert.Contains($"{LibraryQueries.HitStart}owlbear{LibraryQueries.HitEnd}", hit.Snippet, StringComparison.Ordinal);
        Assert.Contains(await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("format:png")), filter, ct: Ct),
            e => e.Title == "Handout Map");

        // The image-only page of the known text is flagged for OCR along with the scan.
        Assert.Equal("Complete", StatusOf(c, title, Stage.Text));
        Assert.Equal("Complete", StatusOf(c, title, Stage.Covers));
        Assert.True(File.Exists(Path.Combine(_paths.Cache, "covers", c.ExecuteScalar<string>("SELECT cover FROM doc WHERE display_title = @t", new { t = title })!)));

        // OCR: read where Windows OCR has a language; blocked with a reason everywhere else.
        var ocr = StatusOf(c, "Goblin Scan", Stage.Ocr);
        if (ocr == "Complete")
            Assert.Contains("goblin", c.ExecuteScalar<string>("SELECT p.text FROM page p JOIN doc d USING (document_id) WHERE d.display_title = 'Goblin Scan'"), StringComparison.OrdinalIgnoreCase);
        else
            Assert.Equal("Blocked", ocr);

        // The image: probed for its size, no text stage, a cover from the image itself.
        Assert.Equal((640L, 480L), c.QuerySingle<(long, long)>("SELECT width_px, height_px FROM doc WHERE display_title = 'Handout Map'"));
        Assert.Equal("Skipped", StatusOf(c, "Handout Map", Stage.Text));
        Assert.Equal("Complete", StatusOf(c, "Handout Map", Stage.Covers));

        // Files needing attention: the password and the damaged file, each with a reason a person can act on.
        var attention = await _queries.GetAttentionAsync(Ct);
        var locked = attention.Single(a => a.Stage == Stage.Probe && a.Status == StageStatus.Blocked);
        Assert.Contains("Password", locked.Reason, StringComparison.Ordinal);
        Assert.Contains(attention, a => a.Stage == Stage.Probe && a.Status == StageStatus.Failed);
        Assert.Equal(progress.NeedAttention, attention.Select(a => a.DocumentId).Distinct().Count());
    }

    [Fact]
    public async Task A_paused_lane_does_nothing_until_it_resumes()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        await _roots.AddAsync(_library, Ct);
        _service.Pause(Lane.Index);

        await _service.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(0, (await _queries.GetProgressAsync(Ct)).Documents);

        _service.Resume(Lane.Index);
        var progress = await SettleAsync();

        Assert.Equal(1, progress.Searchable);
    }

    [Fact]
    public async Task A_document_finished_before_a_restart_is_not_processed_again()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        await _service.StopAsync(Ct);
        var before = await JobsAsync();

        await _service.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await SettleAsync();

        Assert.Equal(before, await JobsAsync());
    }

    [Fact]
    public async Task Stages_blocked_while_a_file_could_not_be_read_run_once_it_can_be()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();

        // As if Covers had run while the folder was briefly unreadable; then the map goes for good.
        await _writer.WriteAsync((c, t) =>
        {
            c.Execute("UPDATE job SET status = 'blocked', last_error = @reason WHERE stage = 'Covers'", new { reason = StageOutcome.Blocked.Unreachable }, t);
            c.Execute("UPDATE stage_status SET status = 'Blocked' WHERE stage = 'Covers'", transaction: t);
        }, Ct);
        File.Delete(Path.Combine(_library, "Handout Map.png"));
        _service.RequestScan();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        await using var read = _index.OpenRead();
        while (StatusOf(read, "Known Text", Stage.Covers) != "Complete") await Task.Delay(200, timeout.Token);
        Assert.Equal("Blocked", StatusOf(read, "Handout Map", Stage.Covers));
    }

    [Fact]
    public async Task Hints_from_names_reach_the_library_and_the_users_correction_survives_the_index_being_rebuilt()
    {
        Copy(pdfs.KnownText, "D&D 5e/Adventures/Known Text (Levels 1-3).pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();

        var search = new LibraryQueries(_index);
        var entry = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal(("D&D 5e", "Adventure", "Levels 1–3"), (entry.System, entry.Kind, entry.Levels));
        Assert.Contains(await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("system:5e type:adventure level:2")), new LibraryFilter(), ct: Ct),
            e => e.EntryId == entry.EntryId);
        var metadata = (await _metadataStore.GetAsync(entry.EntryId, Ct)).Compute();
        Assert.Equal(AssertionOrigin.Folder, metadata[Core.Metadata.MetadataFields.Edition].First!.Origin);

        // The user says it is a bestiary, not an adventure.
        Assert.Null(await _metadata.SetAsync(entry.EntryId, Core.Metadata.MetadataFields.Types, "Bestiary", Ct));
        Assert.Equal("Bestiary", Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Kind);

        // index.db loses its metadata and hint jobs, as a rebuild would; the hints run again on the next start.
        await _service.StopAsync(Ct);
        await _writer.WriteAsync((c, t) => c.Execute(
            "DELETE FROM entry_meta; DELETE FROM entry_facet; DELETE FROM job WHERE stage = 'RuleHints'; DELETE FROM stage_status WHERE stage = 'RuleHints';", transaction: t), Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();

        entry = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal(("D&D 5e", "Bestiary"), (entry.System, entry.Kind));
    }

    [Fact]
    public async Task Reprocessing_reads_the_file_again_keeps_the_book_searchable_and_keeps_what_the_user_said()
    {
        Copy(pdfs.KnownText, "D&D 5e/Adventures/Known Text (Levels 1-3).pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var book = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        var (id, entryId) = (book.DocumentId, book.EntryId);

        // The user keeps the folder's edition, says it isn't an adventure, and adds a tag of their own. (Notes, which
        // arrive in slice 3, are user data in catalog.db like these.)
        await _metadata.ConfirmAsync(entryId, Core.Metadata.MetadataFields.Edition, Ct);
        await _metadata.RejectAsync(entryId, Core.Metadata.MetadataFields.Types, "adventure", Ct);
        Assert.Null(await _metadata.SetAsync(entryId, Core.Metadata.MetadataFields.Tags, "Friday game", Ct));
        // The phrase's page comes out garbled, as a bad text layer or an old pipeline bug would leave it.
        await _writer.WriteAsync((c, t) => c.Execute("UPDATE page SET text = 'xq zv garbled' WHERE pdf_page = @page", new { page = SyntheticPdfs.KnownPhrasePage }, t), Ct);
        var filter = new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
        async Task<bool> FoundAsync(string words) =>
            (await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse(words)), filter, ct: Ct)).Entries.Any(d => d.Entry.EntryId == entryId);
        Assert.False(await FoundAsync("owlbear"));

        Assert.True(await _service.ReprocessAsync(id, ct: Ct));

        // Found by its contents page the whole time it is being read again.
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            timeout.CancelAfter(Patience);
            while (!(await _queries.GetQueueSummaryAsync(timeout.Token)).IsIdle)
            {
                Assert.True(await FoundAsync("credits"), "The book dropped out of search while it was reprocessed.");
                await Task.Delay(20, timeout.Token);
            }
        }
        await SettleAsync();

        Assert.True(await FoundAsync("owlbear"));
        Assert.True(await FoundAsync("credits"));
        await using (var c = _index.OpenRead())
        {
            foreach (var stage in new[] { Stage.Probe, Stage.Text, Stage.Covers, Stage.RuleHints })
                Assert.Equal("Complete", c.ExecuteScalar<string>("SELECT status FROM stage_status WHERE document_id = @id AND stage = @stage", new { id, stage = stage.ToString() }));
        }

        var metadata = (await _metadataStore.GetAsync(entryId, Ct)).Compute();
        Assert.True(metadata[Core.Metadata.MetadataFields.Edition].First?.Confirmed);
        Assert.DoesNotContain(metadata[Core.Metadata.MetadataFields.Types].Values, v => v.Normalized == "adventure");
        Assert.DoesNotContain(metadata[Core.Metadata.MetadataFields.Types].Alternatives, v => v.Normalized == "adventure");
        Assert.Equal(["Friday game"], metadata[Core.Metadata.MetadataFields.Tags].Values.Select(v => v.Value));
        var entry = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal("D&D 5e", entry.System);
        Assert.NotEqual("Adventure", entry.Kind);
    }

    [Fact]
    public async Task A_type_split_out_in_the_vocabulary_reaches_books_through_their_folder_names()
    {
        Copy(pdfs.KnownText, "One Shots/Known Text.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        Assert.Equal("Adventure", Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Kind);

        // Settings > Vocabulary: One-shot becomes a type of its own, taking "one shot" and "one shots" from Adventure.
        var vocabulary = new VocabularyService(_vocabulary, _projector, _service, _hints);
        var added = await vocabulary.AddTermAsync("type", "One-shot", Ct);
        Assert.Null((await vocabulary.AddAliasAsync(added.TermId!.Value, "one shots", Ct)).Problem);
        await vocabulary.RefreshAsync(Ct);
        await SettleAsync();

        Assert.Equal("One-shot", Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Kind);
    }

    [Fact]
    public async Task Needs_review_counts_cards_and_a_decision_can_be_undone()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var review = new ReviewService(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
            new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs);
        Assert.Equal(0, await review.CountAsync(Ct));

        // Reviewing everything: the title and the type nobody has confirmed are cards now.
        await review.SetReviewAllAsync(true, Ct);
        var cards = (await review.GetQueueAsync(Ct)).Items;
        Assert.Equal(cards.Count, await review.CountAsync(Ct));
        var type = Assert.Single(cards, c => c.Issue.Field == Core.Metadata.MetadataFields.Types);
        Assert.Equal(Core.Metadata.ReviewKind.Suggestion, type.Issue.Kind);

        var undo = await review.AcceptAsync(type, Ct);
        Assert.Equal(cards.Count - 1, await review.CountAsync(Ct));
        Assert.DoesNotContain((await review.GetQueueAsync(Ct)).Items, c => c.Issue.Field == Core.Metadata.MetadataFields.Types);

        await review.UndoAsync([undo], Ct);
        Assert.Equal(cards.Count, await review.CountAsync(Ct));

        await review.SetReviewAllAsync(false, Ct);
        Assert.Equal(0, await review.CountAsync(Ct));
    }

    [Fact]
    public async Task A_bulk_edit_reaches_search_on_every_book_survives_the_hints_and_undo_takes_it_back()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        Copy(pdfs.Scanned, "Adventures/Goblin Scan.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var ids = (await search.ListAsync(new LibraryFilter(), ct: Ct)).Select(e => e.EntryId).OrderBy(e => e.Value).ToList();
        Assert.Equal(2, ids.Count);
        async Task<List<EntryId>> FindAsync(string words) =>
            [.. (await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse(words)), new LibraryFilter(), ct: Ct)).Select(e => e.EntryId).OrderBy(e => e.Value)];
        Assert.Equal(ids, await FindAsync("type:adventure"));

        // A value that can't be stored stops the whole edit before anything is saved.
        var (problem, none) = await _metadata.EditManyAsync(ids,
            [new BulkChange(Core.Metadata.MetadataFields.Tags, BulkAction.Add, "Friday game"), new BulkChange(Core.Metadata.MetadataFields.Year, BulkAction.Set, "soon")], ct: Ct);
        Assert.NotNull(problem);
        Assert.Null(none);
        Assert.Empty(await FindAsync("tag:friday"));

        var progress = new Reports();
        var (ok, undo) = await _metadata.EditManyAsync(ids,
        [
            new BulkChange(Core.Metadata.MetadataFields.Tags, BulkAction.Add, "Friday game"),
            new BulkChange(Core.Metadata.MetadataFields.Types, BulkAction.Remove, "adventure"),
            new BulkChange(Core.Metadata.MetadataFields.Types, BulkAction.Add, "Heist kit"),
            new BulkChange(Core.Metadata.MetadataFields.System, BulkAction.Set, "Pathfinder"),
        ], progress, Ct);
        Assert.Null(ok);
        Assert.Equal(ids, await FindAsync("tag:friday"));
        Assert.Empty(await FindAsync("type:adventure"));
        Assert.Equal((2, 2), progress.Last);
        var heist = (await _vocabulary.GetAsync(Ct)).Resolve("type", "heist kit");
        Assert.Equal("Heist kit", heist?.Label);
        foreach (var id in ids)
        {
            var metadata = (await _metadataStore.GetAsync(id, Ct)).Compute();
            Assert.Equal([heist!.Key], metadata[Core.Metadata.MetadataFields.Types].Values.Select(v => v.Value));
            Assert.True(metadata[Core.Metadata.MetadataFields.System].First?.Confirmed);
        }

        // The hints from names run again, as after a vocabulary change: the Adventures folder doesn't bring it back.
        await _service.RerunAsync(Stage.RuleHints, Ct);
        await SettleAsync();
        Assert.Empty(await FindAsync("type:adventure"));

        await _metadata.UndoManyAsync(undo!, ct: Ct);
        Assert.Empty(await FindAsync("tag:friday"));
        Assert.Equal(ids, await FindAsync("type:adventure"));
        Assert.All(await search.ListAsync(new LibraryFilter(), ct: Ct), e => Assert.Equal("Adventure", e.Kind));
    }

    /// <summary>Keeps the last progress report, synchronously, where <see cref="Progress{T}"/> would post it.</summary>
    sealed class Reports : IProgress<(int Done, int Total)>
    {
        public (int Done, int Total) Last { get; private set; }

        public void Report((int Done, int Total) value) => Last = value;
    }

    /// <summary>How many jobs there are and how many times they have run.</summary>
    async Task<(long Jobs, long Attempts)> JobsAsync()
    {
        await using var c = _index.OpenRead();
        return await c.QuerySingleAsync<(long, long)>("SELECT count(*), coalesce(sum(attempts), 0) FROM job");
    }

    sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
    }

    /// <summary>Encodes nothing: a "JPEG" is a marker plus the size, and PNG sizes come from the header.</summary>
    sealed class FakeCodec : IImageCodec
    {
        public byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride) => [0xFF, 0xD8, (byte)width, (byte)height];

        public (int Width, int Height)? ReadSize(Stream image)
        {
            var header = new byte[24];
            if (image.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24 || header[1] != (byte)'P') return null;
            return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
        }

        public byte[]? Thumbnail(Stream image, int maxWidth) => ReadSize(image) is null ? null : [0xFF, 0xD8];

        /// <summary>A PNG signature and IHDR: enough for anything that only reads the size.</summary>
        public static byte[] Png(int width, int height)
        {
            var png = new byte[33];
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
            return png;
        }
    }
}
