using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Core.Tests;

/// <summary>The one list of search fields, and the field guide's logic: what it offers at the cursor and what a pick does.</summary>
public class SearchGuideTests
{
    [Fact]
    public void Every_field_the_parser_knows_is_in_the_list_once()
    {
        Assert.Equal(Enum.GetValues<SearchField>().Order(), SearchFields.All.Select(f => f.Field).Order());
        Assert.All(Enum.GetValues<SearchField>(), field => Assert.Equal(field, SearchFields.Get(field).Field));
        var names = SearchFields.All.SelectMany(f => f.Names).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_name_and_alias_in_the_list_parses_as_its_field()
    {
        foreach (var field in SearchFields.All)
        {
            var value = field.Example[field.Prefix.Length..];
            foreach (var name in field.Names)
            {
                var query = SearchQuery.Parse($"{name}:{value}");
                Assert.Empty(query.Issues);
                Assert.Equal(field.Field, Assert.IsType<FieldNode>(query.Root).Field);
                Assert.Equal(field.Field, Assert.IsType<FieldNode>(SearchQuery.Parse($"{name.ToUpperInvariant()}:{value}").Root).Field);
            }
        }
    }

    [Fact]
    public void Every_example_is_a_search_that_parses_cleanly()
    {
        foreach (var field in SearchFields.All)
        {
            Assert.StartsWith(field.Prefix, field.Example, StringComparison.Ordinal);
            var query = SearchQuery.Parse(field.Example);
            Assert.Empty(query.Issues);
            Assert.Equal(field.Field, Assert.IsType<FieldNode>(query.Root).Field);
        }
    }

    [Theory]
    [InlineData("year:1990")]
    [InlineData("pages:12")]
    [InlineData("http://example.com")]
    public void A_name_not_in_the_list_is_an_ordinary_word(string text) =>
        Assert.IsType<TermNode>(SearchQuery.Parse(text).Root);

    [Fact]
    public void Fields_still_to_come_are_not_in_the_list_but_say_so()
    {
        foreach (var name in SearchFields.Coming)
        {
            Assert.Null(SearchFields.Find(name));
            Assert.Contains("isn't available yet", Assert.Single(SearchQuery.Parse($"{name}:short").Issues).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_missing_value_is_explained_with_the_lists_example()
    {
        foreach (var field in SearchFields.All)
            Assert.Contains($"like {field.Example}.", Assert.Single(SearchQuery.Parse(field.Prefix).Issues).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_fields_are_the_vocabulary_ones_and_format()
    {
        Assert.Equal(["system", "edition", "type", "setting", "theme", "environment", "own", "format"], SearchFields.All.Where(f => f.ListsValues).Select(f => f.Name));
        // A Term field's values are its catalog field's vocabulary, which is also its entry_facet field.
        Assert.All(SearchFields.All.Where(f => f.Kind == SearchFieldKind.Term),
            f => Assert.True(f.Metadata is { Kind: FieldKind.Term, Vocabulary: not null } m && m.Vocabulary == m.Key, f.Name));
    }

    // ---- At the cursor -------------------------------------------------------------------------------------------

    /// <summary>The field names on offer, or null when nothing is. A | in <paramref name="text"/> marks the cursor.</summary>
    static string[]? Offered(string text, bool requested = false)
    {
        var at = Guide(text, requested);
        return at is null ? null : at.Mode == GuideMode.Fields ? [.. at.Fields.Select(f => f.Name)] : [$"{at.Mode}:{at.Field!.Name}:{at.Typed}"];
    }

    static GuideContext? Guide(string text, bool requested = false) => SearchGuide.At(text.Replace("|", "", StringComparison.Ordinal), text.IndexOf('|', StringComparison.Ordinal), requested);

    [Theory]
    [InlineData("|")]
    [InlineData("  |")]
    public void An_empty_box_offers_every_field(string text) =>
        Assert.Equal(SearchFields.All.Select(f => f.Name), Offered(text)!);

    [Theory]
    [InlineData("sy|", new[] { "system" })]
    [InlineData("S|", new[] { "system", "series", "setting" })]
    [InlineData("t|", new[] { "title", "type", "theme", "tag" })]
    [InlineData("dragon ty|", new[] { "type" })]
    [InlineData("-sy|", new[] { "system" })]
    [InlineData("(sy|", new[] { "system" })]
    [InlineData("env|", new[] { "environment" })]
    [InlineData("authors|", new[] { "author" })]
    [InlineData("level|", new[] { "level" })]
    public void The_word_at_the_cursor_narrows_the_names(string text, string[] expected) =>
        Assert.Equal(expected, Offered(text));

    [Theory]
    [InlineData("dragon|")]
    [InlineData("dragon |")]
    [InlineData("sy|stem")]
    [InlineData("\"sy|")]
    [InlineData("length:|")]
    [InlineData("system:\"D&D|")]
    public void Nothing_is_offered_where_no_field_fits(string text) => Assert.Null(Offered(text));

    [Fact]
    public void A_match_replaces_the_whole_word_and_keeps_a_minus()
    {
        var at = Guide("dragon -sy|")!;
        Assert.Equal((8, 10, "sy"), (at.Start, at.End, at.Typed));
        Assert.Equal(new GuideEdit("dragon -system:", 15), SearchGuide.InsertField("dragon -sy", at, SearchFields.System));
    }

    [Fact]
    public void Asking_for_the_guide_offers_every_field_anywhere()
    {
        Assert.Equal(SearchFields.All.Select(f => f.Name), Offered("dragon |", requested: true)!);
        var after = Guide("drag|on", requested: true)!;
        Assert.Equal(SearchFields.All.Select(f => f.Name), after.Fields.Select(f => f.Name));
        Assert.Equal(new GuideEdit("dragon type:", 12), SearchGuide.InsertField("dragon", after, SearchFields.Type));
        Assert.Equal(["system"], Offered("sy|", requested: true)!);
    }

    [Theory]
    [InlineData("system:|", "Values:system:")]
    [InlineData("system:dn|", "Values:system:dn")]
    [InlineData("dragon -TYPE:adv|", "Values:type:adv")]
    [InlineData("format:|", "Values:format:")]
    [InlineData("env:ur|", "Values:environment:ur")]
    [InlineData("title:|", "Example:title:")]
    [InlineData("publisher:kob|", "Example:publisher:kob")]
    [InlineData("level:|", "Example:level:")]
    public void After_a_colon_the_field_decides_what_is_offered(string text, string expected) =>
        Assert.Equal([expected], Offered(text)!);

    [Fact]
    public void Picking_a_field_puts_the_cursor_after_its_colon_where_its_values_are_offered()
    {
        var edit = SearchGuide.InsertField("", Guide("|")!, SearchFields.System);
        Assert.Equal(new GuideEdit("system:", 7), edit);
        Assert.Equal(GuideMode.Values, SearchGuide.At(edit.Text, edit.Caret)!.Mode);

        Assert.Equal(new GuideEdit("goblins system: lair", 15), SearchGuide.InsertField("goblins sy lair", Guide("goblins sy| lair")!, SearchFields.System));
    }

    [Theory]
    [InlineData("system:d|", "dnd", "system:dnd ", 11)]
    [InlineData("system:d|n", "dnd", "system:dnd ", 11)]
    [InlineData("system:| type:map", "dnd", "system:dnd type:map", 11)]
    [InlineData("(system:|)", "dnd", "(system:dnd)", 11)]
    [InlineData("setting:|", "lost lands", "setting:\"lost lands\" ", 21)]
    public void Picking_a_value_replaces_what_was_typed_and_moves_past_a_space(string text, string value, string expected, int caret) =>
        Assert.Equal(new GuideEdit(expected, caret), SearchGuide.InsertValue(text.Replace("|", "", StringComparison.Ordinal), Guide(text)!, value));

    [Fact]
    public void A_system_and_a_type_search_can_be_built_by_picking_alone()
    {
        var text = "";
        var edit = SearchGuide.InsertField(text, SearchGuide.At(text, 0)!, SearchFields.System);
        edit = SearchGuide.InsertValue(edit.Text, SearchGuide.At(edit.Text, edit.Caret)!, "dnd");
        Assert.Null(SearchGuide.At(edit.Text, edit.Caret)); // a finished value: the guide steps aside
        edit = SearchGuide.InsertField(edit.Text, SearchGuide.At(edit.Text, edit.Caret, requested: true)!, SearchFields.Type);
        edit = SearchGuide.InsertValue(edit.Text, SearchGuide.At(edit.Text, edit.Caret)!, "adventure");

        Assert.Equal("system:dnd type:adventure ", edit.Text);
        Assert.Equal("AND(system:dnd, type:adventure)", SearchQuery.Parse(edit.Text).Root!.ToString());
    }

    // ---- Values ---------------------------------------------------------------------------------------------------

    static readonly GuideValue[] Systems =
        [new("dnd", "Dungeons & Dragons", 12), new("pathfinder", "Pathfinder", 4), new("call-of-cthulhu", "Call of Cthulhu", 2), new(SearchQuery.Unknown, "Unknown", 7)];

    [Theory]
    [InlineData("", new[] { "dnd", "pathfinder", "call-of-cthulhu", "unknown" })]
    [InlineData("d", new[] { "dnd" })]
    [InlineData("DUN", new[] { "dnd" })]
    [InlineData("cth", new[] { "call-of-cthulhu" })]
    [InlineData("call-of", new[] { "call-of-cthulhu" })]
    [InlineData("pa*", new[] { "pathfinder" })]
    [InlineData("unk", new[] { "unknown" })]
    [InlineData("zzz", new string[0])]
    public void Values_narrow_by_value_label_or_a_word_of_the_label(string typed, string[] expected) =>
        Assert.Equal(expected, SearchGuide.Values(Systems, typed).Select(v => v.Value));

    [Fact]
    public void Values_keep_the_most_common_first_order_they_came_in() =>
        Assert.Equal([12L, 4, 2, 7], SearchGuide.Values(Systems, "").Select(v => v.Count));

    [Fact]
    public void Formats_add_any_picture_when_there_are_both_kinds()
    {
        Assert.Equal([("pdf", 9L), ("png", 3L), ("jpg", 2L), ("image", 5L)],
            SearchGuide.FormatValues([new("pdf", "PDF", 9), new("png", "PNG", 3), new("jpg", "JPG", 2)]).Select(v => (v.Value, v.Count)));
        Assert.Equal(["pdf", "png"], SearchGuide.FormatValues([new("pdf", "PDF", 9), new("png", "PNG", 3)]).Select(v => v.Value));
        Assert.Equal(SearchFields.Format.Field, Assert.IsType<FieldNode>(SearchQuery.Parse("format:image").Root).Field);
    }

    [Theory]
    [InlineData("dnd", "dnd")]
    [InlineData("map-pack", "map-pack")]
    [InlineData("n/a", "n/a")]
    [InlineData("lost lands", "\"lost lands\"")]
    [InlineData("say \"hi\"", "\"say hi\"")]
    [InlineData("(x)", "\"(x)\"")]
    public void Values_are_quoted_only_when_they_need_it(string value, string expected) =>
        Assert.Equal(expected, SearchGuide.Quote(value));
}
