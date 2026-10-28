namespace Bibliotaph.Catalog.Tests;

public sealed class WebLibraryPathsTests
{
    [Theory]
    [InlineData("/bibliotaph.online/7", 7L, "")]
    [InlineData("/bibliotaph.online/7/Adventures/Lantern.pdf", 7L, "Adventures/Lantern.pdf")]
    // A full path joins with a backslash on Windows.
    [InlineData("/bibliotaph.online/12\\Adventures\\Lantern.pdf", 12L, "Adventures/Lantern.pdf")]
    public void A_web_library_path_names_its_folder_and_the_path_under_it(string path, long folder, string relative)
    {
        Assert.True(WebLibraryPaths.TryParse(path, out var id, out var under));
        Assert.Equal((folder, relative), (id, under));
    }

    [Theory]
    [InlineData(@"C:\RPG\Lantern.pdf")]
    [InlineData(@"\\nas\bibliotaph.online\7")]
    [InlineData("/onedrive/drive/RPG")]
    [InlineData("/bibliotaph.online/")]
    [InlineData("/bibliotaph.online/maps/Lantern.pdf")]
    public void Nothing_else_is_one(string path) => Assert.False(WebLibraryPaths.IsWebPath(path));

    [Fact]
    public void A_folders_path_is_its_id_under_the_web_library() => Assert.Equal("/bibliotaph.online/7", WebLibraryPaths.Of(7));
}
