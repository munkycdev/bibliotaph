namespace Bibliotaph.Classification.Tests;

/// <summary>
/// Asking the whole library without an index or a server: a few pages a book, pages ranked by meaning one at a time,
/// and pages of several books cited by book and page.
/// </summary>
public class LibraryAskRetrievalTests
{
    static RankedPage Ranked(long pageId, double score) => new(pageId, 1, null, false, score);

    [Fact]
    public void Capping_pages_per_book_keeps_each_books_best_in_order_and_lets_the_next_books_in()
    {
        // Pages 1 to 5 are book 10's, 6 and 7 book 20's; page 8's book isn't known.
        IReadOnlyList<RankedPage> ranked = [Ranked(1, 9), Ranked(2, 8), Ranked(3, 7), Ranked(6, 6), Ranked(4, 5), Ranked(8, 4), Ranked(7, 3), Ranked(5, 2)];
        var bookOf = new Dictionary<long, long> { [1] = 10, [2] = 10, [3] = 10, [4] = 10, [5] = 10, [6] = 20, [7] = 20 };

        var capped = AskRetrieval.CapPerBook(ranked, bookOf, 2);

        Assert.Equal([1, 2, 6, 8, 7], capped.Select(p => p.PageId));
        Assert.Same(ranked, AskRetrieval.CapPerBook(ranked, bookOf, int.MaxValue));
    }

    [Fact]
    public void The_evidence_takes_at_most_its_pages_per_book_once_capped()
    {
        var options = new AskRetrievalOptions { MaxPages = 4, MaxPagesPerBook = 1 };
        IReadOnlyList<RankedPage> ranked = [Ranked(1, 9), Ranked(2, 8), Ranked(3, 7)];
        var bookOf = new Dictionary<long, long> { [1] = 10, [2] = 10, [3] = 20 };
        var pages = new Dictionary<long, AskPage> { [1] = new(1, 0, "One."), [2] = new(2, 1, "Two."), [3] = new(3, 0, "Three.") };

        var chosen = AskRetrieval.Choose(AskRetrieval.CapPerBook(ranked, bookOf, options.MaxPagesPerBook), pages, options);

        Assert.Equal([1, 3], chosen.Select(p => p.PageId));
    }

    [Fact]
    public void Ranking_by_meaning_one_page_at_a_time_keeps_the_closest_as_ranking_them_all_would()
    {
        float[] question = [1, 0, 0];
        (long PageId, float[] Vector)[] pages =
        [
            (1, [0, 1, 0]), (2, [1, 1, 0]), (3, [1, 0, 0]), (4, [1, 0.1f, 0]), (5, [1, 0, 0]), (6, [0, 0, 1]),
        ];
        var ranking = new MeaningRanking(question, 3);
        foreach (var (pageId, vector) in pages) ranking.Add(pageId, vector);

        var all = AskRetrieval.RankByMeaning(question, pages);
        Assert.Equal(all.Take(3), ranking.Best());
        Assert.Equal([3, 5, 4], ranking.Best().Select(p => p.PageId));
        Assert.Equal(6, ranking.Count);
        Assert.Equal(pages.Select(p => AskRetrieval.Cosine(question, p.Vector)), ranking.Cosines);
    }

    [Fact]
    public void Pages_of_several_books_are_cited_by_book_then_page_numbered_in_the_order_the_books_come()
    {
        var markers = AskPrompt.LibraryMarkers([(40, 11, "12"), (7, 3, "4"), (40, 45, "12"), (7, 9, "x")]);

        // Book 40 comes first; its two pages printed 12 are told apart by their PDF pages, as in one book.
        Assert.Equal(["1:12 (PDF 12)", "2:4", "1:12 (PDF 46)", "2:x"], markers);
    }

    [Fact]
    public void A_page_of_the_library_is_sent_with_its_book_and_the_rules_say_how_to_cite_it()
    {
        var user = AskPrompt.UserMessage("Where is the lich?", [new AskPromptPage("2:112", 0, "The Crypt", "The lich waits below.", "The Sunken Keep")]);

        Assert.Contains("[Page 2:112]\nBook: The Sunken Keep\nHeading: The Crypt\nThe lich waits below.", user.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("[p. 2:112]", AskPrompt.LibrarySystemMessage, StringComparison.Ordinal);
        Assert.Contains(AskPrompt.NotAnswered, AskPrompt.LibrarySystemMessage, StringComparison.Ordinal);
        // A page of one book is sent as before, with no book line.
        Assert.DoesNotContain("Book:", AskPrompt.UserMessage("Where?", [new AskPromptPage("4", 3, null, "Text.")]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_citation_by_book_and_page_is_checked_like_any_other()
    {
        IReadOnlyList<AskPromptPage> pages = [new("1:12", 0, null, "The lich waits in the crypt below the keep.", "Keep"), new("2:4", 1, null, "Ghouls guard the gate.", "Gate")];

        var answer = AskAnswerCheck.Check("The lich waits in the crypt [p. 1:12]. Ghouls guard it [p. 2:4]. Nothing else [p. 12].", pages);

        Assert.Equal([0, 1], answer.CitedPages);
        Assert.Equal(2, answer.Citations);
        Assert.Equal(1, answer.RemovedCitations);
    }
}
