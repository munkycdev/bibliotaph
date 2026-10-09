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
    [InlineData("type:adventure", "type:adventure")]
    [InlineData("goblins level:3", "AND(goblins, level:3)")]
    [InlineData("level:1-5", "level:1-5")]
    [InlineData("levels:\"1 to 5\"", "level:1-5")]
    [InlineData("level:none", "level:n/a")]
    [InlineData("level:unknown", "level:unknown")]
    [InlineData("system:\"D&D 5e\"", "system:\"D&D 5e\"")]
    [InlineData("-system:unknown", "-system:unknown")]
    [InlineData("publisher:kobold author:\"Jane Doe\"", "AND(publisher:kobold, author:\"Jane Doe\")")]
    [InlineData("authors:doe tags:prep series:saltmarsh", "AND(author:doe, tag:prep, series:saltmarsh)")]
    [InlineData("edition:5e setting:eberron theme:horror env:urban", "AND(edition:5e, setting:eberron, theme:horror, environment:urban)")]
    [InlineData("type:adv*", "type:adv*")]
    public void Parses_metadata_fields(string text, string expected) => Assert.Equal(expected, Tree(text));

    [Theory]
    [InlineData("goblins length:short", 8, 12)]
    public void Fields_not_built_yet_say_so_and_are_left_out(string text, int start, int length)
    {
        var issue = OnlyIssue(text);

        Assert.Contains("isn't available yet", issue.Message, StringComparison.Ordinal);
        Assert.Equal((start, length), (issue.Start, issue.Length));
        Assert.Equal("goblins", Tree(text));
    }

    [Theory]
    [InlineData("level:high")]
    [InlineData("level:3*")]
    [InlineData("level:5-1")]
    [InlineData("level:40")]
    public void A_level_that_isnt_one_is_reported(string text) =>
        Assert.Contains("level: can be", OnlyIssue(text).Message, StringComparison.Ordinal);

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

    [Theory]
    [InlineData("red dragon", new[] { "red", "dragon" })]
    [InlineData("\"secret door\" -trap", new[] { "secret door" })]
    [InlineData("gob* (tavern OR inn) title:lairs", new[] { "gob", "tavern", "inn" })]
    [InlineData("-dragon", new string[0])]
    public void Highlight_terms_are_the_words_searched_for(string text, string[] expected) =>
        Assert.Equal(expected, SearchQuery.Parse(text).HighlightTerms());
}
