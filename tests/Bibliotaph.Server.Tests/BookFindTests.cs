using Bibliotaph.Api.Contracts;
using Bibliotaph.Web.Components.Reader;

namespace Bibliotaph.Server.Tests;

/// <summary>Find in the web reader steps through a book's matches.</summary>
public sealed class BookFindTests
{
    static readonly ReaderFind Found = new([new ReaderFindPage(2, 2), new ReaderFindPage(5, 1), new ReaderFindPage(9, 1)]);

    [Fact]
    public void Each_match_on_a_page_counts()
    {
        Assert.Equal([(2, 0), (2, 1), (5, 0), (9, 0)], BookFind.Hits(Found));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(12, 0)]
    public void Finding_starts_at_the_page_in_view_and_wraps_round(int page, int first)
    {
        var find = new BookFind();
        Assert.Equal(first, find.Take("owlbear", BookFind.Hits(Found), page));
        Assert.Equal("owlbear", find.Found);
    }

    [Fact]
    public void Stepping_goes_round_from_the_last_match_to_the_first()
    {
        var find = new BookFind();
        find.Show(find.Take("owlbear", BookFind.Hits(Found), 9));
        Assert.Equal("4 of 4", find.Status);
        Assert.Equal(0, find.Next(forward: true));
        Assert.Equal(2, find.Next(forward: false));
        Assert.Equal((2, 0), find.Show(find.Next(forward: true)));
        Assert.Equal(3, find.Next(forward: false));
    }

    [Fact]
    public void No_matches_leave_nothing_to_step_through()
    {
        var find = new BookFind();
        Assert.Equal(-1, find.Take("owlbear", [], 0));
        Assert.False(find.HasMatches);
        Assert.False(find.HasFound("owlbear"));
    }

    [Fact]
    public void The_same_words_again_step_while_whole_file_forgets_them()
    {
        var find = new BookFind { Text = "owlbear" };
        find.Show(find.Take("owlbear", BookFind.Hits(Found), 0));
        Assert.True(find.HasFound("owlbear"));
        Assert.False(find.HasFound("owlbears"));
        find.Forget();
        Assert.False(find.HasFound("owlbear"));
        Assert.Equal("owlbear", find.Text);
    }

    [Fact]
    public void Clearing_drops_the_status_too()
    {
        var find = new BookFind();
        find.Show(find.Take("owlbear", BookFind.Hits(Found), 0));
        find.Clear();
        Assert.Equal("", find.Status);
        Assert.False(find.HasMatches);
    }
}
