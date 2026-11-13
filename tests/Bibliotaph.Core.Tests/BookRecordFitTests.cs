using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

/// <summary>Telling which of the items a catalogue's search found by name is a book's, from what the book prints.</summary>
public sealed class BookRecordFitTests
{
    static readonly string[] Lines = ["Gull Rock Saga"];

    static CatalogueItem Item(string key, string name) => new("RPGGeek", key, name);

    [Theory]
    // Its file or its cover prints the item's own name whole.
    [InlineData("Gull Rock Saga: Book 2. Spells", "Gull Rock Saga - Book 2 Spells", null, null, NameFit.Named)]
    [InlineData("Gull Rock Saga: Book 2. Spells", "grs_02", "GULL ROCK SAGA\nBook 2: Spells", null, NameFit.Named)]
    // Printed whole only further in, where a book also names the others of its line.
    [InlineData("Gull Rock Saga: Book 2. Spells", "grs_02", "GULL ROCK SAGA", "Also available: Book 2. Spells", NameFit.Partly)]
    // Every word of it on the cover, but not together.
    [InlineData("Gull Rock Saga: Book 2. Spells", "grs_02", "SPELLS\nGULL ROCK SAGA\nBOOK 2", null, NameFit.Partly)]
    // A name of one word must be a line of the cover by itself: a blurb holds many a word.
    [InlineData("Gull Rock Saga: Bestiary", "grs_03", "Creatures and a bestiary of the isles", null, NameFit.None)]
    [InlineData("Gull Rock Saga: Bestiary", "grs_03", "GULL ROCK SAGA\nBESTIARY", null, NameFit.Named)]
    [InlineData("Gull Rock Saga: Book 2. Spells", "Sailors", "BOOK 1 SAILORS", null, NameFit.None)]
    // Named as its line is: every book of the line prints that.
    [InlineData("Gull Rock Saga", "Gull Rock Saga - Book 2 Spells", "GULL ROCK SAGA", null, NameFit.None)]
    public void A_name_fits_a_book_by_what_its_titles_cover_and_front_pages_print(string name, string title, string? cover, string? inside, NameFit fit) =>
        Assert.Equal(fit, BookRecords.Fit(name, Lines, [title], cover, [cover, inside]));

    [Fact]
    public void The_one_item_a_book_goes_by_the_name_of_is_taken_and_several_are_asked_about()
    {
        string?[] titles = ["Gull Rock Saga - Bestiary 2"];
        var one = BookRecords.Pick([Item("1", "Gull Rock Saga: Bestiary"), Item("2", "Gull Rock Saga: Bestiary 2"), Item("3", "Gull Rock Saga: Book of Maps")],
            Lines, titles, null, []);
        // Two lines' screens: neither is surer.
        var two = BookRecords.Pick([Item("4", "Game Master's Screen"), Item("5", "Game Master's Screen"), Item("4", "Game Master's Screen")],
            [], ["Game Master's Screen"], null, []);
        var partly = BookRecords.Pick([Item("6", "Gull Rock Saga: Book 2. Spells"), Item("7", "Gull Rock Saga: Book 1. Sailors")],
            Lines, ["grs_02"], "SPELLS\nBOOK 2", ["SPELLS\nBOOK 2"]);
        var many = BookRecords.Pick(Enumerable.Range(10, 4).Select(i => Item($"{i}", "Game Master's Screen")), [], ["Game Master's Screen"], null, []);
        var none = BookRecords.Pick([Item("8", "Gull Rock Saga: Book 1. Sailors")], Lines, ["Spells"], null, []);

        // The longer name holds the shorter, so it's the one.
        Assert.Equal(("2", 0), (one.Sure?.Key, one.Ask.Count));
        Assert.Null(two.Sure);
        Assert.Equal(["4", "5"], two.Ask.Select(i => i.Key));
        Assert.Null(partly.Sure);
        Assert.Equal(["6"], partly.Ask.Select(i => i.Key));
        // More than can be asked about says too little.
        Assert.Equal((null, 0), (many.Sure, many.Ask.Count));
        Assert.Equal((null, 0), (none.Sure, none.Ask.Count));
    }
}
