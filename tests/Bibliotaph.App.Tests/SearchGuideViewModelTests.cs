using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.App.Tests;

/// <summary>
/// The field guide under the search box, as far as it goes without a library: field names, and a free-text field's
/// example. The values after a closed field's colon come from the library, so they aren't asked for here.
/// </summary>
public class SearchGuideViewModelTests
{
    sealed class NoCatalog : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => throw new NotSupportedException("The guide reads the library only for a field's values.");
    }

    readonly SearchGuideViewModel _guide = new(new LibraryQueries(IndexDatabase.InMemory("search-guide-tests")), new LibraryStore(new NoCatalog()),
        NullLogger<SearchGuideViewModel>.Instance);

    readonly List<GuideEdit> _edits = [];

    public SearchGuideViewModelTests() => _guide.Edited += (_, edit) => _edits.Add(edit);

    IEnumerable<string> Names => _guide.Suggestions.Cast<FieldSuggestionViewModel>().Select(f => f.Name);

    [Fact]
    public void Focusing_the_empty_box_lists_every_field()
    {
        _guide.Focused("", 0);

        Assert.True(_guide.IsOpen);
        Assert.Equal("Search by field", _guide.Heading);
        Assert.Equal(SearchFields.All.Select(f => f.Prefix), Names);
    }

    [Fact]
    public void Focusing_a_box_that_holds_a_search_leaves_the_guide_closed()
    {
        _guide.Focused("dragon", 6);

        Assert.False(_guide.IsOpen);
    }

    [Fact]
    public void Typing_the_start_of_a_field_name_narrows_the_list_to_the_fields_it_starts()
    {
        _guide.TextChanged("dragon fo", 9);

        Assert.True(_guide.IsOpen);
        Assert.Equal(["format:", "folder:"], Names);
    }

    [Fact]
    public void Emptying_the_box_does_not_open_the_guide_by_itself()
    {
        _guide.TextChanged("", 0);

        Assert.False(_guide.IsOpen);
    }

    [Fact]
    public void The_arrow_keys_wrap_round_the_list_and_say_which_row_they_are_on()
    {
        _guide.TextChanged("dragon fo", 9);

        Assert.True(_guide.Move(-1));
        var folder = _guide.Highlighted;
        Assert.True(_guide.Move(1));
        var format = _guide.Highlighted;

        Assert.Equal("folder:", ((FieldSuggestionViewModel)folder!).Name);
        Assert.Equal("format:", ((FieldSuggestionViewModel)format!).Name);
        Assert.True(format.IsHighlighted);
        Assert.False(folder.IsHighlighted);
        Assert.Equal(format.Spoken, _guide.Announcement);
    }

    [Fact]
    public void The_arrow_keys_do_nothing_while_the_guide_is_closed()
    {
        Assert.False(_guide.Move(1));
        Assert.False(_guide.PickHighlighted());
    }

    [Fact]
    public void Picking_a_field_puts_its_name_and_colon_in_place_of_the_word_typed()
    {
        _guide.TextChanged("dragon sy", 9);
        _guide.Move(1);

        Assert.True(_guide.PickHighlighted());

        Assert.Equal([new GuideEdit("dragon system:", 14)], _edits);
    }

    [Fact]
    public void Enter_with_no_row_highlighted_picks_nothing()
    {
        _guide.TextChanged("dragon sy", 9);

        Assert.False(_guide.PickHighlighted());
        Assert.Empty(_edits);
    }

    [Fact]
    public void Ctrl_Space_after_a_word_offers_every_field_to_add_after_it()
    {
        _guide.Request("dragon", 6);
        _guide.PickCommand.Execute(_guide.Suggestions[0]);

        Assert.Equal(SearchFields.All.Count, _guide.Suggestions.Count);
        Assert.Equal([new GuideEdit("dragon title:", 13)], _edits);
    }

    [Fact]
    public void After_a_free_text_field_colon_the_guide_shows_the_field_example_instead_of_rows()
    {
        _guide.TextChanged("title:", 6);

        Assert.True(_guide.IsOpen);
        Assert.Empty(_guide.Suggestions);
        Assert.Equal("title:dragon", _guide.Hint?.Example);
    }

    [Fact]
    public void Escape_closes_the_guide_and_forgets_the_highlight()
    {
        _guide.TextChanged("dragon fo", 9);
        _guide.Move(1);

        _guide.Close();

        Assert.False(_guide.IsOpen);
        Assert.Null(_guide.Highlighted);
        Assert.Equal("", _guide.Announcement);
    }
}
