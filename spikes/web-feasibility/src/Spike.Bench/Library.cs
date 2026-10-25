using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Microsoft.Data.Sqlite;

namespace Spike.Bench;

/// <summary>
/// Bibliotaph.Index, unchanged, over a made-up library: the real schema, single writer and search queries. The same
/// code runs natively (Spike.Native) and in a browser worker (Spike.Web), so the two can be compared.
/// </summary>
public sealed class Library
{
    readonly IndexDatabase _db;
    readonly IndexWriter _writer;
    readonly IndexStore _store;
    readonly LibraryQueries _queries;

    Library(IndexDatabase db, IndexWriter writer)
    {
        _db = db;
        _writer = writer;
        _store = new IndexStore(writer);
        _queries = new LibraryQueries(db);
    }

    public static async Task<(Library Library, string Result)> OpenAsync(string path)
    {
        var db = IndexDatabase.ForFile(path);
        var result = await db.InitializeAsync();
        var writer = new IndexWriter(db);
        await writer.StartAsync(CancellationToken.None);
        return (new Library(db, writer), result.ToString());
    }

    public async Task CloseAsync()
    {
        await _writer.StopAsync(CancellationToken.None);
        _db.ClearPools();
    }

    /// <summary>Adds books <paramref name="first"/> onwards, each a document with an entry and pages of made-up text.</summary>
    public async Task<object> AddBooksAsync(int first, int count, int pagesPerBook)
    {
        var watch = Stopwatch.StartNew();
        long chars = 0;
        for (var id = first; id < first + count; id++)
        {
            var random = new Random(id);
            var pages = Enumerable.Range(0, pagesPerBook).Select(_ => Text.Page(random)).ToList();
            chars += pages.Sum(p => p.Length);
            await _store.UpsertDocumentAsync(
                new DocRow { DocumentId = id, ContentHash = $"hash{id}", Format = "pdf", DisplayTitle = Text.Title(random), PageCount = pagesPerBook, FolderHint = "Spike" },
                [.. pages.Select((_, i) => new PageRow(i, (i + 1).ToString(CultureInfo.InvariantCulture), 612, 792))], [], CancellationToken.None);
            await _store.SetPageTextAsync(id, [.. pages.Select((text, i) => new PageTextRow(i, text, "pdf", 1, false))], CancellationToken.None);
            await _store.SetEntriesAsync([new EntryDocRow(new EntryId(id + 1_000_000), id, EntryKind.Whole)], CancellationToken.None);
        }
        return new { books = count, pages = count * pagesPerBook, chars, ms = watch.ElapsedMilliseconds };
    }

    /// <summary>Both search tabs for one query, timed separately, as the Library runs them.</summary>
    public async Task<object> SearchAsync(string query)
    {
        var plan = SearchPlan.From(SearchQuery.Parse(query));
        var watch = Stopwatch.StartNew();
        var documents = await _queries.SearchDocumentsAsync(plan, new LibraryFilter());
        var documentsMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var pages = await _queries.SearchPagesAsync(plan, new LibraryFilter());
        var pagesMs = watch.Elapsed.TotalMilliseconds;
        return new { query, documents = documents.Count, documentsMs, books = pages.Entries.Count, matchingPages = pages.MatchingPages, pagesMs };
    }

    public static object Stats(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM doc), (SELECT count(*) FROM page), (SELECT page_count * page_size FROM pragma_page_count, pragma_page_size)";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new { docs = reader.GetInt64(0), pages = reader.GetInt64(1), bytes = reader.GetInt64(2) };
    }

    public static readonly string[] Queries =
        ["owlbear", "owlbear tavern", "\"red dragon\"", "necromancer crypt -vampire", "owl*", "damage", "title:dragon", "kitsune yokai oni"];
}

/// <summary>
/// Made-up page text with the word frequencies of real prose (Zipf's law), so FTS5 sees a realistic spread: a few
/// words on every page, most words on few. The RPG words sit at fixed ranks, from common (dragon) to rare (kitsune).
/// </summary>
public static class Text
{
    const int Vocabulary = 40_000;

    static readonly string[] Common =
    [
        "the", "of", "and", "a", "to", "in", "is", "you", "that", "it", "with", "for", "as", "on", "are", "this", "be", "or", "can",
        "your", "an", "at", "by", "from", "each", "if", "its", "they", "their", "one", "turn", "creature", "level", "damage", "roll",
        "check", "spell", "attack", "character", "target", "action", "hit", "points", "saving", "throw", "round", "range", "feet",
    ];

    // Rank in the vocabulary: lower is more common. About 1 in 1,000 pages for rank 3,000 and rarer beyond.
    static readonly (string Word, int Rank)[] Placed =
    [
        ("dragon", 60), ("red", 80), ("tavern", 150), ("goblin", 200), ("dungeon", 120), ("crypt", 400), ("vampire", 600), ("lich", 900),
        ("owlbear", 1_500), ("necromancer", 2_000), ("galleon", 3_000), ("beholder", 4_000), ("kitsune", 12_000), ("yokai", 15_000),
        ("oni", 9_000), ("symbaroum", 20_000), ("davokar", 25_000), ("coriolis", 30_000),
    ];

    static readonly string[] Words = BuildWords();
    static readonly double[] Cumulative = BuildCumulative();

    static string[] BuildWords()
    {
        var words = new string[Vocabulary];
        var syllables = new[] { "ka", "lo", "mir", "dun", "the", "ra", "vel", "or", "en", "sha", "tor", "gal", "bri", "um", "ix", "pe", "dor", "ae" };
        for (var i = 0; i < Vocabulary; i++)
        {
            if (i < Common.Length) { words[i] = Common[i]; continue; }
            var word = new StringBuilder();
            for (var n = i; word.Length < 4 || n > 0; n /= syllables.Length) word.Append(syllables[n % syllables.Length]);
            words[i] = word.ToString();
        }
        foreach (var (word, rank) in Placed) words[rank] = word;
        return words;
    }

    static double[] BuildCumulative()
    {
        var cumulative = new double[Vocabulary];
        double total = 0;
        for (var i = 0; i < Vocabulary; i++) cumulative[i] = total += 1 / Math.Pow(i + 1, 1.07);
        for (var i = 0; i < Vocabulary; i++) cumulative[i] /= total;
        return cumulative;
    }

    static string Word(Random random)
    {
        var index = Array.BinarySearch(Cumulative, random.NextDouble());
        return Words[Math.Min(index < 0 ? ~index : index, Vocabulary - 1)];
    }

    public static string Page(Random random)
    {
        var text = new StringBuilder(2600);
        while (text.Length < 2500) text.Append(Word(random)).Append(random.Next(14) == 0 ? ". " : " ");
        return text.ToString();
    }

    public static string Title(Random random) => $"The {Words[random.Next(100, 5000)]} of the {Words[random.Next(100, 5000)]}";
}
