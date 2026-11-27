using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Which book a map, handout, character sheet or pack goes with, from folders, ZIPs, names, pages and the AI.</summary>
public sealed class SourceBookRulesTests
{
    const long Root = 1;
    long _next;

    static readonly IReadOnlyDictionary<long, IReadOnlyList<string>> NoCodes = new Dictionary<long, IReadOnlyList<string>>();
    static readonly IReadOnlySet<EntryId> NoSkip = new HashSet<EntryId>();

    SourceCard Book(string title, params string[] folders) =>
        new(new EntryId(++_next), EntryKind.Whole, SourceFormats.Pdf, 200, title, ["adventure"], _next, [.. folders.Select(f => new FolderKey(Root, f))]);

    SourceCard Map(string title, params string[] folders) =>
        new(new EntryId(++_next), EntryKind.Whole, SourceFormats.Jpeg, 1, title, [], _next, [.. folders.Select(f => new FolderKey(Root, f))]);

    SourceCard Sheet(string title, params string[] folders) =>
        new(new EntryId(++_next), EntryKind.Whole, SourceFormats.Pdf, 2, title, ["character-sheet"], _next, [.. folders.Select(f => new FolderKey(Root, f))]);

    static IReadOnlyList<FoundSourceBook> Find(IReadOnlyList<SourceCard> cards, IReadOnlyList<SharedPages>? shared = null,
        IReadOnlyDictionary<long, IReadOnlyList<string>>? codes = null, IReadOnlySet<EntryId>? skip = null,
        IReadOnlyDictionary<EntryId, IReadOnlyList<(string, string?)>>? ai = null) =>
        SourceBookRules.Find(cards, shared ?? [], codes ?? NoCodes, skip ?? NoSkip, ai).Found;

    [Fact]
    public void A_map_beside_the_only_book_in_its_folder_goes_with_it_without_asking()
    {
        var book = Book("Harbor of Shadows", "Adventures\\Harbor");
        var map = Map("Lower docks", "Adventures\\Harbor");

        var found = Assert.Single(Find([book, map]));

        Assert.Equal((map.EntryId, book.EntryId, true), (found.Companion, found.Book, found.Strong));
        Assert.Equal(SourceBookSignal.SameFolder, found.Evidence.Signal);
        Assert.Equal("Harbor", found.Evidence.Detail);
    }

    [Fact]
    public void A_boxed_set_s_cards_go_with_its_book_and_aren_t_offered_as_a_book_for_its_map()
    {
        var book = Book("Harbor of Shadows", "Boxed Set");
        // Untyped, as before the AI has read them, and long enough to pass for a book by their pages alone.
        var cards = new SourceCard(new EntryId(++_next), EntryKind.Whole, SourceFormats.Pdf, 14, "Harbor of Shadows Cards", [], _next,
            [new FolderKey(Root, "Boxed Set")]);
        var map = Map("Harbor of Shadows Map", "Boxed Set");

        var found = Find([book, cards, map]);

        Assert.All(found, f => Assert.Equal(book.EntryId, f.Book));
        Assert.Equal([cards.EntryId, map.EntryId], found.Select(f => f.Companion).OrderBy(e => e.Value));
    }

    [Fact]
    public void A_map_loose_in_the_library_folder_beside_one_book_is_only_proposed()
    {
        var book = Book("Harbor of Shadows", "");
        var map = Map("Lower docks", "");

        Assert.False(Assert.Single(Find([book, map])).Strong);
    }

    [Fact]
    public void A_folder_of_a_few_books_proposes_each_and_a_folder_of_many_proposes_none()
    {
        var first = Book("Harbor of Shadows", "Campaign");
        var second = Book("Tower of Ash", "Campaign");
        var map = Map("Lower docks", "Campaign");

        var found = Find([first, second, map]);

        Assert.Equal([first.EntryId, second.EntryId], found.Select(f => f.Book).OrderBy(b => b.Value));
        Assert.All(found, f => Assert.False(f.Strong));
        Assert.All(found, f => Assert.Equal(0.3, f.Evidence.Score));

        var many = Enumerable.Range(1, SourceBookRules.FewBooks + 1).Select(i => Book($"Volume number {i}", "Shelf")).ToList();
        Assert.Empty(Find([.. many, Map("Lower docks", "Shelf")]));
    }

    [Fact]
    public void A_folder_of_extras_under_the_book_goes_with_it_but_any_other_subfolder_does_not()
    {
        var book = Book("Harbor of Shadows", "Harbor");
        var map = Map("Lower docks", "Harbor\\Player Maps");
        var other = Map("Lower docks", "Harbor\\Session notes");

        var found = Assert.Single(Find([book, map, other]));

        Assert.Equal((map.EntryId, true, 0.85), (found.Companion, found.Strong, found.Evidence.Score));
        Assert.True(SourceBookRules.IsExtrasFolder("VTT Assets"));
        Assert.True(SourceBookRules.IsExtrasFolder("Maps 2"));
        Assert.False(SourceBookRules.IsExtrasFolder("Session notes"));
    }

    [Fact]
    public void A_pack_inside_the_zip_the_book_came_in_goes_with_it()
    {
        var book = Book("Harbor of Shadows", "Downloads\\Harbor.zip\\Book");
        var pack = new SourceCard(new EntryId(++_next), EntryKind.Pack, SourceFormats.Png, null, "Tokens", [], ++_next,
            [new FolderKey(Root, "Downloads\\Harbor.zip\\Art\\Tokens")]);

        var found = Assert.Single(Find([book, pack]));

        Assert.Equal((pack.EntryId, book.EntryId, true, SourceBookSignal.SameZip), (found.Companion, found.Book, found.Strong, found.Evidence.Signal));
        Assert.Equal("Harbor", found.Evidence.Detail);
    }

    [Fact]
    public void A_name_carrying_the_title_product_code_or_initials_is_proposed()
    {
        var harbor = Book("Harbor of Shadows: A Coastal Adventure", "Books");
        var tower = Book("The Tower of Ash and Ember", "Books\\More");
        var coded = Book("Into the Deep", "Books\\Coded");
        var named = Sheet("Harbor of Shadows pregens", "Sheets");
        var initialled = Map("TAE handouts", "Maps");
        var code = Map("ABC1234 handouts", "Maps");

        var found = Find([harbor, tower, coded, named, initialled, code], codes: new Dictionary<long, IReadOnlyList<string>> { [coded.DocumentId!.Value] = ["ABC1234"] });

        var byName = Assert.Single(found, f => f.Companion == named.EntryId);
        Assert.Equal((harbor.EntryId, false, SourceBookSignal.Name), (byName.Book, byName.Strong, byName.Evidence.Signal));
        Assert.True(byName.Evidence.Score >= 0.6);
        Assert.Equal(tower.EntryId, Assert.Single(found, f => f.Companion == initialled.EntryId).Book);
        var byCode = Assert.Single(found, f => f.Companion == code.EntryId);
        Assert.Equal((coded.EntryId, "ABC1234", 0.7), (byCode.Book, byCode.Evidence.Detail, byCode.Evidence.Score));
    }

    [Fact]
    public void A_word_every_file_is_called_by_isnt_taken_for_a_books_initials()
    {
        // "Ruins of the Pale Gate" has the initials "rpg" (and "rotpg"), and so do many files.
        var ruins = Book("Ruins of the Pale Gate", "Books");
        var starfall = Map("Starfall RPG", "Starfall");
        Assert.Empty(Find([ruins, starfall]));

        // A word several books are titled with is a word of the library too, while on its own it may be initials.
        var gods = Book("Fallen Old Gods", "Books\\Gods");
        var tokens = Map("Fog tokens", "Tokens");
        Assert.Equal(gods.EntryId, Assert.Single(Find([gods, tokens])).Book);
        Assert.Empty(Find([gods, Book("Fog over Marrow", "Books\\Marrow"), Book("Fog Lanterns", "Books\\Lanterns"), tokens]));
    }

    [Fact]
    public void Pages_printed_from_a_longer_book_go_with_it()
    {
        var book = Book("Harbor of Shadows", "Books");
        var handout = Sheet("Untitled", "Printouts");

        var found = Assert.Single(Find([book, handout], shared: [new SharedPages(handout.DocumentId!.Value, book.DocumentId!.Value, 2, 2)]));

        Assert.Equal((book.EntryId, true, SourceBookSignal.SharedPages, "2 pages"), (found.Book, found.Strong, found.Evidence.Signal, found.Evidence.Detail));
        // One page of three in common isn't enough.
        Assert.Empty(Find([book, handout], shared: [new SharedPages(handout.DocumentId!.Value, book.DocumentId!.Value, 1, 3)]));
    }

    [Fact]
    public void A_title_the_AI_read_in_its_pages_is_proposed_with_its_quote()
    {
        var book = Book("Harbor of Shadows", "Books");
        var map = Map("Lower docks", "Maps");

        var found = Assert.Single(Find([book, map], ai: new Dictionary<EntryId, IReadOnlyList<(string, string?)>>
        {
            [map.EntryId] = [("Harbor of Shadows", "From the pages of Harbor of Shadows")],
        }));

        Assert.Equal((book.EntryId, false, SourceBookSignal.Ai), (found.Book, found.Strong, found.Evidence.Signal));
        Assert.Equal("From the pages of Harbor of Shadows", found.Evidence.Detail);
    }

    [Fact]
    public void A_strong_signal_drops_weak_guesses_but_keeps_another_title()
    {
        var beside = Book("Harbor of Shadows", "Harbor");
        var other = Book("The Tower of Ash and Ember", "Books");
        var third = Book("Into the Deep", "Books\\Deep");
        var map = Map("TAE map", "Harbor");
        var titled = Map("Into the Deep map", "Harbor");

        var found = Find([beside, other, third, map, titled]);

        // The initials guess is dropped beside a strong signal; a whole title is still asked about.
        Assert.Equal([beside.EntryId], found.Where(f => f.Companion == map.EntryId).Select(f => f.Book));
        Assert.Equal([beside.EntryId, third.EntryId], found.Where(f => f.Companion == titled.EntryId).Select(f => f.Book).OrderBy(b => b.Value));
    }

    [Fact]
    public void Books_and_cards_the_user_has_decided_are_left_out()
    {
        var book = Book("Harbor of Shadows", "Harbor");
        var map = Map("Lower docks", "Harbor");
        var big = Book("Lower docks gazetteer", "Harbor");

        Assert.False(big.IsCompanion);
        Assert.True(map.IsCompanion);
        var (considered, found) = SourceBookRules.Find([book, map], [], NoCodes, new HashSet<EntryId> { map.EntryId });
        Assert.Empty(considered);
        Assert.Empty(found);

        // A book others go with is still a book for the rest; a card that goes with a book is never one.
        var second = Map("Upper docks", "Harbor");
        Assert.Equal(2, Find([book, map, second], skip: new HashSet<EntryId> { book.EntryId }).Count(f => f.Book == book.EntryId));
        var linkedBook = Book("Harbor of Shadows", "Harbor");
        Assert.Empty(SourceBookRules.Find([linkedBook, second], [], NoCodes, NoSkip, linked: new HashSet<EntryId> { linkedBook.EntryId }).Found);
    }

    [Fact]
    public void The_zip_a_folder_is_in_is_the_path_up_to_the_archive()
    {
        Assert.Equal(new FolderKey(Root, "Downloads\\Harbor.zip"), SourceBookRules.InZip(new FolderKey(Root, "Downloads\\Harbor.zip\\Art\\Tokens")));
        Assert.Null(SourceBookRules.InZip(new FolderKey(Root, "Downloads\\Harbor")));
    }
}
