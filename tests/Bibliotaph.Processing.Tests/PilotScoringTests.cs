using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>The model pilot's arithmetic (slice 2d): the sample, when two values mean the same, and the scores.</summary>
public sealed class PilotScoringTests
{
    static readonly Vocabulary Vocabulary = new(
        [new Term("system", "dnd", "Dungeons & Dragons", "D&D"), new Term("type", "adventure", "Adventure"), new Term("type", "map", "Map"),
            new Term("publisher", "wotc", "Wizards of the Coast")],
        [("type", "adventure", "module"), ("publisher", "wotc", "WotC")]);

    static readonly PilotMatcher Matcher = new(Vocabulary);

    [Fact]
    public void The_sample_takes_turns_across_folders_and_systems_and_is_the_same_each_time()
    {
        // Ninety books in one folder and ten in each of two others: a round robin takes from all three.
        List<PilotCandidate> candidates =
        [
            .. Enumerable.Range(1, 90).Select(i => new PilotCandidate(i, "D&D 5e / Adventures", "dnd")),
            .. Enumerable.Range(101, 10).Select(i => new PilotCandidate(i, "Pathfinder", "pf2e")),
            .. Enumerable.Range(201, 10).Select(i => new PilotCandidate(i, "Maps", null)),
        ];

        var picked = PilotSample.Pick(candidates, 30);

        Assert.Equal(30, picked.Count);
        Assert.Equal(picked.Count, picked.Distinct().Count());
        Assert.Equal(10, picked.Count(id => id is > 100 and <= 110));
        Assert.Equal(10, picked.Count(id => id > 200));
        Assert.Equal(picked, PilotSample.Pick(candidates, 30));
        Assert.NotEqual(picked, PilotSample.Pick(candidates, 30, seed: 7));
        Assert.Equal(110, PilotSample.Pick(candidates, 500).Count);
    }

    [Theory]
    [InlineData("title", "Tomb of the Serpent Kings", "tomb of the serpent kings", true)]
    [InlineData("title", "Tomb of the Serpent Kings: A Dungeon for Beginners", "Tomb of the Serpent Kings", true)]
    [InlineData("title", "Tomb of the Serpent Kings – Second Printing", "Tomb of the Serpent Kings", true)]
    [InlineData("title", "Tomb of the Serpent Kings: Part 1", "Tomb of the Serpent Kings: Part 2", false)]
    [InlineData("title", "Tomb", "Tomb of the Serpent Kings", false)]
    [InlineData("publisher", "Lantern Works, Inc.", "Lantern Works", true)]
    [InlineData("publisher", "WotC", "Wizards of the Coast LLC", true)]
    [InlineData("publisher", "Lantern Works", "Evil Corp", false)]
    [InlineData("system", "dnd", "D&D", true)]
    [InlineData("system", "Dungeons and Dragons", "dnd", false)]
    [InlineData("type", "module", "adventure", true)]
    [InlineData("type", "Heist kit", "heist  KIT", true)]
    [InlineData("levels", "1-5", "levels 1 to 5", true)]
    [InlineData("levels", "3", "1-3", false)]
    public void Values_match_by_meaning(string field, string a, string b, bool same)
    {
        Assert.Equal(same, Matcher.Same(MetadataFields.Get(field), a, b));
        Assert.Equal(same, Matcher.Same(MetadataFields.Get(field), b, a));
    }

    [Fact]
    public void Scores_count_right_values_complete_fields_and_books_to_correct()
    {
        static PilotProposal Kept(string model, long book, string field, string value) => new(model, book, field, value, 0, "quote", true);
        List<PilotRun> runs =
        [
            new("good", 1, 10, null), new("good", 2, 20, null), new("good", 3, 30, null),
            new("bad", 1, 5, null), new("bad", 2, 0, "It answered in prose."), new("bad", 3, 7, null),
        ];
        List<PilotProposal> proposals =
        [
            Kept("good", 1, "title", "The Sunken Lantern"), Kept("good", 1, "system", "dnd"), Kept("good", 1, "type", "module"),
            Kept("good", 2, "title", "Harbour Maps"), Kept("good", 2, "type", "map"),
            Kept("good", 3, "title", "Unanswered Book"),
            // Bad gets the title right but adds a second title the Classify stage would never store, a wrong system,
            // and a type for a field the user said the book doesn't state.
            Kept("bad", 1, "title", "The Sunken Lantern"), Kept("bad", 1, "title", "Something Else"), Kept("bad", 1, "system", "pf2e"),
            Kept("bad", 1, "levels", "3"),
            new("bad", 1, "publisher", "Evil Corp", 3, "not on the page", false, "The quote isn't on page 4."),
        ];
        var answers = new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            [1] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["title"] = ["The Sunken Lantern"],
                ["system"] = ["dnd"],
                ["type"] = ["adventure"],
                ["publisher"] = [PilotStore.NotInBook],
                ["levels"] = [PilotStore.NotInBook],
            },
            [2] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["title"] = ["Harbour Maps"],
                ["type"] = ["map", "adventure"],
            },
        };

        var scores = PilotScoring.Score(["good", "bad"], runs, proposals, answers, Matcher);

        var good = scores[0];
        Assert.Equal((3, 0, 2, 0), (good.Books, good.Failures, good.Answered, good.NeedCorrection));
        Assert.Equal(20, good.MedianSeconds);
        Assert.Equal(1.0, good.Precision);
        // Title twice, system once, type twice (book 2 lacks "adventure"); publisher isn't answerable.
        Assert.Equal(4.0 / 5, good.Coverage);
        Assert.Equal(0.0, good.CorrectionRate);
        Assert.True(good.Passes);

        var bad = scores[1];
        Assert.Equal((3, 1, 2, 1), (bad.Books, bad.Failures, bad.Answered, bad.NeedCorrection));
        Assert.Equal(6, bad.MedianSeconds);
        Assert.Equal(new PilotFieldScore("title", 1, 1, 2, 1), bad.Fields.Single(f => f.Field == "title"));
        Assert.Equal(0.5, bad.Precision);
        Assert.Equal(0.5, bad.CorrectionRate);
        Assert.False(bad.Passes);
        Assert.Equal(
            [("Game system", "pf2e", "Dungeons & Dragons"), ("Levels", "3", "not in this book")],
            bad.Mistakes.Select(m => (m.Field, m.Proposed, m.Answer)));
        Assert.Same(good, PilotScoring.Best(scores));
    }

    [Fact]
    public void The_book_list_is_read_by_its_leading_numbers()
    {
        string[] lines = ["# The books", "", "12\tC:\\Games\\a.pdf", "  7 ", "x\tnot a number", "12\tagain", "# 99"];

        Assert.Equal([12L, 7, 12], PilotService.ParseBookList(lines));
        Assert.Equal(["qwen3:8b", "llama3.1:8b"], PilotService.ParseModels(" qwen3:8b, llama3.1:8b ,qwen3:8b,"));
    }
}
