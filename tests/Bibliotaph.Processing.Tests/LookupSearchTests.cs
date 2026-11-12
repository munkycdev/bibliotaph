using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>What a book is searched for by in the catalogues searched by name: its titles once each, and the lines it prints.</summary>
public sealed class LookupSearchTests
{
    [Fact]
    public void A_folder_the_book_prints_the_name_of_is_its_line_nearest_first()
    {
        string[] front = ["BESTIARY", "Credits. Gull Rock Saga is a trademark of Example Press."];

        Assert.Equal(["Gull Rock Saga"], BookLookups.Lines("Gull Rock Saga / Bestiaries", front));
        Assert.Equal(["Gull Rock Saga"], BookLookups.Lines("Gull_Rock_Saga", front));
        // Written as a title would read it, unless that splits a name the book prints whole.
        Assert.Equal(["Maps", "GullRock"], BookLookups.Lines("GullRock / Maps", ["THE GULLROCK ATLAS", "Maps by C. Painter."]));
    }

    [Theory]
    [InlineData("My Downloads / To Sort")]
    [InlineData("")]
    [InlineData(null)]
    // Too short to search for: it would find too many books.
    [InlineData("C. P")]
    public void A_folder_the_book_doesnt_print_isnt_sent(string? folders)
    {
        Assert.Empty(BookLookups.Lines(folders, ["Example Press presents C. Painter's maps"]));
    }

    [Fact]
    public void A_book_with_no_text_has_no_line()
    {
        Assert.Empty(BookLookups.Lines("Gull Rock Saga", [null, " "]));
    }

    [Fact]
    public void A_title_is_searched_for_once_however_it_is_written()
    {
        Assert.Equal(["The Owlbear Mill", "Goblin Warrens"],
            BookRecords.Searches(["The Owlbear Mill ", "the owlbear mill.", "  ", "Goblin Warrens", "Lighthouse at the End"], 2));
    }

    [Fact]
    public void A_cover_prints_its_title_in_the_lines_that_read_as_words()
    {
        // A cover's art read as text leaves noise among the words.
        var (title, lines) = BookLookups.Cover("TALES OF THE GULL ROCK SAGA\r\n“> ce! oe 4 ly fps taf Fl it i ;\r\nra | Meme lad im\r\nMONSTER TOME\r\ny gee");

        Assert.Equal("TALES OF THE GULL ROCK SAGA MONSTER TOME", title);
        // Only a line of three words or more stands for a line of books.
        Assert.Equal(["TALES OF THE GULL ROCK SAGA"], lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("| = 4 ~ ; \\) a A =")]
    public void A_cover_that_reads_as_no_words_has_no_title(string? cover)
    {
        Assert.Equal((null, 0), (BookLookups.Cover(cover).Title, BookLookups.Cover(cover).Lines.Count));
    }

    [Fact]
    public void The_records_of_the_books_beside_one_name_their_line_before_a_colon()
    {
        Assert.Equal(["Gull Rock Saga"], BookLookups.LinesBeside(
            ["Gull Rock Saga: Book 1. Heroes", "gull rock saga: Book 2. Spells", "The Owlbear Mill", "GRS: Maps"]));
    }

    [Fact]
    public void A_record_found_by_title_names_the_book_only_when_every_part_of_its_title_is_printed()
    {
        var screen = new BookRecord("RPGGeek", "1", "", "Gull Rock Saga: Game Master's Screen", [], [], null);

        Assert.True(BookRecords.IsNamedWholeIn(screen, ["GAME MASTER'S SCREEN", "Gull Rock Saga is a trademark of Example Press."]));
        // The line's name is printed in every book of the line, so it isn't enough.
        Assert.False(BookRecords.IsNamedWholeIn(screen, ["Gull Rock Saga is a trademark of Example Press."]));
        Assert.True(BookRecords.IsNamedIn(screen, ["Gull Rock Saga is a trademark of Example Press."]));
    }

    [Theory]
    [InlineData("No record of its ISBN in RPGGeek.", true)]
    [InlineData("No record of its product code in RPGGeek or Open Library.", true)]
    [InlineData("No record of its title in AdventureLookup.", true)]
    [InlineData("Looked up in RPGGeek or Open Library.", false)]
    [InlineData(null, false)]
    public void A_book_no_catalogue_found_is_looked_up_again_when_its_title_changes(string? reason, bool again)
    {
        Assert.Equal(again, BookLookups.Missed(reason));
    }
}
