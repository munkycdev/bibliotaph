namespace Bibliotaph.Processing.Tests;

/// <summary>A search found only inside books says where its words are, rather than that nothing is here.</summary>
public sealed class LibraryTextTests
{
    [Theory]
    [InlineData(52, 7, "No book's title or details have these words, but 52 pages in 7 books do.")]
    [InlineData(1, 1, "No book's title or details have these words, but 1 page in 1 book does.")]
    public void Says_how_many_pages_have_the_words(long pages, long books, string said) =>
        Assert.Equal(said, LibraryText.OnlyInside(pages, books));
}
