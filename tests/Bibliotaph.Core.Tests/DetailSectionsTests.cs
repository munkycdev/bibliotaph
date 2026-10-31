namespace Bibliotaph.Core.Tests;

public class DetailSectionsTests
{
    [Fact]
    public void Nothing_stored_folds_the_file_sections()
    {
        Assert.Equal([DetailSections.File, DetailSections.Where, DetailSections.Copies, DetailSections.Indexing],
            DetailSections.All.Where(DetailSections.Read(null).Contains));
    }

    [Fact]
    public void Everything_opened_is_stored_as_nothing_folded()
    {
        Assert.Equal("", DetailSections.Write([]));
        Assert.Empty(DetailSections.Read(""));
    }

    [Fact]
    public void Folded_sections_are_stored_in_page_order_and_read_back()
    {
        var stored = DetailSections.Write([DetailSections.Indexing, DetailSections.Collections]);
        Assert.Equal("collections,indexing", stored);
        Assert.Equal(new HashSet<string> { DetailSections.Collections, DetailSections.Indexing }, DetailSections.Read(stored));
    }

    [Fact]
    public void A_section_no_longer_shown_is_ignored() => Assert.Equal(new HashSet<string> { DetailSections.Details }, DetailSections.Read("details,gone"));
}
