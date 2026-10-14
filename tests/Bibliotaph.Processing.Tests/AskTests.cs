using System.Collections.Concurrent;
using System.Net;
using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Tests;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Ask this book: pages found by words alone without an embedding model, by words and meaning with one, the
/// book's vectors made on the first ask and made again when the model changes, the weak-signal guard, and the books
/// and setups that can't be asked; answers written from those pages and checked; follow-ups rewritten and answered
/// with the conversation, which is kept on the book's entry. The model server is a stand-in that counts what it is
/// sent.
/// </summary>
[Collection(SqlitePools.Name)]
public sealed class AskTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-ask-").FullName;
    readonly FakeServer _server = new();
    CatalogDatabase _catalog = null!;
    IndexDatabase _index = null!;
    IndexWriter _writer = null!;
    AiSettings _ai = null!;
    AskService _ask = null!;
    long _rootId;
    long _book;
    EntryId _entry;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Endpoint = "http://localhost:11434";

    /// <summary>
    /// A model server with an embedding model: each vector is the text's words, hashed into 64 buckets, so texts that
    /// share words are close. It answers with what the test says, streaming it in pieces when asked to.
    /// </summary>
    sealed class FakeServer : IModelClient, IEmbeddingClient
    {
        public ConcurrentQueue<(string Model, IReadOnlyList<string> Texts)> Embedded { get; } = new();

        public ConcurrentQueue<ModelRequest> Asked { get; } = new();

        public string Answer { get; set; } = "";

        /// <summary>What a follow-up's rewrite comes back as; null to fail it as if the server stopped answering.</summary>
        public string? Rewrite { get; set; }

        public volatile bool Down;

        public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) =>
            Task.FromResult(new EndpointInfo(EndpointKind.Ollama, "0.0-test", ["chat-8b", "embed-a", "embed-b"]));

        public Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (Down) throw new ModelEndpointException("Nothing is answering at http://localhost:11434/. Is the model server running?");
            Asked.Enqueue(request);
            if (request.System == AskPrompt.RewriteSystemMessage)
                return Rewrite is { } rewrite ? Task.FromResult(rewrite) : throw new ModelEndpointException("The model server stopped answering.");
            if (request.Progress is { } progress)
            {
                progress.Report(new ModelProgress(ModelStep.Waiting, ""));
                for (var i = 10; i < Answer.Length; i += 10) progress.Report(new ModelProgress(ModelStep.Writing, Answer[..i]));
                progress.Report(new ModelProgress(ModelStep.Writing, Answer));
            }
            return Task.FromResult(Answer);
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            if (Down) throw new ModelEndpointException("Nothing is answering at http://localhost:11434/. Is the model server running?");
            Embedded.Enqueue((model, texts));
            return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Vector)]);
        }

        static float[] Vector(string text)
        {
            var vector = new float[64];
            foreach (var word in AskRetrieval.Words(text)) vector[word.Aggregate(7, (h, c) => (h * 31 + c) & 0xFFFF) % 64] += 1;
            return vector;
        }
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
        await _catalog.MigrateFromTemplateAsync(Ct);
        var contexts = new Factory(_catalog);
        _index = IndexDatabase.ForFile(paths.IndexDatabase);
        await _index.InitializeAsync(Ct);
        _writer = new IndexWriter(_index);
        await _writer.StartAsync(Ct);
        var store = new IndexStore(_writer);
        _ai = new AiSettings(new SettingsStore(contexts), new NoApiKeys(), _ => _server);
        _ask = new AskService(_ai, new LibraryStore(contexts), new AskQueries(_index), new IndexQueries(_index), store, new EntryStore(contexts),
            new AskHistoryStore(contexts));

        // An invented book in an invented folder: a page waiting for OCR, then three with text under two bookmarks.
        await using (var db = _catalog.CreateContext())
        {
            var root = db.SourceRoots.Add(new SourceRoot { Path = Path.Combine(_dir, "Invented Library"), AddedUtc = DateTime.UtcNow }).Entity;
            var document = db.Documents.Add(new Document { ContentHash = new string('a', 64), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity;
            await db.SaveChangesAsync(Ct);
            db.FileLocations.Add(new FileLocation { SourceRootId = root.Id, RelativePath = "Mill Road.pdf", DocumentId = document.Id, ContentHash = document.ContentHash });
            var entry = db.Entries.Add(new Entry { Kind = EntryKind.Whole, CreatedUtc = DateTime.UtcNow }).Entity;
            await db.SaveChangesAsync(Ct);
            db.EntrySources.Add(new EntrySource { EntryId = entry.Id, DocumentId = document.Id, IsCurrent = true });
            await db.SaveChangesAsync(Ct);
            (_rootId, _book, _entry) = (root.Id, document.Id, new EntryId(entry.Id));
        }
        string[] texts =
        [
            "",
            "Chapter One. The red dragon sleeps beneath the old mill by the river.",
            "The innkeeper pours ale and tells tales of the harbour.",
            "A secret door hides behind the bar of the inn.",
        ];
        await store.UpsertDocumentAsync(new DocRow { DocumentId = _book, ContentHash = new string('a', 64), Format = "pdf", DisplayTitle = "Mill Road", PageCount = 4 },
            [new PageRow(0, "i", 612, 792), new PageRow(1, "1", 612, 792), new PageRow(2, "2", 612, 792), new PageRow(3, "3", 612, 792)],
            [new OutlineRow("Chapter One", 1, 0), new OutlineRow("The Inn", 2, 0)], Ct);
        await store.SetPageTextAsync(_book, [.. texts.Select((t, i) => new PageTextRow(i, t, "pdf", 1, t.Length == 0))], Ct);
    }

    public async ValueTask DisposeAsync()
    {
        // Asking checks for missing vectors in the background; on Windows its open index.db would stop the delete.
        if (_ask.FillOf(_book) is { Running: true }) await FilledAsync();
        _ask.Dispose();
        _writer.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    Task SetUpAsync(string? embeddingModel, bool enabled = true, IReadOnlySet<long>? skipped = null) =>
        _ai.SaveAsync(new AiSetup(Endpoint, "chat-8b", "ollama", enabled, false, skipped ?? new HashSet<long>(), EmbeddingModel: embeddingModel), Ct);

    /// <summary>Waits for the book's vectors to stop being made, then says how far they got.</summary>
    async Task<AskFill> FilledAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (_ask.FillOf(_book) is { Running: false } fill) return fill;
            await Task.Delay(20, timeout.Token);
        }
    }

    long VectorsBy(string model)
    {
        using var connection = _index.OpenRead();
        return connection.ExecuteScalar<long>("SELECT count(*) FROM page_vec WHERE model = @model", new { model });
    }

    [Fact]
    public async Task Without_an_embedding_model_pages_are_found_by_their_words_and_nothing_is_sent()
    {
        await SetUpAsync(null);

        var found = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);

        Assert.False(_ask.HasEmbeddingModel);
        Assert.False(found.Weak);
        var passage = found.Passages[0];
        Assert.Equal((1, "1", "Chapter One", "dragon"), (passage.PdfPage, passage.Page, passage.Heading, passage.Phrase));
        Assert.True(passage.ByWords);
        Assert.False(passage.ByMeaning);
        Assert.Equal(1, found.Evidence[0].PdfPage);
        Assert.Equal(AskMeaning.Off, found.Meaning);
        Assert.Equal(new PageCoverage(4, 3, 1, 0), found.Coverage);
        Assert.Empty(_server.Embedded);
        Assert.Empty(_server.Asked);
        Assert.Null(_ask.FillOf(_book));
    }

    [Fact]
    public async Task The_first_ask_makes_the_books_vectors_and_then_pages_are_found_by_meaning_too()
    {
        await SetUpAsync("embed-a");

        var first = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        var fill = await FilledAsync();
        Assert.NotEqual(AskMeaning.Off, first.Meaning);
        Assert.Equal(new AskFill(3, 3, false, null), fill);
        Assert.Equal(3, VectorsBy("embed-a"));
        // Pages are sent with the bookmark they are under; the page waiting for OCR isn't sent.
        var documents = _server.Embedded.Single(e => e.Texts.Count == 3).Texts;
        Assert.Contains("Chapter One. Chapter One. The red dragon", documents[0], StringComparison.Ordinal);
        Assert.StartsWith("The Inn. ", documents[1], StringComparison.Ordinal);

        var second = await _ask.FindAsync(_book, "Where is the secret door of the inn?", Ct);
        Assert.Equal(AskMeaning.On, second.Meaning);
        Assert.Equal(new PageCoverage(4, 3, 1, 3), second.Coverage);
        var best = second.Passages[0];
        Assert.Equal(3, best.PdfPage);
        Assert.True(best.ByWords && best.ByMeaning);
        Assert.Null(second.MeaningProblem);
    }

    [Fact]
    public async Task Opening_the_book_makes_its_vectors_before_it_is_asked_unless_ask_cant_use_them()
    {
        await SetUpAsync(null);
        await _ask.PrepareAsync(_book, Ct);
        Assert.Null(_ask.FillOf(_book));

        await SetUpAsync("embed-a", skipped: new HashSet<long> { _rootId });
        await _ask.PrepareAsync(_book, Ct);
        Assert.Null(_ask.FillOf(_book));

        await SetUpAsync("embed-a", enabled: false);
        await _ask.PrepareAsync(_book, Ct);
        Assert.Null(_ask.FillOf(_book));
        Assert.Empty(_server.Embedded);

        await SetUpAsync("embed-a");
        await _ask.PrepareAsync(_book, Ct);
        Assert.Equal(new AskFill(3, 3, false, null), await FilledAsync());
        Assert.Equal(3, VectorsBy("embed-a"));

        // The first question is found by meaning on every page.
        var first = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        Assert.Equal(AskMeaning.On, first.Meaning);
        Assert.True(first.Passages[0].ByMeaning);
    }

    [Fact]
    public async Task Choosing_another_embedding_model_drops_the_old_vectors_and_makes_new_ones()
    {
        await SetUpAsync("embed-a");
        await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        await FilledAsync();
        Assert.Equal(3, VectorsBy("embed-a"));

        await SetUpAsync("embed-b");
        await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        await FilledAsync();

        Assert.Equal(0, VectorsBy("embed-a"));
        Assert.Equal(3, VectorsBy("embed-b"));
        Assert.Contains(_server.Embedded, e => e.Model == "embed-b" && e.Texts.Count == 3);
    }

    [Fact]
    public async Task A_question_neither_signal_finds_is_weak_and_says_which_pages_came_closest()
    {
        await SetUpAsync("embed-a");
        await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        await FilledAsync();

        var nothing = await _ask.FindAsync(_book, "How fast is the starship's warp engine?", Ct);
        Assert.True(nothing.Weak);
        Assert.Empty(nothing.Evidence);
        Assert.Empty(nothing.Passages);

        // One word of three is on a page: too little to send pages to a model, but worth naming the pages looked at.
        await SetUpAsync(null);
        var thin = await _ask.FindAsync(_book, "Which starship crashed into the harbour engine?", Ct);
        Assert.True(thin.Weak);
        Assert.Equal(["2"], thin.LookedAt);
    }

    [Fact]
    public async Task A_server_that_stops_answering_leaves_words_finding_pages_and_says_why()
    {
        await SetUpAsync("embed-a");
        await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        await FilledAsync();

        _server.Down = true;
        var found = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        var fill = await FilledAsync();

        Assert.Equal(1, found.Passages[0].PdfPage);
        Assert.Contains("Nothing is answering", found.MeaningProblem, StringComparison.Ordinal);
        Assert.Null(fill.Problem);

        // A book whose vectors can't be made says so, and asking again carries on.
        await SetUpAsync("embed-b");
        await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        Assert.Contains("Nothing is answering", (await FilledAsync()).Problem, StringComparison.Ordinal);
        _server.Down = false;
        await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        Assert.Equal(new AskFill(3, 3, false, null), await FilledAsync());
    }

    [Fact]
    public async Task A_book_in_a_folder_kept_from_ai_and_any_book_with_ai_off_cant_be_asked_and_nothing_is_sent()
    {
        await SetUpAsync("embed-a", skipped: new HashSet<long> { _rootId });
        Assert.True(_ask.IsOn);
        var kept = await Assert.ThrowsAsync<AskUnavailableException>(() => _ask.FindAsync(_book, "Where does the dragon sleep?", Ct));
        Assert.Contains("kept from AI", kept.Message, StringComparison.Ordinal);

        await SetUpAsync("embed-a", enabled: false);
        Assert.False(_ask.IsOn);
        Assert.False(_ask.HasEmbeddingModel);
        await Assert.ThrowsAsync<AskUnavailableException>(() => _ask.FindAsync(_book, "Where does the dragon sleep?", Ct));

        Assert.Empty(_server.Embedded);
        Assert.Null(_ask.FillOf(_book));
    }

    [Fact]
    public async Task Work_that_took_the_setup_keeps_its_model_and_clients_together_when_the_setup_changes()
    {
        await SetUpAsync("embed-a");
        var before = _ai.Current;

        await _ai.SaveAsync(new AiSetup(Endpoint, "chat-14b", "ollama", true, false, new HashSet<long>(), EmbeddingModel: "embed-b"), Ct);
        var after = _ai.Current;

        Assert.Equal(("chat-8b", "chat-8b", "chat-8b", "embed-a"),
            (before.Setup.ModelName, before.Classifier?.Model, before.Interpreter?.Model, before.Setup.EmbeddingName));
        Assert.Equal(("chat-14b", "chat-14b", "chat-14b", "embed-b"),
            (after.Setup.ModelName, after.Classifier?.Model, after.Interpreter?.Model, after.Setup.EmbeddingName));
        Assert.NotNull(after.Chat);
        Assert.NotNull(after.Embedder);

        await _ai.SaveAsync(new AiSetup(Endpoint, "chat-14b", "ollama", false, false, new HashSet<long>()), Ct);
        Assert.Equal(AiState.Off with { Setup = _ai.Setup }, _ai.Current);
    }

    [Fact]
    public async Task A_remote_server_without_its_tick_is_not_sent_pages()
    {
        await _ai.SaveAsync(new AiSetup("https://models.example.com", "chat-8b", "openai", true, false, new HashSet<long>(), EmbeddingModel: "embed-a"), Ct);

        Assert.False(_ask.IsOn);
        Assert.Null(_ai.Embedder);
        await Assert.ThrowsAsync<AskUnavailableException>(() => _ask.FindAsync(_book, "Where does the dragon sleep?", Ct));
        Assert.Empty(_server.Embedded);
    }

    /// <summary>An engine process that "runs" until it is disposed.</summary>
    sealed class StandInEngine : IEngineProcess
    {
        public bool HasExited { get; private set; }

        public string RecentOutput => "";

        public void Dispose() => HasExited = true;
    }

    /// <summary>llama-server's /health, ready at once.</summary>
    sealed class Healthy : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Fact]
    public async Task The_built_in_embedding_engine_keeps_running_while_ask_can_use_it_and_stops_when_it_cant()
    {
        var server = Path.Combine(_dir, "llama-server.exe");
        var model = Path.Combine(_dir, "embed.gguf");
        await File.WriteAllTextAsync(server, "", Ct);
        await File.WriteAllTextAsync(model, "", Ct);
        var setup = AiSetup.Off with { Enabled = true, BuiltIn = true, BuiltInModel = "qwen3-4b", BuiltInEmbeddingModel = BuiltInModels.Embedding.Id };
        await _ai.SaveAsync(setup, Ct);
#pragma warning disable CA2000 // The engine's client owns the handler.
        var engine = new BuiltInEngine(new BuiltInEngineOptions { ServerPath = server, Embedding = true, MinContextTokens = 2_048 },
            new HttpClient(new Healthy()), _ => new StandInEngine());
#pragma warning restore CA2000
        await using (engine)
        {
            engine.StopWhenNotNeededForAsk(_ai);
            await engine.UseAsync(model, 2_048, (_, _) => Task.FromResult(0), Ct);
            Assert.True(engine.IsRunning);

            // Another chat model, or anything else about AI that leaves the embedding model, keeps it.
            await _ai.SaveAsync(setup with { BuiltInModel = "qwen3-8b" }, Ct);
            Assert.True(engine.IsRunning);

            await _ai.SaveAsync(setup with { BuiltInEmbeddingModel = null }, Ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (engine.IsRunning) await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Keeps every report in order, on the reporting thread.</summary>
    sealed class Reports : IProgress<ModelProgress>
    {
        public List<ModelProgress> All { get; } = [];

        public void Report(ModelProgress value) => All.Add(value);
    }

    [Fact]
    public async Task An_answer_is_written_from_the_chosen_pages_only_streamed_and_checked()
    {
        await SetUpAsync(null);
        _server.Answer = "It sleeps beneath the old mill [p. 1]. The book says \"the red dragon sleeps beneath the old mill\" [p. 1]. Its lair has nine exits [p. 7].";
        var found = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        var reports = new Reports();

        var answer = await _ask.AnswerAsync(_book, found, reports, ct: Ct);

        var request = Assert.Single(_server.Asked);
        Assert.Equal(("chat-8b", (string?)null, AskPrompt.ContextTokens, AskPrompt.SystemMessage), (request.Model, request.SchemaJson, request.ContextTokens, request.System));
        Assert.Contains("[Page 1]", request.User, StringComparison.Ordinal);
        Assert.Contains("Heading: Chapter One", request.User, StringComparison.Ordinal);
        Assert.Contains("The red dragon sleeps beneath the old mill", request.User, StringComparison.Ordinal);
        // Only the pages retrieval chose are sent.
        Assert.DoesNotContain("innkeeper", request.User, StringComparison.Ordinal);
        Assert.DoesNotContain("dragon", request.System, StringComparison.Ordinal);

        Assert.True(reports.All.Count > 3);
        Assert.Equal(_server.Answer, reports.All[^1].Answer);
        Assert.Equal((2, 1, 1, 0), (answer.Checked.Citations, answer.Checked.RemovedCitations, answer.Checked.Quotes, answer.Checked.Paraphrases));
        Assert.Equal([1], answer.Checked.CitedPages);
        Assert.Contains(answer.Checked.Parts, p => p.Kind == AnswerPartKind.Unsupported);
        Assert.Equal(("chat-8b", "Where does the dragon sleep?"), (answer.Model, answer.Question));
    }

    [Fact]
    public async Task Pages_that_dont_answer_are_said_so_and_a_question_nothing_matches_never_reaches_the_model()
    {
        await SetUpAsync(null);
        _server.Answer = AskPrompt.NotAnswered;
        var found = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        Assert.True((await _ask.AnswerAsync(_book, found, ct: Ct)).Checked.NotAnswered);

        var weak = await _ask.FindAsync(_book, "How fast is the starship's warp engine?", Ct);
        Assert.True(weak.Weak);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _ask.AnswerAsync(_book, weak, ct: Ct));
        Assert.Single(_server.Asked);
    }

    [Fact]
    public async Task A_book_kept_from_ai_since_its_pages_were_found_is_not_sent_and_a_server_that_fails_says_why()
    {
        await SetUpAsync(null);
        var found = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);

        _server.Down = true;
        var down = await Assert.ThrowsAsync<ModelEndpointException>(() => _ask.AnswerAsync(_book, found, ct: Ct));
        Assert.Contains("Nothing is answering", down.Message, StringComparison.Ordinal);
        _server.Down = false;

        await SetUpAsync(null, skipped: new HashSet<long> { _rootId });
        await Assert.ThrowsAsync<AskUnavailableException>(() => _ask.AnswerAsync(_book, found, ct: Ct));
        await SetUpAsync(null, enabled: false);
        Assert.False(_ask.CanAnswer);
        await Assert.ThrowsAsync<AskUnavailableException>(() => _ask.AnswerAsync(_book, found, ct: Ct));
        Assert.Empty(_server.Asked);
    }

    [Fact]
    public async Task A_follow_up_is_rewritten_to_stand_on_its_own_found_by_that_and_answered_with_the_earlier_turns()
    {
        await SetUpAsync(null);
        _server.Answer = "It sleeps beneath the old mill [p. 1].";
        var found = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        var first = await _ask.AnswerAsync(_book, found, ct: Ct);
        var kept = await _ask.SaveTurnAsync(_book, "Where does the dragon sleep?", null, first, Ct);
        Assert.NotNull(kept);

        _server.Rewrite = "Follow-up: Where is the secret door of the inn?";
        var (question, rewritten) = await _ask.RewriteAsync(_book, "And the door?", [kept.Earlier], Ct);

        Assert.Equal(("Where is the secret door of the inn?", true), (question, rewritten));
        var rewrite = _server.Asked.Last();
        Assert.Equal(((string?)null, AskPrompt.RewriteContextTokens, (int?)AskPrompt.RewriteTokens), (rewrite.SchemaJson, rewrite.ContextTokens, rewrite.MaxTokens));
        Assert.Contains("Q: Where does the dragon sleep?", rewrite.User, StringComparison.Ordinal);
        // The earlier answer goes without its citations, and no page of the book goes with a rewrite.
        Assert.Contains("A: It sleeps beneath the old mill.", rewrite.User, StringComparison.Ordinal);
        Assert.DoesNotContain(ClassifierPrompt.ExcerptTag, rewrite.User, StringComparison.Ordinal);
        Assert.EndsWith("Follow-up: And the door?", rewrite.User.TrimEnd(), StringComparison.Ordinal);

        var next = await _ask.FindAsync(_book, question, Ct);
        Assert.Equal(3, next.Evidence[0].PdfPage);
        _server.Answer = "Behind the bar [p. 3].";
        await _ask.AnswerAsync(_book, next, earlier: [kept.Earlier], ct: Ct);
        var answer = _server.Asked.Last();
        Assert.Contains("Earlier in this conversation:", answer.User, StringComparison.Ordinal);
        Assert.Contains("Question: Where is the secret door of the inn?", answer.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_question_goes_as_typed_and_a_rewrite_that_fails_or_says_nothing_leaves_the_follow_up_as_it_was()
    {
        await SetUpAsync(null);
        var earlier = new AskEarlierTurn("Where does the dragon sleep?", "Beneath the old mill [p. 1].");

        Assert.Equal(("And the door?", false), await _ask.RewriteAsync(_book, "And the door?", [], Ct));
        Assert.Empty(_server.Asked);

        _server.Rewrite = null;
        Assert.Equal(("And the door?", false), await _ask.RewriteAsync(_book, "And the door?", [earlier], Ct));
        _server.Rewrite = "\n";
        Assert.Equal(("And the door?", false), await _ask.RewriteAsync(_book, "And the door?", [earlier], Ct));
        _server.Rewrite = "And the door?";
        Assert.Equal(("And the door?", false), await _ask.RewriteAsync(_book, "And the door?", [earlier], Ct));

        await SetUpAsync(null, skipped: new HashSet<long> { _rootId });
        await Assert.ThrowsAsync<AskUnavailableException>(() => _ask.RewriteAsync(_book, "And the door?", [earlier], Ct));
        Assert.Equal(3, _server.Asked.Count);
    }

    [Fact]
    public async Task The_conversation_is_kept_on_the_books_entry_with_its_checked_answers_and_starts_again_when_asked()
    {
        await SetUpAsync(null);
        _server.Answer = "The book says \"the red dragon sleeps beneath the old mill\" [p. 1]. Its lair has nine exits [p. 7].";
        var found = await _ask.FindAsync(_book, "Where does the dragon sleep?", Ct);
        await _ask.SaveTurnAsync(_book, "Where does the dragon sleep?", null, await _ask.AnswerAsync(_book, found, ct: Ct), Ct);
        _server.Answer = AskPrompt.NotAnswered;
        await _ask.SaveTurnAsync(_book, "And its hoard?", "Where is the red dragon's hoard?", await _ask.AnswerAsync(_book, found, ct: Ct), Ct);

        var history = await _ask.HistoryAsync(_book, Ct);
        Assert.Equal(["Where does the dragon sleep?", "And its hoard?"], history.Select(t => t.Info.Question));
        // The checked pieces come back as they were shown: a quote found on its page, and a claim with no page it read.
        Assert.Contains(history[0].Parts, p => p.Kind == AnswerPartKind.Quote);
        Assert.Contains(history[0].Parts, p => p is { Kind: AnswerPartKind.Citation, PdfPage: 1 });
        Assert.Contains(history[0].Parts, p => p.Kind == AnswerPartKind.Unsupported);
        Assert.Equal([1], history[0].Info.CitedPages);
        Assert.Equal("chat-8b", history[0].Info.Model);
        Assert.True(history[1].Info.NotAnswered);
        Assert.Equal(new AskEarlierTurn("Where is the red dragon's hoard?", AskPrompt.NotAnswered), history[1].Earlier);
        Assert.Equal(AskPrompt.NotAnswered, Assert.Single(history[1].Parts).Text);
        Assert.Equal(2, (await new AskHistoryStore(new Factory(_catalog)).GetAsync(_entry, Ct)).Count);

        Assert.Equal(2, await _ask.ClearHistoryAsync(_book, Ct));
        Assert.Empty(await _ask.HistoryAsync(_book, Ct));
        // A book the catalog has no card for has no conversation to keep.
        Assert.Empty(await _ask.HistoryAsync(9999, Ct));
        Assert.Null(await _ask.SaveTurnAsync(9999, "Anyone?", null, await _ask.AnswerAsync(_book, found, ct: Ct), Ct));
    }
}
