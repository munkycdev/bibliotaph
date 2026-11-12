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
}
