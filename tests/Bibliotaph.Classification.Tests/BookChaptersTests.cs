using System.Globalization;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Classification.Tests;

public class BookChaptersTests
{
    static BookPage Page(int pdfPage, string text) => new(pdfPage, text);

    static List<(string Title, int First, int Last)> Shape(IEnumerable<ScannedChapter> chapters) =>
        [.. chapters.Select(c => (c.Title, c.FirstPage, c.LastPage))];

    static void AssertCoversEveryPage(IReadOnlyList<ScannedChapter> chapters, int pageCount)
    {
        Assert.Equal(0, chapters[0].FirstPage);
        Assert.Equal(pageCount - 1, chapters[^1].LastPage);
        Assert.All(chapters.Zip(chapters.Skip(1)), p => Assert.Equal(p.First.LastPage + 1, p.Second.FirstPage));
    }

    [Fact]
    public void Bookmarks_give_the_chapters_with_the_opening_pages_joined_to_the_first()
    {
        BookHeading[] outline =
        [
            new("Part One", 2, 0), new("The Gate", 3, 1), new("Part Two", 20, 0), new("Appendix", 36, 0),
        ];

        var chapters = BookChapters.Split(40, outline, [], PrintedPages.None);

        Assert.Equal([("Part One", 0, 19), ("Part Two", 20, 35), ("Appendix", 36, 39)], Shape(chapters));
        Assert.All(chapters, c => Assert.Equal((ChapterSource.Outline, ChapterReading.Waiting), (c.Source, c.Reading)));
    }

    [Fact]
    public void A_long_chapter_is_read_in_even_parts_and_short_ones_join_the_next()
    {
        BookHeading[] outline =
        [
            new("Welcome", 0, 0), new("Rumours", 4, 0), new("Room 1", 6, 0), new("The Long Descent", 10, 0), new("Afterword", 70, 0),
        ];

        var chapters = BookChapters.Split(72, outline, [], PrintedPages.None);

        Assert.Equal(
            [
                ("Welcome", 0, 3), ("Rumours", 4, 9), ("The Long Descent (1 of 3)", 10, 29), ("The Long Descent (2 of 3)", 30, 50),
                ("The Long Descent (3 of 3)", 51, 71),
            ],
            Shape(chapters));
        AssertCoversEveryPage(chapters, 72);
    }

    [Fact]
    public void Deep_bookmarks_for_every_room_are_not_chapters()
    {
        BookHeading[] outline =
        [
            new("The Mill", 0, 0), .. Enumerable.Range(1, 9).Select(i => new BookHeading($"Room {i}", i, 2)),
            new("The Cellar", 12, 0), new("The Tower", 24, 0),
        ];

        var chapters = BookChapters.Split(30, outline, [], PrintedPages.None);

        Assert.Equal([("The Mill", 0, 11), ("The Cellar", 12, 23), ("The Tower", 24, 29)], Shape(chapters));
    }

    [Fact]
    public void Without_bookmarks_the_printed_contents_page_gives_the_chapters()
    {
        // The book's printed page 1 is its fourth PDF page.
        var labels = new string?[] { "Cover", "i", "ii" }.Concat(Enumerable.Range(1, 57).Select(n => n.ToString(CultureInfo.InvariantCulture))).ToList();
        var printed = PrintedPages.FromLabels(labels, labels.Count);
        var pages = Enumerable.Range(0, 60).Select(i => Page(i, $"Page text {i}")).ToList();
        pages[1] = Page(1, "Contents\nIntroduction .......... 1\nThe Salt Caves .......... 9\nThe Lighthouse .......... 30\nCredits .......... 55\n");

        var chapters = BookChapters.Split(60, [], pages, printed);

        // The three credits pages are too few to read alone, so they join the chapter before.
        Assert.Equal([("Introduction", 0, 10), ("The Salt Caves", 11, 31), ("The Lighthouse", 32, 59)], Shape(chapters));
        Assert.All(chapters, c => Assert.Equal(ChapterSource.Contents, c.Source));
    }

    [Fact]
    public void An_index_near_the_front_is_not_taken_for_a_contents_page()
    {
        var labels = Enumerable.Range(1, 60).Select(n => (string?)n.ToString(CultureInfo.InvariantCulture)).ToList();
        var printed = PrintedPages.FromLabels(labels, labels.Count);
        var pages = Enumerable.Range(0, 60).Select(i => Page(i, $"Page text {i}")).ToList();
        pages[2] = Page(2, "Index\nAmber golems, 41\nBandit camps, 12\nCart roads, 55\nDire badgers, 8\nEmber drakes, 30\n");

        var chapters = BookChapters.Split(60, [], pages, printed);

        Assert.All(chapters, c => Assert.Equal(ChapterSource.Pages, c.Source));
    }

    [Fact]
    public void A_book_with_no_chapters_is_read_in_runs_of_about_25_pages()
    {
        var chapters = BookChapters.Split(60, [], [], PrintedPages.None);

        Assert.Equal([("Pages 1–20", 0, 19), ("Pages 21–40", 20, 39), ("Pages 41–60", 40, 59)], Shape(chapters));
        Assert.Single(BookChapters.Split(12, [], [], PrintedPages.None));
        Assert.Empty(BookChapters.Split(0, [], [], PrintedPages.None));
    }

    static ScannedChapter Chapter(int first, int last, ChapterReading reading = ChapterReading.Waiting) =>
        new($"Pages {first}", first, last, ChapterSource.Pages, reading, []);

    [Fact]
    public void Batches_hold_chapters_up_to_30_pages_and_skip_those_already_read()
    {
        ScannedChapter[] chapters =
        [
            Chapter(0, 9), Chapter(10, 24), Chapter(25, 34), Chapter(35, 39, ChapterReading.Read), Chapter(40, 44), Chapter(45, 80),
        ];

        var batches = BookChapters.Batches(chapters, _ => 100);

        Assert.Equal([[0, 1], [2], [4], [5]], batches.Select(b => b.ToArray()));
    }

    [Fact]
    public void Batches_also_close_when_the_text_would_not_fit()
    {
        ScannedChapter[] chapters = [Chapter(0, 3), Chapter(4, 7), Chapter(8, 11)];

        var batches = BookChapters.Batches(chapters, _ => 1_000, maxCharacters: 8_000);

        Assert.Equal([[0, 1], [2]], batches.Select(b => b.ToArray()));
    }

    [Fact]
    public void Fit_cuts_every_page_to_an_even_share_and_leaves_short_pages_whole()
    {
        BookPage[] pages = [Page(0, "Short."), Page(1, new string('a', 5_000)), Page(2, "   "), Page(3, new string('b', 5_000))];

        var fitted = BookChapters.Fit(pages, 4_000);

        Assert.Equal([0, 1, 3], fitted.Select(p => p.PdfPage));
        Assert.Equal("Short.", fitted[0].Text);
        Assert.True(fitted.Sum(p => p.Text.Length) <= 4_000 + 2, $"{fitted.Sum(p => p.Text.Length)} characters.");
        Assert.True(fitted[1].Text.Length > 1_500);
        Assert.Same(pages[0].Text, BookChapters.Fit([pages[0]], 4_000)[0].Text);
    }
}

public class ChapterPromptTests
{
    static readonly Vocabulary Vocabulary = new(
        [
            new Term("setting", "glimmerdeep", "Glimmerdeep"),
            new Term("environment", "swamp", "Swamp"),
            new Term("environment", "underground", "Underground"),
            new Term("theme", "mystery", "Mystery"),
        ],
        [("environment", "swamp", "marsh"), ("environment", "swamp", "bog"), ("environment", "underground", "caves")]);

    static readonly ScannedChapter[] Chapters =
    [
        new("The Reed Road", 0, 4, ChapterSource.Outline, ChapterReading.Waiting, []),
        new("Under the Hill", 5, 9, ChapterSource.Outline, ChapterReading.Waiting, []),
    ];

    static readonly Dictionary<int, string> Pages = new()
    {
        [1] = "The road to Glimmerdeep runs through the marsh, where reed hags hunt at dusk.",
        [6] = "The caves under the hill are home to three cave trolls and a nest of stirges.",
    };

    static ChapterBatchResult Check(string answer) =>
        ChapterCheck.Check(ChapterPrompt.ParseAnswer(answer), Chapters, p => Pages.GetValueOrDefault(p), Vocabulary);

    [Fact]
    public void The_message_numbers_chapters_and_pages_as_the_answer_must()
    {
        var batch = new ChapterBatch("The Reed Road and Other Tales", Chapters, [new BookPage(1, Pages[1]), new BookPage(6, Pages[6])]);

        var user = ChapterPrompt.UserMessage(batch);

        Assert.Contains("Chapter 1: The Reed Road (pages 1 to 5)", user, StringComparison.Ordinal);
        Assert.Contains("Chapter 2: Under the Hill (pages 6 to 10)", user, StringComparison.Ordinal);
        Assert.True(user.IndexOf("[Page 2]", StringComparison.Ordinal) < user.IndexOf("Chapter 2:", StringComparison.Ordinal));
        Assert.Contains("[Page 7]", user, StringComparison.Ordinal);
        Assert.Contains("Swamp", ChapterPrompt.SystemMessage(Vocabulary), StringComparison.Ordinal);
    }

    [Fact]
    public void Values_stand_only_with_a_quote_from_their_own_chapters_page()
    {
        var result = Check("""
            {"chapters": [
              {"chapter": 1,
               "setting": [{"value": "Glimmerdeep", "page": 2, "quote": "The road to Glimmerdeep runs through the marsh"}],
               "environment": [{"value": "bog", "page": 2, "quote": "runs through the marsh"}],
               "creature": [
                 {"value": "Reed hag", "page": 2, "quote": "where reed hags hunt at dusk"},
                 {"value": "cave troll", "page": 7, "quote": "home to three cave trolls"},
                 {"value": "dragon", "page": 2, "quote": "a dragon sleeps in the reeds"}
               ]},
              {"chapter": 2,
               "environment": [{"value": "Volcano", "page": 7, "quote": "The caves under the hill"}, {"value": "caves", "page": 7, "quote": "The caves under the hill"}],
               "theme": [{"value": "unknown", "page": 7, "quote": "The caves under the hill"}],
               "creature": [{"value": "stirge", "page": 7, "quote": "a nest of stirges"}, {"value": "Stirge", "page": 7, "quote": "a nest of stirges"}]}
            ]}
            """);

        Assert.Equal(
            ["creature:reed hag:Reed hag", "environment:swamp:Swamp", "setting:glimmerdeep:Glimmerdeep"],
            result.Tags[0].Select(t => $"{t.Field}:{t.Value}:{t.Label}").Order(StringComparer.Ordinal));
        Assert.Equal(1, result.Tags[0].Single(t => t.Field == ChapterFields.Creature).PdfPage);
        Assert.Equal(
            ["creature:stirge", "environment:underground"],
            result.Tags[1].Select(t => $"{t.Field}:{t.Value}").Order(StringComparer.Ordinal));
        // The troll from the other chapter's page, the made-up dragon, the volcano not in the vocabulary, "unknown" and the repeated stirge.
        Assert.Equal(5, result.Dropped);
    }

    [Fact]
    public void A_setting_must_be_named_in_its_quote()
    {
        var result = Check("""
            {"chapters": [{"chapter": 1, "setting": [{"value": "Glimmerdeep", "page": 2, "quote": "reed hags hunt at dusk"}]}]}
            """);

        Assert.Empty(result.Tags[0]);
    }

    [Fact]
    public void An_answer_that_isnt_json_is_refused_and_one_without_chapters_finds_nothing()
    {
        Assert.Throws<ClassifierAnswerException>(() => ChapterPrompt.ParseAnswer("I couldn't read it."));
        Assert.Empty(ChapterPrompt.ParseAnswer("""{"books": []}"""));
        Assert.Equal(
            [(2, "creature", "ogre", (int?)3)],
            ChapterPrompt.ParseAnswer(
                """
                ```json
                {"chapters": [{"chapter": "2", "creature": {"value": "ogre", "page": 3, "quote": "an ogre"}}]}
                ```
                """).Select(c => (c.Chapter, c.Field, c.Value, c.Page)));
    }

    /// <summary>A server whose context holds <paramref name="context"/> tokens, counting one per <paramref name="charactersPerToken"/> characters.</summary>
    sealed class CountingServer(int context, double charactersPerToken) : IModelClient
    {
        public List<ModelRequest> Requests { get; } = [];

        public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) => Task.FromResult(new EndpointInfo(EndpointKind.BuiltIn, null, ["m"]));

        public Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var tokens = (int)((request.System.Length + request.User.Length) / charactersPerToken);
            return tokens >= context
                ? throw new ModelContextException("Too long.", tokens, context)
                : Task.FromResult("""{"chapters": []}""");
        }
    }

    /// <summary>A server whose model runs out of tokens part way through every answer.</summary>
    sealed class CutOffServer : IModelClient
    {
        public List<ModelRequest> Requests { get; } = [];

        public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) => Task.FromResult(new EndpointInfo(EndpointKind.BuiltIn, null, ["m"]));

        public Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            throw new ModelAnswerCutOffException("""{"chapters":[{"chapter":1,"creature":[{"value":"ogre","page":1,"quote":"an ogre bars the""", 2_048);
        }
    }

    [Fact]
    public async Task Chapters_whose_answer_is_cut_off_fail_by_themselves_and_say_why()
    {
        var server = new CutOffServer();

        var ex = await Assert.ThrowsAsync<ClassifierAnswerException>(
            () => new ChapterReader(server, "m").ReadAsync(DenseBatch(), _ => null, Vocabulary, TestContext.Current.CancellationToken));

        Assert.Contains("cut off", ex.Message, StringComparison.Ordinal);
        // No check request: the server answered.
        Assert.Single(server.Requests);
    }

    static ChapterBatch DenseBatch()
    {
        var page = string.Join(' ', Enumerable.Repeat("AC 15, HP 22 (4d8+4), Str 14 (+2), Dex 12 (+1).", 60));
        return new ChapterBatch("Stat Blocks", Chapters, [.. Enumerable.Range(0, 10).Select(i => new BookPage(i, page))]);
    }

    [Fact]
    public async Task Pages_the_model_counts_as_too_long_for_its_context_are_sent_again_cut_to_fit()
    {
        var server = new CountingServer(10_000, 2.0);

        var result = await new ChapterReader(server, "m").ReadAsync(DenseBatch(), _ => null, Vocabulary, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Tags.Count);
        Assert.Equal(2, server.Requests.Count);
        Assert.True(server.Requests[1].User.Length < server.Requests[0].User.Length / 2, $"{server.Requests[1].User.Length} characters.");
        // Every page is still read, each cut shorter, rather than the last pages left out.
        Assert.All(Enumerable.Range(1, 10), n => Assert.Contains($"[Page {n}]", server.Requests[1].User, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_batch_too_long_even_cut_down_fails_by_itself()
    {
        var server = new CountingServer(3_500, 1.0);

        var ex = await Assert.ThrowsAsync<ClassifierAnswerException>(
            () => new ChapterReader(server, "m").ReadAsync(DenseBatch(), _ => null, Vocabulary, TestContext.Current.CancellationToken));

        Assert.IsType<ModelContextException>(ex.InnerException);
        // No check request: the refusal already said the server is fine.
        Assert.Single(server.Requests);
    }

    [Fact]
    public void Chapter_tags_are_described_one_line_per_field()
    {
        ChapterTag[] tags =
        [
            new(ChapterFields.Creature, "ogre", "Ogre", 3, "an ogre"), new(ChapterFields.Environment, "swamp", "Swamp", 3, "the marsh"),
            new(ChapterFields.Creature, "goblin", "Goblin", 4, "goblins"),
        ];

        var lines = ChapterFields.Describe(tags);

        Assert.Contains("Creatures: Ogre, Goblin", lines);
        Assert.Equal(2, lines.Count);
    }
}
