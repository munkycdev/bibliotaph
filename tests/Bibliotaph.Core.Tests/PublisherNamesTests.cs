using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

/// <summary>One publisher under one name, whatever legal words and punctuation its books' names carry.</summary>
public sealed class PublisherNamesTests
{
    [Theory]
    [InlineData("Lantern Press", "Lantern Press Inc.")]
    [InlineData("Lantern Press", "Lantern Press, Incorporated")]
    [InlineData("Lantern Press", "lantern press corp")]
    [InlineData("Lantern Press", "Lantern Press Corporation")]
    [InlineData("Lantern Press", "Lantern Press Co.")]
    [InlineData("Lantern Press", "Lantern Press Company")]
    [InlineData("Lantern Press", "Lantern Press LLC")]
    [InlineData("Lantern Press", "Lantern Press L.L.C.")]
    [InlineData("Lantern Press", "Lantern Press Ltd")]
    [InlineData("Lantern Press", "Lantern Press Limited")]
    [InlineData("Lantern Press", "Lantern Press GmbH")]
    [InlineData("Lantern Press", "Lantern Press S.A.")]
    [InlineData("Lantern Press", "The Lantern Press")]
    [InlineData("Owl & Oak", "Owl and Oak", false)]
    [InlineData("Lantern Press", "Lantern Publishing", false)]
    public void Names_of_one_publisher_match(string a, string b, bool same = true) => Assert.Equal(same, PublisherNames.Same(a, b));

    [Fact]
    public void A_name_that_is_only_a_legal_word_keeps_it() => Assert.Equal("company", PublisherNames.Key("The Company"));

    [Fact]
    public void Files_under_the_name_most_books_use_and_the_shorter_on_a_tie()
    {
        Assert.Equal("Lantern Press Inc.", PublisherNames.Choose([("Lantern Press", 1), ("Lantern Press Inc.", 3)]));
        Assert.Equal("Lantern Press", PublisherNames.Choose([("Lantern Press Corporation", 2), ("Lantern Press", 2)]));
    }

    [Fact]
    public void Groups_each_name_under_its_publisher_with_its_books_in_all()
    {
        var groups = PublisherNames.Group([("Lantern Press", 2), ("Lantern Press Corporation", 1), ("Quill Games", 1)]);

        Assert.Equal(("Lantern Press", 3L), groups["Lantern Press Corporation"]);
        Assert.Equal(("Quill Games", 1L), groups["Quill Games"]);
    }

    [Fact]
    public void A_new_name_takes_the_library_s_form_of_it()
    {
        (string, long)[] known = [("Lantern Press", 4), ("Quill Games", 1)];

        Assert.Equal("Lantern Press", PublisherNames.Existing("Lantern Press, Inc.", known));
        Assert.Null(PublisherNames.Existing("Lantern Press", known));
        Assert.Null(PublisherNames.Existing("Marrow House", known));
    }
}
