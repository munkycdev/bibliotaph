namespace Bibliotaph.Core.Tests;

/// <summary>What dropping cards on a card, a book, a pin or "Not with a book" does in Organize mode.</summary>
public class OrganizeRulesTests
{
    static readonly OrganizeCard Book = new(1, "Drowned Abbey", "pdf");
    static readonly OrganizeCard OtherBook = new(2, "Salt Mine", "pdf");
    static readonly OrganizeCard Map = new(3, "harbor", "png", Loose: true);
    static readonly OrganizeCard Tower = new(4, "tower", "png", Loose: true);
    static readonly OrganizeCard Pack = new(5, "Abbey maps", "png", StandsForPack: true, Loose: true);
    static readonly OrganizeCard Handout = new(6, "Letter", "pdf", Loose: true);

    [Fact]
    public void Maps_dropped_on_a_book_go_with_it()
    {
        var plan = OrganizeRules.OnCard([Map, Tower], Book);

        Assert.Equal(new DropPlan(DropAction.GoesWith, "Goes with “Drowned Abbey”"), plan);
    }

    [Fact]
    public void A_book_dropped_on_a_book_asks_whether_it_goes_with_it_or_is_the_same_book()
    {
        Assert.Equal(DropAction.GoesWithOrSameBook, OrganizeRules.OnCard([OtherBook], Book).Action);
        // Something typed as a handout only ever goes with a book.
        Assert.Equal(DropAction.GoesWith, OrganizeRules.OnCard([Handout], Book).Action);
    }

    [Fact]
    public void A_card_that_cannot_go_with_a_book_is_offered_as_the_same_book_or_refused()
    {
        var elsewhere = new OrganizeCard(7, "Print copy", "elsewhere", Elsewhere: true);
        var withMaps = OtherBook with { Companions = 2 };

        Assert.Equal(DropAction.SameBook, OrganizeRules.OnCard([elsewhere], Book).Action);
        // With a pack among them they can't be one book either.
        var refused = OrganizeRules.OnCard([withMaps, Pack], Book);
        Assert.Equal((false, "Other cards go with “Salt Mine”, so it can't go with a book itself."), (refused.CanDrop, refused.Label));
    }

    [Fact]
    public void Pictures_join_a_pack_or_pack_together_with_a_picture()
    {
        Assert.Equal(new DropPlan(DropAction.JoinPack, "Join “Abbey maps”"), OrganizeRules.OnCard([Map], Pack));
        Assert.Equal(DropAction.PackTogether, OrganizeRules.OnCard([Tower], Map).Action);
        // A book isn't packed with a picture, and a picture isn't a book to go with.
        var onPicture = OrganizeRules.OnCard([Book], Map);
        Assert.Equal((false, "A picture can't be the book. Drop them on the book itself."), (onPicture.CanDrop, onPicture.Label));
    }

    [Fact]
    public void A_card_dropped_on_itself_or_on_a_card_that_goes_with_a_book_does_nothing()
    {
        var linked = Tower with { Format = "pdf", GoesWithBook = true };

        Assert.False(OrganizeRules.OnCard([Map, Book], Book).CanDrop);
        Assert.Equal("“tower” goes with a book itself.", OrganizeRules.OnCard([Map], linked).Label);
    }

    [Fact]
    public void The_books_panel_only_links_and_refuses_a_book_with_maps_of_its_own()
    {
        var withMaps = OtherBook with { Companions = 2 };

        Assert.Equal(DropAction.GoesWith, OrganizeRules.OnBook([Map], Book.EntryId, Book.Title).Action);
        Assert.False(OrganizeRules.OnBook([withMaps], Book.EntryId, Book.Title).CanDrop);
        Assert.False(OrganizeRules.OnBook([Book], Book.EntryId, Book.Title).CanDrop);
    }

    [Fact]
    public void Not_with_a_book_takes_only_cards_that_go_with_one()
    {
        Assert.False(OrganizeRules.NotWithABook([Map]).CanDrop);
        Assert.Equal(DropAction.NotWithABook, OrganizeRules.NotWithABook([Map, Tower with { GoesWithBook = true }]).Action);
    }

    [Fact]
    public void Same_book_and_pack_together_fit_the_cards_they_did_in_select_mode()
    {
        Assert.True(OrganizeRules.CanJoin([Book, OtherBook]));
        Assert.False(OrganizeRules.CanJoin([Book, Pack]));
        Assert.True(OrganizeRules.CanPack([Map, Pack, Handout]));
        Assert.False(OrganizeRules.CanPack([Map]));
        Assert.Equal(("1 card", "3 cards"), (OrganizeRules.Dragging(1), OrganizeRules.Dragging(3)));
    }
}
