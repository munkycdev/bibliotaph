using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>Easier corrections (scenario review, row 8): which books look like a corrected one, what repeats the correction, and when a folder rule is due.</summary>
public class CorrectionsTests
{
    static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    long _id;

    MetadataClaim Claim(MetadataField field, string value, AssertionOrigin origin, AssertionState state = AssertionState.Provisional, int minutes = 0) =>
        new(++_id, field.Key, value, MetadataValues.Normalize(field, value), origin, state, T0.AddMinutes(minutes),
            DecidedUtc: state == AssertionState.Confirmed ? T0.AddMinutes(minutes) : null);

    static EffectiveField Field(MetadataField field, params MetadataClaim[] claims) => EffectiveMetadata.Compute(claims, [])[field];

    [Fact]
    public void A_value_typed_over_a_guess_is_repeated_as_that_value()
    {
        var before = Field(MetadataFields.System, Claim(MetadataFields.System, "dnd", AssertionOrigin.Ai));
        var after = Field(MetadataFields.System, Claim(MetadataFields.System, "dnd", AssertionOrigin.Ai), Claim(MetadataFields.System, "pathfinder", AssertionOrigin.User, AssertionState.Confirmed));

        Assert.Equal([new BulkChange(MetadataFields.System, BulkAction.Set, "pathfinder")], LookAlikes.Changes(before, after));
    }

    [Fact]
    public void A_rejected_guess_is_repeated_as_a_rejection_even_with_the_next_suggestion_showing()
    {
        var before = Field(MetadataFields.System, Claim(MetadataFields.System, "dnd", AssertionOrigin.Ai), Claim(MetadataFields.System, "coc", AssertionOrigin.Folder));
        var after = EffectiveMetadata.Compute(
            [Claim(MetadataFields.System, "dnd", AssertionOrigin.Ai), Claim(MetadataFields.System, "coc", AssertionOrigin.Folder)], [("system", "dnd")])[MetadataFields.System];

        Assert.Equal("coc", after.First!.Value);
        Assert.Equal([new BulkChange(MetadataFields.System, BulkAction.Remove, "dnd")], LookAlikes.Changes(before, after));
    }

    [Fact]
    public void A_list_is_repeated_as_what_was_taken_out_and_what_was_put_in()
    {
        var before = Field(MetadataFields.Types, Claim(MetadataFields.Types, "adventure", AssertionOrigin.Ai), Claim(MetadataFields.Types, "bestiary", AssertionOrigin.Ai));
        var after = Field(MetadataFields.Types,
            Claim(MetadataFields.Types, "adventure", AssertionOrigin.User, AssertionState.Confirmed), Claim(MetadataFields.Types, "setting", AssertionOrigin.User, AssertionState.Confirmed));

        Assert.Equal(
            [new BulkChange(MetadataFields.Types, BulkAction.Remove, "bestiary"), new BulkChange(MetadataFields.Types, BulkAction.Add, "setting")],
            LookAlikes.Changes(before, after));
    }

    [Fact]
    public void Keeping_a_value_or_correcting_a_books_own_title_year_or_store_is_not_offered()
    {
        var guess = Field(MetadataFields.System, Claim(MetadataFields.System, "dnd", AssertionOrigin.Ai));
        var kept = Field(MetadataFields.System, Claim(MetadataFields.System, "dnd", AssertionOrigin.Ai, AssertionState.Confirmed));
        Assert.Empty(LookAlikes.Changes(guess, kept));

        var title = Field(MetadataFields.Title, Claim(MetadataFields.Title, "Primer", AssertionOrigin.Filename));
        var typed = Field(MetadataFields.Title, Claim(MetadataFields.Title, "Court of Thorns Primer", AssertionOrigin.User, AssertionState.Confirmed));
        Assert.Empty(LookAlikes.Changes(title, typed));
        Assert.Empty(LookAlikes.Changes(Field(MetadataFields.Year), Field(MetadataFields.Year, Claim(MetadataFields.Year, "2019", AssertionOrigin.User, AssertionState.Confirmed))));
    }

    [Fact]
    public void A_look_alike_shows_exactly_what_the_corrected_book_did_and_the_user_hasnt_settled_it()
    {
        var before = Field(MetadataFields.Types, Claim(MetadataFields.Types, "adventure", AssertionOrigin.Ai), Claim(MetadataFields.Types, "bestiary", AssertionOrigin.Ai));
        Assert.Equal(2, before.Values.Count);

        Assert.True(LookAlikes.IsLookAlike(
            Field(MetadataFields.Types, Claim(MetadataFields.Types, "bestiary", AssertionOrigin.Ai), Claim(MetadataFields.Types, "adventure", AssertionOrigin.Ai, minutes: 5)), before));
        // One value more or less isn't the same guess.
        Assert.False(LookAlikes.IsLookAlike(Field(MetadataFields.Types, Claim(MetadataFields.Types, "adventure", AssertionOrigin.Ai)), before));
        // The same values, but the user kept one of them.
        Assert.False(LookAlikes.IsLookAlike(Field(MetadataFields.Types,
            Claim(MetadataFields.Types, "bestiary", AssertionOrigin.Ai, AssertionState.Confirmed), Claim(MetadataFields.Types, "adventure", AssertionOrigin.Ai)), before));
        // Filling a gap: books with nothing there look alike, books with anything don't.
        var empty = Field(MetadataFields.Players);
        Assert.True(LookAlikes.IsLookAlike(Field(MetadataFields.Players), empty));
        Assert.False(LookAlikes.IsLookAlike(Field(MetadataFields.Players, Claim(MetadataFields.Players, "4", AssertionOrigin.Ai)), empty));
    }

    [Fact]
    public void A_folder_rule_is_due_once_three_books_have_the_same_value_the_user_decided()
    {
        static EntryMetadata Book(long id, params MetadataClaim[] claims) => new(new EntryId(id), claims, []);
        var books = new List<EntryMetadata>
        {
            Book(1, Claim(MetadataFields.System, "pathfinder", AssertionOrigin.User, AssertionState.Confirmed)),
            Book(2, Claim(MetadataFields.System, "pathfinder", AssertionOrigin.User, AssertionState.Confirmed)),
            // A guess nobody confirmed, and a rule's own suggestion, don't count.
            Book(3, Claim(MetadataFields.System, "pathfinder", AssertionOrigin.Ai)),
            Book(4, Claim(MetadataFields.System, "pathfinder", AssertionOrigin.FolderRule, AssertionState.Confirmed)),
        };
        Assert.Empty(FolderRules.RepeatedValues(books, MetadataFields.System));

        // A suggestion the user kept counts, as a typed value that matches one is stored that way.
        books.Add(Book(5, Claim(MetadataFields.System, "pathfinder", AssertionOrigin.Ai, AssertionState.Confirmed)));
        Assert.Equal([("pathfinder", 3)], FolderRules.RepeatedValues(books, MetadataFields.System));
        Assert.Empty(FolderRules.RepeatedValues(books, MetadataFields.Types));
    }

    [Fact]
    public void A_value_the_user_rejected_again_does_not_count_toward_a_rule()
    {
        EntryMetadata[] books =
        [
            new(new EntryId(1), [Claim(MetadataFields.Types, "adventure", AssertionOrigin.User, AssertionState.Confirmed)], []),
            new(new EntryId(2), [Claim(MetadataFields.Types, "adventure", AssertionOrigin.User, AssertionState.Confirmed)], []),
            new(new EntryId(3), [Claim(MetadataFields.Types, "adventure", AssertionOrigin.User, AssertionState.Confirmed)], [("type", "adventure")]),
        ];

        Assert.Empty(FolderRules.RepeatedValues(books, MetadataFields.Types));
        Assert.Equal(2, Assert.Single(FolderRules.RepeatedValues(books, MetadataFields.Types, threshold: 2)).Books);
    }
}
