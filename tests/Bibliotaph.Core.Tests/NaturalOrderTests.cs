namespace Bibliotaph.Core.Tests;

public sealed class NaturalOrderTests
{
    [Fact]
    public void File_names_sort_as_File_Explorer_lists_them()
    {
        string[] names = ["Goblin 10.png", "goblin 2.png", "Goblin 1.png", "Goblin 02b.png", "Ghast.jpg", "Goblin.png"];

        Assert.Equal(["Ghast.jpg", "Goblin 1.png", "goblin 2.png", "Goblin 02b.png", "Goblin 10.png", "Goblin.png"],
            names.Order(NaturalOrder.Instance));
    }

    [Fact]
    public void A_folders_title_keeps_its_dots() =>
        Assert.Equal(("Tokens.Undead", "Harbor Set"), (DisplayTitle.FromFolderName("Tokens.Undead"), DisplayTitle.FromFileName("Harbor_Set.zip")));
}
