using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

public class DetailSectionsTests
{
    [Fact]
    public void Nothing_stored_folds_publication_and_the_file_sections()
    {
        Assert.Equal([DetailSections.Publication, DetailSections.File, DetailSections.Copies, DetailSections.Read],
            DetailSections.All.Where(DetailSections.ReadFolded(null).Contains));
    }

    [Fact]
    public void Everything_opened_is_stored_as_nothing_folded()
    {
        var stored = DetailSections.Write([]);
        Assert.Equal("v2", stored);
        Assert.Empty(DetailSections.ReadFolded(stored));
    }

    [Fact]
    public void Folded_sections_are_stored_in_page_order_and_read_back()
    {
        var stored = DetailSections.Write([DetailSections.Read, DetailSections.Collections]);
        Assert.Equal("v2,collections,read", stored);
        Assert.Equal(new HashSet<string> { DetailSections.Collections, DetailSections.Read }, DetailSections.ReadFolded(stored));
    }

    [Fact]
    public void A_section_no_longer_shown_is_ignored() =>
        Assert.Equal(new HashSet<string> { DetailSections.Glance }, DetailSections.ReadFolded("v2,glance,gone"));

    [Fact]
    public void Folds_stored_before_the_regrouping_carry_over()
    {
        // The old default: the file sections folded, the rest open.
        Assert.Equal(new HashSet<string> { DetailSections.File, DetailSections.Copies, DetailSections.Read, DetailSections.Publication },
            DetailSections.ReadFolded("file,where,copies,indexing"));
        Assert.Equal(new HashSet<string> { DetailSections.Inside, DetailSections.Glance, DetailSections.Collections, DetailSections.Publication },
            DetailSections.ReadFolded("found,collections,details"));
    }

    [Fact]
    public void The_file_section_stays_open_when_either_of_the_two_it_joins_was_open()
    {
        Assert.DoesNotContain(DetailSections.File, DetailSections.ReadFolded("file"));
        Assert.DoesNotContain(DetailSections.File, DetailSections.ReadFolded("where"));
        Assert.DoesNotContain(DetailSections.File, DetailSections.ReadFolded(""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_field_has_one_place_in_the_details(bool elsewhere)
    {
        var order = DetailSections.FieldOrder(elsewhere);
        Assert.Equal(MetadataFields.All.Count, order.Count);
        Assert.Equal(MetadataFields.All.ToHashSet(), order.ToHashSet());
    }

    [Fact]
    public void Fields_group_by_what_a_game_master_asks_first()
    {
        Assert.Equal(DetailSections.Glance, DetailSections.FieldSection(MetadataFields.Levels, elsewhere: false));
        Assert.Equal(DetailSections.Glance, DetailSections.FieldSection(MetadataFields.Tags, elsewhere: false));
        Assert.Equal(DetailSections.Inside, DetailSections.FieldSection(MetadataFields.Monsters, elsewhere: false));
        Assert.Equal(DetailSections.Publication, DetailSections.FieldSection(MetadataFields.Title, elsewhere: false));
        Assert.Equal(DetailSections.Publication, DetailSections.FieldSection(MetadataFields.AlsoOwn, elsewhere: false));
    }

    [Fact]
    public void A_book_owned_elsewhere_says_first_where_it_is_owned()
    {
        Assert.Equal(MetadataFields.AlsoOwn, DetailSections.FieldOrder(elsewhere: true)[0]);
        Assert.Equal(DetailSections.Glance, DetailSections.FieldSection(MetadataFields.AlsoOwn, elsewhere: true));
    }

    [Fact]
    public void Fields_sort_into_the_details_order()
    {
        MetadataField[] fields = [MetadataFields.Title, MetadataFields.Monsters, MetadataFields.System];
        Assert.Equal([MetadataFields.System, MetadataFields.Monsters, MetadataFields.Title], DetailSections.InFieldOrder(fields, f => f, elsewhere: false));
    }

    [Fact]
    public void The_first_chapters_shown_are_those_the_ai_found_something_in()
    {
        string[] chapters = ["intro", "a!", "b", "c!", "d!", "e!"];
        Assert.Equal(["a!", "c!", "d!"], DetailSections.FirstChapters(chapters, c => c.EndsWith('!')));
        Assert.Equal(["intro", "a!", "b"], DetailSections.FirstChapters(chapters, _ => false));
    }
}
