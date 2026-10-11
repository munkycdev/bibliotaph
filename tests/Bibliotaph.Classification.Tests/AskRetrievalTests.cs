namespace Bibliotaph.Classification.Tests;

/// <summary>Ask this book's retrieval without an index or a server: words, cosine, fusion, the weak-signal guard and the evidence budget.</summary>
public class AskRetrievalTests
{
    [Fact]
    public void A_question_keeps_the_words_worth_looking_for_once_each()
    {
        Assert.Equal(["dragon", "sleep", "mill"], AskRetrieval.Words("Where does the Dragon sleep? Is it under the mill, the dragon?"));
        Assert.Empty(AskRetrieval.Words("What is it about?"));
    }

    [Fact]
    public void The_words_become_an_fts_expression_that_finds_any_of_them_and_nothing_typed_becomes_syntax()
    {
        var words = AskRetrieval.Words("\"lich\" OR NEAR(crypt) 5e*");
        Assert.Equal(["lich", "near", "crypt", "5e"], words);
        // Longer words find words that start with them, as search does; short ones only themselves.
        Assert.Equal("\"lich\"* OR \"near\"* OR \"crypt\"* OR \"5e\"", AskRetrieval.WordsMatch(words));
        Assert.Null(AskRetrieval.WordsMatch([]));
    }

    [Fact]
    public void Coverage_counts_the_question_words_a_text_has_by_their_start_and_ignores_accents()
    {
        Assert.Equal(2, AskRetrieval.Coverage(["dragon", "lair", "goblin"], "The DRAGONS keep a lair under the Café."));
        Assert.Equal(1, AskRetrieval.Coverage(["cafe"], "Meet at the Café."));
        Assert.Equal(0, AskRetrieval.Coverage(["5e"], "5th edition"));
    }

    [Fact]
    public void Cosine_is_one_for_the_same_direction_zero_for_unrelated_and_zero_for_nothing()
    {
        Assert.Equal(1, AskRetrieval.Cosine([1, 2, 3], [2, 4, 6]), 6);
        Assert.Equal(0, AskRetrieval.Cosine([1, 0], [0, 1]), 6);
        Assert.Equal(-1, AskRetrieval.Cosine([1, 0], [-3, 0]), 6);
        Assert.Equal(0, AskRetrieval.Cosine([0, 0], [1, 1]));
        Assert.Equal(0, AskRetrieval.Cosine([1, 2], [1, 2, 3]));
    }

    [Fact]
    public void Pages_are_ranked_by_meaning_closest_first()
    {
        var ranked = AskRetrieval.RankByMeaning([1, 0], [(10, [0f, 1f]), (11, [1f, 0.1f]), (12, [1f, 1f])]);
        Assert.Equal([11L, 12L, 10L], ranked.Select(r => r.PageId));
    }

    [Fact]
    public void Reciprocal_rank_fusion_favours_a_page_both_signals_found_and_a_heading_lifts_a_page_but_brings_none_in()
    {
        // Page 1 is first by words only; page 2 is second by both; page 3 is first by meaning only.
        var fused = AskRetrieval.Fuse([1, 2], [3, 2], new HashSet<long>());
        Assert.Equal([2L, 1L, 3L], fused.Select(p => p.PageId));
        Assert.Equal((2, 2), (fused[0].WordRank, fused[0].MeaningRank));
        Assert.Equal(1.0 / 62 + 1.0 / 62, fused[0].Score, 9);
        Assert.Equal((1, (int?)null), (fused[1].WordRank, fused[1].MeaningRank));

        // A matching bookmark over page 3 lifts it above page 1; one over page 9, which neither signal found, adds nothing.
        var boosted = AskRetrieval.Fuse([1, 2], [3, 2], new HashSet<long> { 3, 9 });
        Assert.Equal([2L, 3L, 1L], boosted.Select(p => p.PageId));
        Assert.True(boosted[1].Boosted);
        Assert.DoesNotContain(boosted, p => p.PageId == 9);
    }

    [Fact]
    public void Each_signal_ranks_only_as_deep_as_the_options_say()
    {
        var fused = AskRetrieval.Fuse([1, 2, 3, 4], [], new HashSet<long>(), new AskRetrievalOptions { RankDepth = 2 });
        Assert.Equal([1L, 2L], fused.Select(p => p.PageId));
    }

    [Theory]
    // Half the words (at least one) on the best page by words is strong enough, whatever meaning says.
    [InlineData(3, 2, null, false)]
    [InlineData(3, 1, null, true)]
    [InlineData(1, 1, null, false)]
    [InlineData(0, 0, null, true)]
    // With vectors, a close page that stands out from the book's typical page is strong too.
    [InlineData(3, 0, new[] { 0.62, 0.30, 0.31, 0.29 }, false)]
    // A close page in a book where every page is that close isn't...
    [InlineData(3, 0, new[] { 0.62, 0.60, 0.61, 0.60 }, true)]
    // ...and nor is a page that stands out but isn't close.
    [InlineData(3, 0, new[] { 0.40, 0.10, 0.12, 0.11 }, true)]
    public void The_weak_signal_guard_asks_no_model_when_neither_words_nor_meaning_find_anything(int words, int coverage, double[]? cosines, bool weak) =>
        Assert.Equal(weak, AskRetrieval.IsWeak(words, coverage, cosines));

    [Fact]
    public void Each_page_is_under_the_last_bookmark_before_it_with_those_above_it()
    {
        BookHeading[] outline =
        [
            new("Part One", 0, 0), new("The Mill", 2, 1), new("The Inn", 5, 1), new("Part Two", 8, 0), new("Elsewhere", -1, 1),
        ];
        var headings = AskRetrieval.Headings(outline, Enumerable.Range(0, 10));
        Assert.Equal(["Part One"], headings[1].Titles);
        Assert.Equal(["Part One", "The Mill"], headings[3].Titles);
        Assert.Equal("The Inn", headings[7].Title);
        Assert.Equal(["Part Two"], headings[9].Titles);

        var under = AskRetrieval.UnderMatchingHeadings(["mill"], headings);
        Assert.Equal([2, 3, 4], under.Order());
        // A chapter's word lifts the pages of its sections too.
        Assert.Equal(10, AskRetrieval.UnderMatchingHeadings(["part"], headings).Count);
        Assert.Equal([5, 6, 7], AskRetrieval.UnderMatchingHeadings(["inn"], headings).Order());
        Assert.Empty(AskRetrieval.Headings([], [1, 2]));
    }

    [Fact]
    public void The_evidence_is_the_best_pages_within_the_page_count_and_the_budget()
    {
        var pages = Enumerable.Range(1, 10).ToDictionary(i => (long)i, i => new AskPage(i, i - 1, new string('a', 3_000) + $" page {i}"));
        pages[4] = pages[4] with { Text = "   " };
        var ranked = AskRetrieval.Fuse([3, 4, 1, 2, 5, 6, 7, 8, 9, 10], [], new HashSet<long>());

        var chosen = AskRetrieval.Choose(ranked, pages, new AskRetrievalOptions { MaxPages = 3 });
        // Page 4 has no text, so it's passed over.
        Assert.Equal([3L, 1L, 2L], chosen.Select(p => p.PageId));

        var budgeted = AskRetrieval.Choose(ranked, pages, new AskRetrievalOptions { Budget = 7_000, PageCharacters = 2_000 });
        Assert.Equal(4, budgeted.Count);
        Assert.All(budgeted, p => Assert.True(p.Text.Length <= 2_001));
        Assert.True(budgeted.Sum(p => p.Text.Length) <= 7_003);
    }

    [Fact]
    public void A_passage_starts_near_the_first_question_word_and_names_it_as_the_page_spells_it()
    {
        var text = string.Join(' ', Enumerable.Repeat("filler words go here", 40)) + " At midnight the Dragons wake beneath the mill. " + string.Join(' ', Enumerable.Repeat("more filler", 40));
        var passage = AskRetrieval.Excerpt(text, ["dragon"], 200);
        Assert.Equal("Dragons", passage.Phrase);
        Assert.StartsWith("…", passage.Text, StringComparison.Ordinal);
        Assert.Contains("Dragons wake beneath", passage.Text, StringComparison.Ordinal);
        Assert.True(passage.Text.Length <= 202);

        var opening = AskRetrieval.Excerpt("The gate is shut.", ["dragon"]);
        Assert.Equal(new Passage("The gate is shut.", null), opening);
    }

    [Fact]
    public void Nomic_embedding_models_get_their_task_prefixes_and_others_the_text_as_it_is()
    {
        Assert.Equal("search_document: The Mill. The wheel turns.", EmbeddingText.Document("nomic-embed-text", "The Mill", "The wheel\n turns."));
        Assert.Equal("search_query: where is the mill?", EmbeddingText.Query("nomic-embed-text:latest", "where is the mill?"));
        Assert.Equal("The wheel turns.", EmbeddingText.Document("bge-m3", null, "The wheel turns."));
        Assert.True(EmbeddingText.Document("bge-m3", null, new string('x', 10_000)).Length <= EmbeddingText.MaxCharacters + 1);
    }
}
