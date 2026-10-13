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

    [Fact]
    public void A_page_that_reads_like_an_index_or_contents_keeps_half_its_score_and_falls_below_pages_that_answer()
    {
        // Page 1 is first by words, but it is the index; page 2 is second by words and page 3 first by meaning.
        var fused = AskRetrieval.Fuse([1, 2], [3], new HashSet<long>(), lists: new HashSet<long> { 1 });
        Assert.Equal([3L, 2L, 1L], fused.Select(p => p.PageId));
        Assert.True(fused[2].ListLike);
        Assert.Equal(0.5 / 61, fused[2].Score, 9);
        Assert.False(fused[0].ListLike);

        // Alone, it is still chosen.
        Assert.Equal([1L], AskRetrieval.Fuse([1], [], new HashSet<long>(), lists: new HashSet<long> { 1 }).Select(p => p.PageId));
    }

    [Fact]
    public void Index_contents_and_glossary_pages_read_as_lists_and_prose_tables_and_stat_blocks_do_not()
    {
        var index = string.Join('\n',
            "Aboleth 12", "Acid, damage 45, 67", "Actions 190–192", "bonus 187", "Advantage 173", "Alchemist's fire 151",
            "Animals", "riding 155, 157", "Armor Class 14, 144", "Attack roll 194–195", "Barbarian 46");
        var contents = "Contents Introduction ........ 4 The Mill ........ 12 The Inn ........ 20 The Crypt ........ 31 "
            + "The Dragon's Lair ........ 44 Appendix ........ 60";
        // An index whose lines were lost: words and page numbers, the numbers in short runs.
        var flat = string.Concat(Enumerable.Range(1, 12).Select(i => $"Entry{i} {i * 7}, {i * 7 + 2} topic {i * 11} "));
        var glossary = string.Join('\n',
            "Glossary", "Advantage. Roll twice, 173", "Bonus action. A quick deed, 189", "Concentration 203", "Darkvision 183",
            "Exhaustion 291", "Grappled 290", "Hit dice 186", "Initiative 177");
        Assert.True(AskRetrieval.IsListLike(index));
        Assert.True(AskRetrieval.IsListLike(contents));
        Assert.True(AskRetrieval.IsListLike(flat));
        Assert.True(AskRetrieval.IsListLike(glossary));

        var prose = string.Join('\n',
            "The red dragon sleeps beneath the old mill by the river, where the",
            "water turns the great wheel through the night. In the year 1492 the",
            "miller sealed the cellar door, and nobody has opened it since then.",
            "Those who listen at the door hear a slow breathing, deep as a bellows,",
            "and smell smoke where no fire burns. The innkeeper says it is only the",
            "wind in the flue, but she keeps a bucket of water by the stairs and",
            "will not send her boy down for ale after dark, not for any coin.",
            "See the map on page 12.");
        var statBlock = "Young Red Dragon Large dragon, chaotic evil Armor Class 18 (natural armor) Hit Points 178 (17d10 + 85) "
            + "Speed 40 ft., climb 40 ft., fly 80 ft. STR 23 (+6) DEX 10 (+0) CON 21 (+5) INT 14 (+2) WIS 11 (+0) CHA 19 (+4) "
            + "Saving Throws Dex +4, Con +9, Wis +4, Cha +8 Skills Perception +8, Stealth +4 Damage Immunities fire "
            + "Senses blindsight 30 ft., darkvision 120 ft., passive Perception 18 Languages Common, Draconic Challenge 10 (5,900 XP)";
        var slots = string.Join('\n', "Level Slots", "1st 2 0 0", "2nd 3 0 0", "3rd 4 2 0", "4th 4 3 0", "5th 4 3 2", "6th 4 3 3",
            "7th 4 3 3 1", "8th 4 3 3 2", "9th 4 3 3 3 1");
        Assert.False(AskRetrieval.IsListLike(prose));
        Assert.False(AskRetrieval.IsListLike(statBlock));
        Assert.False(AskRetrieval.IsListLike(slots));
        Assert.False(AskRetrieval.IsListLike(""));
        Assert.False(AskRetrieval.IsListLike("Chapter 3 ........ 45"));
    }

    [Fact]
    public void A_word_weighs_one_over_the_pages_that_have_it_and_one_on_no_page_weighs_as_much_as_the_rarest()
    {
        Assert.Equal([1.0 / 3, 1.0 / 200, 1.0], AskRetrieval.Weights([3, 200, 0]));
    }

    [Fact]
    public void A_page_with_the_rare_word_carries_the_question_and_one_with_only_common_words_does_not()
    {
        string[] words = ["lich", "spell", "rules"];
        int[] pagesWith = [3, 150, 180];

        var rare = AskRetrieval.WordShare(words, pagesWith, ["The lich hides its heart in a jar."]);
        var common = AskRetrieval.WordShare(words, pagesWith, ["These rules cover every spell you cast."]);
        var both = AskRetrieval.WordShare(words, pagesWith, ["These rules cover every spell you cast.", "The lich hides its heart in a jar."]);

        Assert.True(rare > 0.9);
        Assert.True(common < 0.1);
        Assert.Equal(rare, both);
        Assert.False(AskRetrieval.IsWeak(rare, null));
        Assert.True(AskRetrieval.IsWeak(common, null));

        // With words on as many pages, it's the share of the words: half is enough.
        Assert.Equal(0.5, AskRetrieval.WordShare(["dragon", "mill"], [4, 4], ["The dragon wakes."]));
        // Words the book doesn't have count against the page: one of four found is too little.
        Assert.Equal(0.25, AskRetrieval.WordShare(["starship", "crashed", "harbour", "engine"], [0, 0, 1, 0], ["The harbour is busy."]));
        Assert.Equal(0, AskRetrieval.WordShare([], [], ["Anything."]));
        Assert.Equal(0, AskRetrieval.WordShare(["dragon"], [2], []));
    }

    [Theory]
    // Half the question's weight on the best page by words is strong enough, whatever meaning says.
    [InlineData(0.5, null, false)]
    [InlineData(0.96, null, false)]
    [InlineData(0.3, null, true)]
    [InlineData(0, null, true)]
    // With vectors, a close page that stands out from the book's typical page is strong too.
    [InlineData(0, new[] { 0.62, 0.30, 0.31, 0.29 }, false)]
    // A close page in a book where every page is that close isn't...
    [InlineData(0.3, new[] { 0.62, 0.60, 0.61, 0.60 }, true)]
    // ...and nor is a page that stands out but isn't close.
    [InlineData(0, new[] { 0.40, 0.10, 0.12, 0.11 }, true)]
    public void The_weak_signal_guard_asks_no_model_when_neither_words_nor_meaning_find_anything(double wordShare, double[]? cosines, bool weak) =>
        Assert.Equal(weak, AskRetrieval.IsWeak(wordShare, cosines));

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
