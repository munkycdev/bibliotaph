using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>Needs review's words read as sentences, whatever the card was made from.</summary>
public sealed class ReviewWordingTests
{
    static PendingTerm Term(string? example) =>
        new(1, new Term("system", "tidewater", "Tidewater"), MetadataFields.System, 3, example);

    [Theory]
    [InlineData("…for Tidewater, second edition…")]
    [InlineData("“…for Tidewater, second edition…”")]
    [InlineData("\"…for Tidewater, second edition…\" ")]
    public void A_term_s_example_is_quoted_once(string example) =>
        Assert.Equal("“…for Tidewater, second edition…”", ReviewText.TermExample(Term(example)));

    [Fact]
    public void A_new_term_s_chip_asks() => Assert.EndsWith("?", ReviewText.TermChip(Term(null)), StringComparison.Ordinal);

    [Fact]
    public void Both_tabs_show_their_counts_alike()
    {
        Assert.Equal("Metadata suggestions · 3", ReviewText.SuggestionsTab(3));
        Assert.Equal("Files needing attention · 1", ReviewText.FilesTab(1));
    }

    [Fact]
    public void A_collection_suggestion_with_nothing_named_still_reads_as_a_sentence()
    {
        var books = new[] { new SuggestedBook(new EntryId(1), "The Salt Road", 1), new SuggestedBook(new EntryId(2), "The Glass Ferry", 2) };
        var text = new CollectionSuggestionCardText(new CollectionSuggestion(1, "Sunken Coast", books), new Vocabulary([]));

        Assert.Equal("These books are like most of the books in this collection.", text.Detail);
    }

    [Fact]
    public void A_collection_suggestion_says_what_the_books_share()
    {
        var books = new[] { new SuggestedBook(new EntryId(1), "The Salt Road", 1) };
        var text = new CollectionSuggestionCardText(new CollectionSuggestion(1, "Sunken Coast", books, Series: "Tidewater Tales"), new Vocabulary([]));

        Assert.Equal("Most of the books in this collection are in the series “Tidewater Tales”, and so is this one.", text.Detail);
    }
}
