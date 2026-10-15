namespace Bibliotaph.Processing.Tests;

/// <summary>Where a store's download goes.</summary>
public sealed class DownloadPlacesTests
{
    static readonly string Base = Path.Combine(Path.GetTempPath(), "bibliotaph-downloads");
    static readonly string Games = Path.Combine(Base, "Games");
    static readonly string Maps = Path.Combine(Base, "Games", "Maps");

    [Fact]
    public void A_downloads_folder_must_be_a_library_folder_or_inside_one()
    {
        string[] roots = [Games, Maps, Path.Combine(Base, "Other")];
        Assert.Equal(Games, DownloadPlaces.RootOf(Games, roots, r => r));
        Assert.Equal(Games, DownloadPlaces.RootOf(Path.Combine(Games, "Downloads") + Path.DirectorySeparatorChar, roots, r => r));
        // The innermost library folder holds it.
        Assert.Equal(Maps, DownloadPlaces.RootOf(Path.Combine(Maps, "New"), roots, r => r));
        // A folder whose name only starts the same isn't inside.
        Assert.Null(DownloadPlaces.RootOf(Games + " old", roots, r => r));
        Assert.Null(DownloadPlaces.RootOf(Base, roots, r => r));
    }

    [Fact]
    public void A_download_never_takes_the_name_of_a_file_already_there()
    {
        HashSet<string> taken = [Path.Combine(Games, "Atlas.pdf"), Path.Combine(Games, "Atlas (2).pdf")];
        Assert.Equal(Path.Combine(Games, "Atlas (3).pdf"), DownloadPlaces.FreePath(Games, "Atlas.pdf", taken.Contains));
        Assert.Equal(Path.Combine(Games, "Bestiary.zip"), DownloadPlaces.FreePath(Games, @"C:\Users\someone\Downloads\Bestiary.zip", taken.Contains));
        Assert.Equal(Path.Combine(Games, "download"), DownloadPlaces.FreePath(Games, "..", taken.Contains));
        Assert.Equal(Path.Combine(Games, "a_b.pdf"), DownloadPlaces.FreePath(Games, "a\0b.pdf", taken.Contains));
    }
}
