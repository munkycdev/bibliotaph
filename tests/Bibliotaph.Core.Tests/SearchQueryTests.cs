using Bibliotaph.Core.Search;

namespace Bibliotaph.Core.Tests;

public class SearchQueryTests
{
    static string Tree(string text) => SearchQuery.Parse(text).Root?.ToString() ?? "";

    static QueryIssue OnlyIssue(string text) => Assert.Single(SearchQuery.Parse(text).Issues);

    [Theory]
    [InlineData("dragon", "dragon")]
    [InlineData("  red   dragon ", "AND(red, dragon)")]
    [InlineData("\"secret door\"", "\"secret door\"")]
    [InlineData("“secret   door”", "\"secret door\"")]
    [InlineData("gob*", "gob*")]
    [InlineData("chase -city", "AND(chase, -city)")]
    [InlineData("tavern OR inn", "OR(tavern, inn)")]
    [InlineData("tavern OR inn OR alehouse", "OR(tavern, inn, alehouse)")]
    [InlineData("ghost tavern OR inn", "OR(AND(ghost, tavern), inn)")]
    [InlineData("ghost (tavern OR inn)", "AND(ghost, OR(tavern, inn))")]
    [InlineData("(a b) c", "AND(a, b, c)")]
    [InlineData("half-orc", "half-orc")]
    [InlineData("-\"ancient red\" lair", "AND(-\"ancient red\", lair)")]
    [InlineData("title:dragon", "title:dragon")]
    [InlineData("TITLE:\"red dragon\" lair", "AND(title:\"red dragon\", lair)")]
    [InlineData("title:drag*", "title:drag*")]
    [InlineData("-title:dragon", "-title:dragon")]
    [InlineData("format:PDF", "format:pdf")]
    [InlineData("format:jpeg", "format:jpg")]
    [InlineData("format:images maps", "AND(format:image, maps)")]
    [InlineData("folder:\"Monster Manuals\"", "folder:\"Monster Manuals\"")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("or nor", "AND(or, nor)")]
    public void Parses(string text, string expected) => Assert.Equal(expected, Tree(text));

    [Fact]
    public void An_empty_box_is_an_empty_query()
    {
        Assert.True(SearchQuery.Parse("").IsEmpty);
        Assert.True(SearchQuery.Parse("   ").IsEmpty);
        Assert.True(SearchQuery.Parse(null).IsEmpty);
        Assert.Empty(SearchQuery.Parse("  ").Issues);
    }

    [Fact]
    public void An_unclosed_quote_is_closed_and_pointed_at()
    {
        const string Text = "lair \"ancient red";
        var query = SearchQuery.Parse(Text);

        Assert.Equal("AND(lair, \"ancient red\")", query.Root!.ToString());
        var issue = Assert.Single(query.Issues);
        Assert.Equal((5, Text.Length - 5), (issue.Start, issue.Length));
    }

    [Fact]
    public void An_unclosed_bracket_is_closed_at_the_end()
    {
        var query = SearchQuery.Parse("ghost (tavern OR inn");

        Assert.Equal("AND(ghost, OR(tavern, inn))", query.Root!.ToString());
        Assert.Equal(6, Assert.Single(query.Issues).Start);
    }

    [Fact]
    public void A_stray_closing_bracket_is_ignored()
    {
        var query = SearchQuery.Parse("ghost) tavern");

        Assert.Equal("AND(ghost, tavern)", query.Root!.ToString());
        Assert.Equal(5, Assert.Single(query.Issues).Start);
    }

    [Theory]
    [InlineData("type:adventure", 0, 14)]
    [InlineData("goblins level:3", 8, 7)]
    [InlineData("system:\"D&D 5e\"", 0, 15)]
    public void Metadata_fields_say_they_arrive_later_and_are_left_out(string text, int start, int length)
    {
        var issue = OnlyIssue(text);

        Assert.Contains("arrives once books have metadata", issue.Message, StringComparison.Ordinal);
        Assert.Equal((start, length), (issue.Start, issue.Length));
    }

    [Fact]
    public void The_rest_of_the_query_runs_without_a_metadata_field() =>
        Assert.Equal("goblins", Tree("goblins level:3"));

    [Theory]
    [InlineData("title:", "needs a value")]
    [InlineData("title: dragon", "needs a value")]
    [InlineData("format:docx", "format: can be")]
    [InlineData("format:pd*", "format: can be")]
    [InlineData("OR dragon", "OR needs something on both sides")]
    [InlineData("dragon OR", "OR needs something on both sides")]
    [InlineData("dragon OR -lich", "exclusion can't be one side of OR")]
    [InlineData("title:dragon OR lich", "can't be combined with OR")]
    [InlineData("dragon - lich", "no letters or numbers")]
    [InlineData("dragon -", "no letters or numbers")]
    [InlineData("*", "needs some letters")]
    [InlineData("dragon \"\"", "nothing to search for")]
    [InlineData("()", "empty")]
    [InlineData("dr*gon", "only works at the end")]
    public void Problems_are_reported_not_thrown(string text, string message) =>
        Assert.Contains(message, OnlyIssue(text).Message, StringComparison.Ordinal);

    [Fact]
    public void Deep_nesting_is_cut_off_rather_than_recursing_forever()
    {
        var text = new string('(', 5000) + "dragon" + new string(')', 5000);

        var query = SearchQuery.Parse(text);

        Assert.Contains(query.Issues, i => i.Message.Contains("nested too deeply", StringComparison.Ordinal));
    }

    [Fact]
    public void Equal_queries_have_equal_trees() =>
        Assert.Equal(SearchQuery.Parse("a (b c)").Root, SearchQuery.Parse("a b c").Root);
}
