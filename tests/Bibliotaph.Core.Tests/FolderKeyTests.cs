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
}
