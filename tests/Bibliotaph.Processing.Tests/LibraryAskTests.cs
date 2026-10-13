using System.Collections.Concurrent;
using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Asking the whole library: pages found across books by their words and, once the library is read for meaning, by
/// their meaning; a few pages a book at most; the Library's filters narrowing which books are read; a weak question
/// answered with no model asked; nothing from a folder kept from AI read or sent; and the answer's citations naming
/// their book and page. The background reading of the library for meaning, giving way while the AI is in use. The
/// model server is a stand-in that counts what it is sent.
/// </summary>
[Collection(SqlitePools.Name)]
public sealed class LibraryAskTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-library-ask-").FullName;
    readonly FakeServer _server = new();
    readonly SteppedClock _clock = new();
    CatalogDatabase _catalog = null!;
    IndexDatabase _index = null!;
    IndexWriter _writer = null!;
    AiSettings _ai = null!;
    AskService _bookAsk = null!;
    LibraryAskService _ask = null!;
    LibraryVectors _vectors = null!;
    AiUse _use = null!;
    long _keptRoot;
    long _lich;
    long _city;
    long _secret;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Endpoint = "http://localhost:11434";

    /// <summary>
    /// A model server with an embedding model: each vector is the text's words, hashed into 64 buckets, so texts that
    /// share words are close. It answers with what the test says.
    /// </summary>
    sealed class FakeServer : IModelClient, IEmbeddingClient
    {
        public ConcurrentQueue<IReadOnlyList<string>> Embedded { get; } = new();

        public ConcurrentQueue<ModelRequest> Asked { get; } = new();

        public string Answer { get; set; } = "";

        public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) =>
            Task.FromResult(new EndpointInfo(EndpointKind.Ollama, "0.0-test", ["chat-8b", "embed-a"]));

        public Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Asked.Enqueue(request);
            request.Progress?.Report(new ModelProgress(ModelStep.Writing, Answer));
            return Task.FromResult(Answer);
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            Embedded.Enqueue(texts);
            return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Vector)]);
        }

        static float[] Vector(string text)
        {
            var vector = new float[64];
            foreach (var word in AskRetrieval.Words(text)) vector[word.Aggregate(7, (h, c) => (h * 31 + c) & 0xFFFF) % 64] += 1;
            return vector;
        }
    }

    /// <summary>A clock that moves only when told to, for how long the AI has been left alone.</summary>
    sealed class SteppedClock : TimeProvider
    {
        long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }

    sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
    }

    public async ValueTask InitializeAsync()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        foreach (var directory in paths.Directories) Directory.CreateDirectory(directory);
        _catalog = new CatalogDatabase(paths.CatalogDatabase, paths.Backups);
        await _catalog.MigrateAsync(Ct);
        var contexts = new Factory(_catalog);
        _index = IndexDatabase.ForFile(paths.IndexDatabase);
        await _index.InitializeAsync(Ct);
        _writer = new IndexWriter(_index);
        await _writer.StartAsync(Ct);
        var store = new IndexStore(_writer);
        var library = new LibraryStore(contexts);
        _use = new AiUse(_clock);
        _ai = new AiSettings(new SettingsStore(contexts), new NoApiKeys(), _ => _server);
        _bookAsk = new AskService(_ai, library, new AskQueries(_index), new IndexQueries(_index), store, new EntryStore(contexts),
            new AskHistoryStore(contexts), use: _use);
        _ask = new LibraryAskService(_ai, new LibraryAskQueries(_index), new LibraryQueries(_index), new IndexQueries(_index), library, use: _use);
        _vectors = new LibraryVectors(_ai, _bookAsk, new LibraryAskQueries(_index), library, _use, _clock);

        // Three invented books: one with the lich on every page, one with a lich among other things, and one in a folder kept from AI.
        long[] ids;
        await using (var db = _catalog.CreateContext())
        {
            var shared = db.SourceRoots.Add(new SourceRoot { Path = Path.Combine(_dir, "Invented Library"), AddedUtc = DateTime.UtcNow }).Entity;
            var kept = db.SourceRoots.Add(new SourceRoot { Path = Path.Combine(_dir, "Kept Library"), AddedUtc = DateTime.UtcNow }).Entity;
            var documents = Enumerable.Range(0, 3)
                .Select(i => db.Documents.Add(new Document { ContentHash = new string((char)('a' + i), 64), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity)
                .ToList();
            await db.SaveChangesAsync(Ct);
            for (var i = 0; i < 3; i++)
                db.FileLocations.Add(new FileLocation
                {
                    SourceRootId = i == 2 ? kept.Id : shared.Id,
                    RelativePath = $"Book {i}.pdf",
                    DocumentId = documents[i].Id,
                    ContentHash = documents[i].ContentHash,
                });
            await db.SaveChangesAsync(Ct);
            _keptRoot = kept.Id;
            ids = [.. documents.Select(d => d.Id)];
        }
        (_lich, _city, _secret) = (ids[0], ids[1], ids[2]);
        await AddBookAsync(store, _lich, "The Invented Lich Book", "dnd5e",
            "The lich hides its phylactery in the crypt beneath the old keep.",
            "In the crypt, the lich's phylactery rests on a black altar.",
            "The lich fears sunlight, and its crypt has no windows.",
            "Crypt guards: skeletons serve the lich in the crypt.",
            "The lich keeps a phylactery spell book in the crypt library.");
        await AddBookAsync(store, _city, "Another Invented Book", "pf2e",
            "A lich rules the drowned crypt of the sunken city; its phylactery is a pearl.",
            "Harbour taverns sell ale to sailors.");
        await AddBookAsync(store, _secret, "A Kept Invented Book", "dnd5e",
            "The lich phylactery password is swordfish, hidden in the crypt.");
    }

    static async Task AddBookAsync(IndexStore store, long id, string title, string system, params string[] texts)
    {
        await store.UpsertDocumentAsync(new DocRow { DocumentId = id, ContentHash = new string((char)('a' + id % 3), 64), Format = "pdf", DisplayTitle = $"file {id}", PageCount = texts.Length },
            [.. texts.Select((_, i) => new PageRow(i, null, 612, 792))], [], Ct);
        await store.SetPageTextAsync(id, [.. texts.Select((t, i) => new PageTextRow(i, t, "pdf", 1, false))], Ct);
        var entry = new EntryId(id + 1000);
        await store.SetEntriesAsync([new EntryDocRow(entry, id, EntryKind.Whole)], Ct);
        await store.SetMetadataAsync([new EntryMetaRow { EntryId = entry, Title = title, Facets = [new FacetRow("system", system, system.ToUpperInvariant(), true)] }], Ct);
    }

    public async ValueTask DisposeAsync()
    {
        // The library's reading waits for each book's vectors, but a fill still running would keep index.db open on
        // Windows and stop the delete.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (new[] { _lich, _city, _secret }.Any(id => _bookAsk.FillOf(id) is { Running: true })) await Task.Delay(20, timeout.Token);
        _vectors.Dispose();
        _bookAsk.Dispose();
        _writer.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    Task SetUpAsync(string? embeddingModel, bool keep = true, bool libraryVectors = true)
    {
        HashSet<long> skipped = keep ? [_keptRoot] : [];
        return _ai.SaveAsync(new AiSetup(Endpoint, "chat-8b", "ollama", true, false, skipped, EmbeddingModel: embeddingModel, LibraryVectors: libraryVectors), Ct);
    }

    long Vectors()
    {
        using var connection = _index.OpenRead();
        return connection.ExecuteScalar<long>("SELECT count(*) FROM page_vec");
    }

    [Fact]
    public async Task Pages_come_from_several_books_a_few_from_each_and_the_answer_cites_each_by_book_and_page()
    {
        await SetUpAsync(null);

        var found = await _ask.FindAsync("Where does the lich keep its phylactery?", [], Ct);

        Assert.False(found.Weak);
        Assert.Equal(AskMeaning.Off, found.Meaning);
        Assert.Equal(new LibraryCoverage(2, 7, 0), found.Coverage);
        Assert.Equal(LibraryAskService.Defaults.MaxPagesPerBook, found.Evidence.Count(e => e.DocumentId == _lich));
        Assert.Contains(found.Evidence, e => e.DocumentId == _city && e.PdfPage == 0);
        Assert.Equal(found.Evidence.Select(e => (e.DocumentId, e.PdfPage)), found.Passages.Select(p => (p.DocumentId, p.PdfPage)));
        Assert.All(found.Passages, p => Assert.True(p.ByWords));
        Assert.Equal("Another Invented Book", found.Evidence.First(e => e.DocumentId == _city).Book);

        var pages = LibraryAskService.PromptPages(found);
        var lich = pages[found.Evidence.ToList().FindIndex(e => e.DocumentId == _lich && e.PdfPage == 1)];
        var city = pages[found.Evidence.ToList().FindIndex(e => e.DocumentId == _city)];
        _server.Answer = $"It rests \"on a black altar\" [p. {lich.Marker}]. Another is a pearl [p. {city.Marker}]. Or somewhere else [p. 2].";

        var answer = await _ask.AnswerAsync(found, null, Ct);

        var request = _server.Asked.Single();
        Assert.Equal(AskPrompt.LibrarySystemMessage, request.System);
        Assert.Contains("Book: The Invented Lich Book", request.User, StringComparison.Ordinal);
        var citations = answer.Checked.Parts.Where(p => p.Kind == AnswerPartKind.Citation).ToList();
        (string, int?, long?, string?)[] expected = [("[The Invented Lich Book, p. 2]", 1, _lich, "on a black altar"), ("[Another Invented Book, p. 1]", 0, _city, null)];
        Assert.Equal(expected, citations.Select(c => (c.Text, c.PdfPage, c.DocumentId, c.Phrase)));
        Assert.Contains(answer.Checked.Parts, p => p.Kind == AnswerPartKind.Unsupported);
        Assert.Contains(answer.Checked.Parts, p => p.Kind == AnswerPartKind.Quote);
        Assert.Equal([(_lich, 1), (_city, 0)], answer.Cited.Select(c => (c.DocumentId, c.PdfPage)));
    }

    [Fact]
    public async Task The_librarys_filters_narrow_the_books_a_question_reads()
    {
        await SetUpAsync(null);

        var found = await _ask.FindAsync("Where does the lich keep its phylactery?", [new BookFilter(new LibraryFilter(Systems: ["pf2e"]), new SearchPlan())], Ct);

        Assert.NotEmpty(found.Evidence);
        Assert.All(found.Evidence, e => Assert.Equal(_city, e.DocumentId));
        Assert.Equal(1, found.Coverage.Books);

        // The search box's plain words don't narrow it; its fields do.
        var plan = SearchPlan.From(Core.Search.SearchQuery.Parse("system:dnd5e harbour"));
        var dnd = await _ask.FindAsync("Where does the lich keep its phylactery?", [new BookFilter(new LibraryFilter(), plan)], Ct);
        Assert.NotEmpty(dnd.Evidence);
        Assert.All(dnd.Evidence, e => Assert.Equal(_lich, e.DocumentId));
    }

    [Fact]
    public async Task A_question_the_books_dont_cover_is_weak_names_the_closest_pages_and_asks_no_model()
    {
        await SetUpAsync(null);

        var found = await _ask.FindAsync("Which starship pilot crashed into the harbour?", [], Ct);

        Assert.True(found.Weak);
        Assert.Empty(found.Evidence);
        Assert.Empty(found.Passages);
        Assert.Equal(["Another Invented Book, p. 2"], found.LookedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _ask.AnswerAsync(found, null, Ct));
        Assert.Empty(_server.Asked);
    }

    [Fact]
    public async Task Nothing_from_a_folder_kept_from_ai_is_read_and_ai_off_asks_nothing()
    {
        await SetUpAsync(null);
        var kept = await _ask.FindAsync("What is the swordfish password?", [], Ct);
        Assert.DoesNotContain(kept.Evidence, e => e.DocumentId == _secret);
        Assert.DoesNotContain(kept.Passages, p => p.DocumentId == _secret);
        Assert.Equal(2, kept.Coverage.Books);

        await SetUpAsync(null, keep: false);
        var open = await _ask.FindAsync("What is the swordfish password?", [], Ct);
        Assert.Equal(_secret, open.Evidence[0].DocumentId);

        await _ai.SaveAsync(_ai.Setup with { Enabled = false }, Ct);
        await Assert.ThrowsAsync<AskUnavailableException>(() => _ask.FindAsync("What is the swordfish password?", [], Ct));
    }

    [Fact]
    public async Task The_library_is_read_for_meaning_book_by_book_giving_way_while_the_ai_is_in_use_and_then_found_by_meaning()
    {
        await SetUpAsync("embed-a");

        // While a question is being answered, and for a little after, it waits.
        using (_use.Begin()) Assert.Equal(LibraryVectorsStep.Waiting, await _vectors.FillNextAsync(Ct));
        Assert.Equal(LibraryVectorsStep.Waiting, await _vectors.FillNextAsync(Ct));
        Assert.Empty(_server.Embedded);
        _clock.Advance(LibraryVectors.Quiet);

        var steps = new List<LibraryVectorsStep>();
        while (steps.Count < 10 && (steps.Count == 0 || steps[^1] != LibraryVectorsStep.Done)) steps.Add(await _vectors.FillNextAsync(Ct));
        Assert.Equal([LibraryVectorsStep.Read, LibraryVectorsStep.Read, LibraryVectorsStep.Done], steps);
        // The book in the folder kept from AI isn't read.
        Assert.Equal(7, Vectors());
        Assert.Equal(new LibraryVectorsStatus(LibraryVectorsState.Done, 7, 7, null), _vectors.Status);

        var found = await _ask.FindAsync("Where does the lich keep its phylactery?", [], Ct);
        Assert.Equal(AskMeaning.On, found.Meaning);
        Assert.Contains(found.Passages, p => p.ByMeaning);

        // Switched off in Settings, it does nothing more.
        await SetUpAsync("embed-a", libraryVectors: false);
        Assert.Equal(LibraryVectorsStep.Waiting, await _vectors.FillNextAsync(Ct));
        Assert.Equal(LibraryVectorsState.Off, _vectors.Status.State);
    }

    [Fact]
    public async Task Books_without_vectors_still_take_part_by_their_words()
    {
        await SetUpAsync("embed-a");
        _clock.Advance(LibraryVectors.Quiet);
        // Only the first book is read for meaning.
        Assert.Equal(LibraryVectorsStep.Read, await _vectors.FillNextAsync(Ct));

        var found = await _ask.FindAsync("Where does the lich keep its phylactery?", [], Ct);

        Assert.Equal(AskMeaning.Partial, found.Meaning);
        Assert.Equal(new LibraryCoverage(2, 7, 5), found.Coverage);
        Assert.Contains(found.Evidence, e => e.DocumentId == _city);
    }
}
