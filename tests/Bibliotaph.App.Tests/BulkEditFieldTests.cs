using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Metadata;
using static Bibliotaph.App.Tests.Values;

namespace Bibliotaph.App.Tests;

/// <summary>A field of the bulk editor across the ticked books: what they have now, the change typed, and the summary before it applies.</summary>
public class BulkEditFieldTests
{
    static readonly MetadataField GameSystem = MetadataFields.System;
    static readonly MetadataField Types = MetadataFields.Types;

    int _changes;

    BulkValueFieldViewModel ValueField(MetadataField field, params EffectiveField[] books) => new(field, books, Terms, () => _changes++);

    BulkValueFieldViewModel Systems(params string?[] books) =>
        ValueField(GameSystem, [.. books.Select(b => b is null ? Field(GameSystem) : Field(GameSystem, Value(GameSystem, b)))]);

    static EffectiveField Own(string value) => Field(GameSystem, Value(GameSystem, value, confirmed: true));

    [Fact]
    public void A_value_on_only_some_books_says_on_how_many()
    {
        Assert.Equal("D&D 5e on 2 of 3 books", Systems("dnd5e", null, "dnd5e").Current);
    }

    [Fact]
    public void Books_that_differ_show_each_value_with_its_count()
    {
        var field = Systems("pf2e", "dnd5e", null, "dnd5e");

        Assert.Equal("Mixed (2 values)", field.Current);
        Assert.Equal("D&D 5e 2 · Pathfinder 2e 1 · not set 1", field.Breakdown);
    }

    [Fact]
    public void Editing_starts_from_the_shared_value_but_from_nothing_when_books_differ()
    {
        var shared = Systems("dnd5e", "dnd5e");
        var mixed = Systems("dnd5e", "pf2e");

        shared.EditCommand.Execute(null);
        mixed.EditCommand.Execute(null);

        Assert.Equal("D&D 5e", shared.EditText);
        Assert.Equal("", mixed.EditText);
    }

    [Fact]
    public void A_term_typed_by_another_of_its_names_is_set_on_every_book()
    {
        var field = Systems("dnd5e", null);
        field.EditCommand.Execute(null);
        field.EditText = "pf2e";

        field.SaveCommand.Execute(null);

        Assert.False(field.IsEditing);
        Assert.Equal("Will be Pathfinder 2e on every book", field.PendingText);
        Assert.Equal([new BulkChange(GameSystem, BulkAction.Set, "pf2e")], field.Changes());
        Assert.True(_changes > 0);
    }

    [Fact]
    public void A_name_the_vocabulary_does_not_know_is_set_as_a_new_term()
    {
        var field = Systems("dnd5e", null);
        field.EditCommand.Execute(null);
        field.EditText = "Mothership";

        field.SaveCommand.Execute(null);

        Assert.Equal("Will be Mothership (new system) on every book", field.PendingText);
        Assert.Equal([new BulkSummaryLine("Game system", "Set to Mothership (new system) on 2 books.", null)], field.Describe());
    }

    [Fact]
    public void Saving_an_empty_value_asks_for_one_and_changes_nothing()
    {
        var field = Systems("dnd5e");
        field.EditCommand.Execute(null);
        field.EditText = "  ";

        field.SaveCommand.Execute(null);

        Assert.True(field.IsEditing);
        Assert.Equal("Type a value, or Cancel to leave the game system as it is.", field.Problem);
        Assert.False(field.HasChange);
    }

    [Fact]
    public void A_value_that_does_not_read_is_refused_with_how_to_write_it()
    {
        var field = ValueField(MetadataFields.Year, Field(MetadataFields.Year));
        field.EditCommand.Execute(null);
        field.EditText = "next year";

        field.SaveCommand.Execute(null);

        Assert.Equal("A year looks like 2019.", field.Problem);
        Assert.Empty(field.Changes());
    }

    [Fact]
    public void Levels_are_stored_as_the_catalog_stores_them_and_shown_as_they_read()
    {
        var field = ValueField(MetadataFields.Levels, Field(MetadataFields.Levels));
        field.EditCommand.Execute(null);
        field.EditText = "1 to 5";

        field.SaveCommand.Execute(null);

        Assert.Equal("Will be Levels 1–5 on every book", field.PendingText);
        Assert.Equal([new BulkChange(MetadataFields.Levels, BulkAction.Set, "1-5")], field.Changes());
    }

    [Fact]
    public void The_summary_warns_when_a_value_replaces_one_the_user_set()
    {
        var field = ValueField(GameSystem, Own("dnd5e"), Field(GameSystem, Value(GameSystem, "dnd5e")), Field(GameSystem));
        field.EditCommand.Execute(null);
        field.EditText = "Pathfinder 2e";

        field.SaveCommand.Execute(null);

        Assert.Equal([new BulkSummaryLine("Game system", "Set to Pathfinder 2e on 3 books.", "Replaces your own game system on 1 book.")], field.Describe());
    }

    [Fact]
    public void Setting_the_value_every_book_already_has_as_its_own_changes_nothing()
    {
        var field = ValueField(GameSystem, Own("dnd5e"), Own("dnd5e"));
        field.EditCommand.Execute(null);
        field.EditText = "D&D 5e";

        field.SaveCommand.Execute(null);

        var line = Assert.Single(field.Describe());
        Assert.Equal("Already D&D 5e on every book; nothing changes.", line.Text);
        Assert.False(line.HasWarning);
    }

    [Fact]
    public void Back_to_suggestions_is_offered_only_where_the_user_set_a_value_and_warns_it_forgets_them()
    {
        var suggested = Systems("dnd5e", "pf2e");
        var field = ValueField(GameSystem, Own("dnd5e"), Field(GameSystem, Value(GameSystem, "pf2e")));

        field.ResetCommand.Execute(null);

        Assert.False(suggested.ShowReset);
        Assert.Equal("Back to suggestions on every book", field.PendingText);
        Assert.Equal([new BulkChange(GameSystem, BulkAction.Reset)], field.Changes());
        Assert.Equal([new BulkSummaryLine("Game system", "Back to suggestions on 1 book.", "Forgets your own game system on 1 book.")], field.Describe());
    }

    [Fact]
    public void Undo_leaves_the_field_as_it_is()
    {
        var field = ValueField(GameSystem, Own("dnd5e"));
        field.ResetCommand.Execute(null);

        field.UndoCommand.Execute(null);

        Assert.False(field.HasChange);
        Assert.Empty(field.Changes());
        Assert.Empty(field.Describe());
        Assert.True(field.ShowReset);
    }

    // Three books: the first has Adventure as the user's own, the second Adventure and Map as suggestions, the third no type.
    BulkChipsFieldViewModel Chips() => new(Types,
        [Field(Types, Value(Types, "adventure", confirmed: true)), Field(Types, Value(Types, "adventure"), Value(Types, "map")), Field(Types)],
        Terms, () => _changes++);

    static BulkChipViewModel Chip(BulkChipsFieldViewModel field, string text) => field.Chips.Single(c => c.Text == text);

    [Fact]
    public void Each_value_is_a_chip_saying_on_how_many_books_it_is_most_first()
    {
        var field = Chips();

        Assert.Equal(["Adventure 2 of 3", "Map 1 of 3"], field.Chips.Select(c => $"{c.Text} {c.CountText}"));
    }

    [Fact]
    public void Clicking_a_value_on_some_books_puts_it_on_every_book_and_clicking_again_takes_that_back()
    {
        var field = Chips();
        var map = Chip(field, "Map");

        map.ToggleAllCommand.Execute(null);

        Assert.Equal("3 of 3", map.CountText);
        Assert.Equal([new BulkChange(Types, BulkAction.Add, "map")], field.Changes());
        Assert.Equal([new BulkSummaryLine("Type", "Add Map to 3 books.", null)], field.Describe());
        Assert.True(_changes > 0);

        map.ToggleAllCommand.Execute(null);

        Assert.Equal("1 of 3", map.CountText);
        Assert.False(field.HasChange);
    }

    [Fact]
    public void Removing_a_value_takes_it_off_every_book_and_warns_where_the_user_set_it()
    {
        var field = Chips();
        var adventure = Chip(field, "Adventure");

        adventure.RemoveCommand.Execute(null);

        Assert.Equal("0 of 3", adventure.CountText);
        Assert.Equal("Keep Adventure as it is", adventure.RemoveName);
        Assert.Equal([new BulkSummaryLine("Type", "Remove Adventure from 2 books.", "Takes away your own Adventure on 1 book.")], field.Describe());
    }

    [Fact]
    public void Typed_values_join_the_chips_a_known_one_as_its_term_and_a_new_one_marked_new()
    {
        var field = Chips();
        field.AddText = "map, Hexcrawl";

        field.AddTypedCommand.Execute(null);

        Assert.Equal("", field.AddText);
        Assert.Equal(["Adventure", "Map", "Hexcrawl"], field.Chips.Select(c => c.Text));
        Assert.True(Chip(field, "Map").IsAdding);
        var hexcrawl = Chip(field, "Hexcrawl");
        Assert.True(hexcrawl.IsTyped);
        Assert.Equal("new type", hexcrawl.NewNote);
        Assert.Equal(
            [new BulkSummaryLine("Type", "Add Map to 3 books.", null), new BulkSummaryLine("Type", "Add Hexcrawl (new type) to 3 books.", null)],
            field.Describe());
    }

    [Fact]
    public void Removing_a_typed_value_takes_its_chip_away()
    {
        var field = Chips();
        field.AddText = "Hexcrawl";
        field.AddTypedCommand.Execute(null);

        Chip(field, "Hexcrawl").RemoveCommand.Execute(null);

        Assert.Equal(["Adventure", "Map"], field.Chips.Select(c => c.Text));
        Assert.False(field.HasChange);
    }

    [Fact]
    public void A_typed_value_with_no_letters_is_refused_and_nothing_is_added()
    {
        var field = Chips();
        field.AddText = "Hexcrawl, !!!";

        field.AddTypedCommand.Execute(null);

        Assert.Equal("Type needs some letters or numbers.", field.Problem);
        Assert.Equal(2, field.Chips.Count);
        Assert.Equal("Hexcrawl, !!!", field.AddText);
    }

    [Fact]
    public void A_value_picked_from_the_list_is_added_straight_away()
    {
        var field = Chips();

        field.PickedChoice = "Map";

        Assert.True(Chip(field, "Map").IsAdding);
        Assert.Null(field.PickedChoice);
    }
}
