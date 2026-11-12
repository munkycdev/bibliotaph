namespace Bibliotaph.Core.Tests;

public class FolderKeyTests
{
    [Theory]
    [InlineData(@"Fey Court\Primer.pdf", @"Fey Court", "Fey Court")]
    [InlineData("Fey Court/Maps/Glade.pdf", "Fey Court/Maps", "Maps")]
    [InlineData("Top.pdf", "", "")]
    public void A_files_folder_is_the_path_before_its_name(string relativePath, string folder, string name)
    {
        var key = FolderKey.Of(4, relativePath);
        Assert.Equal(new FolderKey(4, folder), key);
        Assert.Equal(name, key.Name);
    }

    [Fact]
    public void Folders_compare_as_windows_paths_do()
    {
        Assert.Equal(new FolderKey(1, @"Fey Court\Maps"), new FolderKey(1, "fey court/maps"));
        Assert.Equal(new FolderKey(1, @"Fey Court\Maps").GetHashCode(), new FolderKey(1, "fey court/maps").GetHashCode());
        Assert.NotEqual(new FolderKey(1, "Fey Court"), new FolderKey(2, "Fey Court"));
    }

    [Theory]
    [InlineData(@"Fey Court\Primer.pdf", true)]
    [InlineData("fey court/Maps/Glade.pdf", true)]
    [InlineData(@"Fey Courtyard\Gate.pdf", false)]
    [InlineData("Top.pdf", false)]
    public void A_folder_holds_its_files_and_its_subfolders_files(string relativePath, bool held)
    {
        Assert.Equal(held, new FolderKey(1, "Fey Court").Holds(1, relativePath));
        Assert.False(new FolderKey(1, "Fey Court").Holds(2, relativePath));
        Assert.True(new FolderKey(1, "").Holds(1, relativePath));
    }

    [Fact]
    public void A_folder_goes_into_a_menu_value_and_back()
    {
        Assert.Equal("4", new FolderKey(4, "").Text);
        Assert.Equal(@"4:Fey Court\Maps", new FolderKey(4, @"Fey Court\Maps").Text);
        Assert.Equal(new FolderKey(4, @"Fey Court\Maps"), FolderKey.Parse(@"4:Fey Court\Maps"));
        Assert.Equal(new FolderKey(4, ""), FolderKey.Parse("4"));
        Assert.Null(FolderKey.Parse(""));
        Assert.Null(FolderKey.Parse("maps"));
        Assert.Equal(new FolderKey(4, "Maps"), FolderKey.Chosen(4, @"Maps\"));
        Assert.Null(FolderKey.Chosen(null, "Maps"));
    }

    [Fact]
    public void A_menu_names_a_folder_by_its_path_and_its_library_folder_when_there_are_several()
    {
        Assert.Equal("Books", new FolderKey(4, "").Label("Books", withRoot: true));
        Assert.Equal(@"Fey Court\Maps", new FolderKey(4, "Fey Court/Maps").Label("Books", withRoot: false));
        Assert.Equal(@"Books\Fey Court\Maps", new FolderKey(4, "Fey Court/Maps").Label("Books", withRoot: true));
    }

    [Fact]
    public void The_folders_holding_files_include_those_above_them_in_tree_order()
    {
        var folders = FolderKey.Holding([(1, @"Fey Court\Maps\Glade.pdf"), (1, @"Fey Court\Primer.pdf"), (1, @"Fey Courtyard\Gate.pdf"), (1, "Top.pdf"),
            (2, @"Archive\Old.pdf"), (1, "fey court/maps/Other.pdf")]);

        Assert.Equal([new FolderKey(1, "Fey Court"), new FolderKey(1, @"Fey Court\Maps"), new FolderKey(1, "Fey Courtyard"), new FolderKey(2, "Archive")], folders);
    }
}
