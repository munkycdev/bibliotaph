using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Ask this book, Q3: a book's conversation kept on its entry, oldest first, trimmed to the last turns, and cleared.</summary>
public sealed class AskHistoryStoreTests : IAsyncLifetime
{
    static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-ask-history-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(Monday);
    readonly List<ScannedFile> _files = [];
    CatalogDatabase _database = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    AskHistoryStore _history = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        var contexts = new Factory(_database);
        _library = new LibraryStore(contexts);
        _entries = new EntryStore(contexts);
        _history = new AskHistoryStore(contexts, _clock);
        _root = (await new SourceRootStore(contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    async Task<EntryId> AddBookAsync(string path, char hash)
    {
        _files.Add(new ScannedFile(path, 1000, Monday.UtcDateTime, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        var document = (await _library.AttachHashAsync(file, ContentHash.Parse(new string(hash, 64)), Ct))!.Value.DocumentId;
        return (await _entries.GetEntryAsync(document, Ct))!.EntryId;
    }

    [Fact]
    public async Task A_books_conversation_is_kept_in_order_with_its_answers_and_another_books_is_its_own()
    {
        var abbey = await AddBookAsync("abbey.pdf", 'a');
        var barrow = await AddBookAsync("barrow.pdf", 'b');

        var first = await _history.AddAsync(abbey, "Where is the bell tower?", null, "On the north side [p. 4].", "[]", [4], "chat-8b", false, Ct);
        await _history.AddAsync(abbey, "How tall is it?", "How tall is the bell tower?", "Forty feet [p. 4].", "[]", [4, 5], "chat-8b", false, Ct);
        await _history.AddAsync(barrow, "Who is buried here?", null, "The pages I was given don't answer that.", "[]", [], "chat-8b", true, Ct);

        var turns = await _history.GetAsync(abbey, Ct);
        Assert.Equal(["Where is the bell tower?", "How tall is it?"], turns.Select(t => t.Question));
        Assert.Equal(first, turns[0] with { CitedPages = first.CitedPages });
        Assert.Equal([4, 5], turns[1].CitedPages);
        Assert.Equal(("How tall is the bell tower?", "Forty feet [p. 4].", false), (turns[1].Rewritten, turns[1].Answer, turns[1].NotAnswered));
        Assert.True(turns[0].AskedUtc < turns[1].AskedUtc);
        Assert.True(Assert.Single(await _history.GetAsync(barrow, Ct)).NotAnswered);
    }

    [Fact]
    public async Task Only_the_last_turns_are_kept_and_starting_again_clears_only_that_book()
    {
        var abbey = await AddBookAsync("abbey.pdf", 'a');
        var barrow = await AddBookAsync("barrow.pdf", 'b');
        for (var i = 0; i < AskHistoryStore.MaxTurns + 3; i++)
            await _history.AddAsync(abbey, $"Question {i}", null, $"Answer {i}", "[]", [], "chat-8b", false, Ct);
        await _history.AddAsync(barrow, "Who is buried here?", null, "A knight [p. 2].", "[]", [2], "chat-8b", false, Ct);

        var kept = await _history.GetAsync(abbey, Ct);
        Assert.Equal(AskHistoryStore.MaxTurns, kept.Count);
        Assert.Equal(("Question 3", $"Question {AskHistoryStore.MaxTurns + 2}"), (kept[0].Question, kept[^1].Question));

        Assert.Equal(AskHistoryStore.MaxTurns, await _history.ClearAsync(abbey, Ct));
        Assert.Empty(await _history.GetAsync(abbey, Ct));
        Assert.Single(await _history.GetAsync(barrow, Ct));
    }

    [Fact]
    public async Task A_deleted_entry_takes_its_conversation_with_it()
    {
        var abbey = await AddBookAsync("abbey.pdf", 'a');
        await _history.AddAsync(abbey, "Where is the bell tower?", null, "On the north side [p. 4].", "[]", [4], "chat-8b", false, Ct);

        await using (var db = _database.CreateContext())
        {
            await db.EntrySources.Where(s => s.EntryId == abbey.Value).ExecuteDeleteAsync(Ct);
            await db.Entries.Where(e => e.Id == abbey.Value).ExecuteDeleteAsync(Ct);
        }

        await using (var db = _database.CreateContext())
            Assert.Equal(0, await db.AskTurns.CountAsync(Ct));
    }
}
