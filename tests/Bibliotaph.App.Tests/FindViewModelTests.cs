using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Viewer;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.App.Tests;

/// <summary>
/// Find in the reader's book: where it starts, Next and Previous going round, Enter again, the adventure's pages and
/// Whole file, clearing, and a page the Ask panel found.
/// </summary>
public class FindViewModelTests
{
    readonly List<(int Page, double? Top)> _went = [];
    readonly List<string> _searched = [];
    IReadOnlyList<SearchHit> _hits = [Hit(1, 700), Hit(4, 500), Hit(9, 300)];
    int _page;
    PartPages? _part;

    /// <summary>A find over a book open on document 7, whose matches are <see cref="_hits"/>.</summary>
    FindViewModel Open()
    {
        var find = new FindViewModel(() => _page, () => _part, (page, top) => _went.Add((page, top)), NullLogger.Instance);
        find.Attach(7, (text, most) =>
        {
            _searched.Add(text);
            return Task.FromResult(_hits);
        });
        return find;
    }

    static SearchHit Hit(int page, double top) => new(page, 0, 4, [new PdfRect(72, top, 110, top - 12)]);

    /// <summary>A page the reader was taken to, and the spot on it when there was one.</summary>
    static (int Page, double? Top) At(int page, double? top = null) => (page, top);

    [Fact]
    public async Task Find_starts_at_the_first_match_from_the_page_in_view()
    {
        var find = Open();
        _page = 3;
        find.Query = "  bell ";

        await find.FindCommand.ExecuteAsync(null);

        Assert.Equal("2 of 3", find.Status);
        Assert.Equal(At(4, 500), _went[^1]);
        Assert.Equal(1, find.Highlights.Current);
        Assert.Equal(3, find.Highlights.Items.Count);
        Assert.Equal(["bell"], _searched);
    }

    [Fact]
    public async Task Past_the_last_match_Find_goes_round_to_the_first()
    {
        var find = Open();
        _page = 10;
        find.Query = "bell";

        await find.FindCommand.ExecuteAsync(null);

        Assert.Equal("1 of 3", find.Status);
        Assert.Equal(At(1, 700), _went[^1]);
    }

    [Fact]
    public async Task Next_after_the_last_match_goes_round_to_the_first()
    {
        var find = Open();
        _page = 9;
        find.Query = "bell";
        await find.FindCommand.ExecuteAsync(null);

        find.NextCommand.Execute(null);

        Assert.Equal("1 of 3", find.Status);
        Assert.Equal(0, find.Highlights.Current);
        Assert.Equal(At(1, 700), _went[^1]);
    }

    [Fact]
    public async Task Previous_from_the_first_match_goes_round_to_the_last()
    {
        var find = Open();
        find.Query = "bell";
        await find.FindCommand.ExecuteAsync(null);

        find.PreviousCommand.Execute(null);

        Assert.Equal("3 of 3", find.Status);
        Assert.Equal(2, find.Highlights.Current);
        Assert.Equal(At(9, 300), _went[^1]);
    }

    [Fact]
    public async Task Enter_again_goes_to_the_next_match_without_searching_again()
    {
        var find = Open();
        find.Query = "bell";
        await find.FindCommand.ExecuteAsync(null);

        await find.FindCommand.ExecuteAsync(null);

        Assert.Equal("2 of 3", find.Status);
        Assert.Equal(["bell"], _searched);
    }

    [Fact]
    public void Next_and_Previous_do_nothing_before_anything_is_found()
    {
        var find = Open();

        find.NextCommand.Execute(null);
        find.PreviousCommand.Execute(null);

        Assert.Empty(_went);
        Assert.Equal("", find.Status);
    }

    [Fact]
    public async Task No_matches_says_so_and_shows_the_search_marks_again()
    {
        var find = Open();
        IReadOnlyList<PageHighlight> terms = [new(2, [new PageRect(72, 640, 110, 628)])];
        find.MarkTerms(terms, showFirst: true);
        _hits = [];
        find.Query = "dragon";

        await find.FindCommand.ExecuteAsync(null);

        Assert.Equal("No matches", find.Status);
        Assert.Same(terms, find.Highlights.Items);
        Assert.Equal(-1, find.Highlights.Current);
        Assert.Empty(_went);
    }

    [Fact]
    public async Task In_an_adventure_Find_looks_in_its_pages_until_Whole_file_is_ticked()
    {
        var find = Open();
        _part = new PartPages(5, 8);
        find.Query = "bell";

        await find.FindCommand.ExecuteAsync(null);
        var inPart = find.Status;
        find.WholeFile = true;

        Assert.Equal("No matches in this adventure", inPart);
        Assert.Equal("1 of 3", find.Status);
        Assert.Equal(["bell", "bell"], _searched);
    }

    [Fact]
    public async Task Clearing_empties_the_box_and_shows_the_search_marks_again()
    {
        var find = Open();
        IReadOnlyList<PageHighlight> terms = [new(2, [new PageRect(72, 640, 110, 628)])];
        find.MarkTerms(terms, showFirst: true);
        find.Query = "bell";
        await find.FindCommand.ExecuteAsync(null);

        find.ClearCommand.Execute(null);
        find.NextCommand.Execute(null);

        Assert.Equal("", find.Query);
        Assert.Equal("", find.Status);
        Assert.Same(terms, find.Highlights.Items);
        Assert.Equal(-1, find.Highlights.Current);
        Assert.Single(_went);
    }

    [Fact]
    public async Task Closing_the_book_drops_its_matches_and_keeps_the_box()
    {
        var find = Open();
        find.Query = "bell";
        await find.FindCommand.ExecuteAsync(null);

        find.Detach();
        find.NextCommand.Execute(null);

        Assert.Equal("bell", find.Query);
        Assert.Equal("", find.Status);
        Assert.Same(HighlightSet.None, find.Highlights);
        Assert.Single(_went);
    }

    [Fact]
    public async Task A_page_the_Ask_panel_found_opens_at_the_phrase_on_it()
    {
        var find = Open();

        await find.ShowOnPageAsync(4, " the bell ");

        Assert.Equal("the bell", find.Query);
        Assert.Equal("2 of 3", find.Status);
        Assert.Equal([At(4, 500)], _went);
    }

    [Fact]
    public async Task A_page_where_Find_cant_see_the_phrase_opens_with_the_books_matches_marked()
    {
        var find = Open();

        await find.ShowOnPageAsync(6, "the bell");

        Assert.Equal("", find.Status);
        Assert.Equal(3, find.Highlights.Items.Count);
        Assert.Equal(-1, find.Highlights.Current);
        Assert.Equal([At(6)], _went);
    }
}
