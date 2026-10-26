using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

/// <summary>A New version? card's buttons name the file each makes current, since "this file" left which was which to guess.</summary>
public sealed class VersionCardTextTests
{
    static VersionItem Item(string path, string bookPath) => new(
        new PendingVersion(1, 2, new EntryId(3), 4, new EntryId(5), VersionEvidence.SharedPages, 187, 190, 196, 196, DateTime.UtcNow),
        "Gazetteer of the Ash Coast", path, "Gazetteer of the Ash Coast", bookPath);

    [Fact]
    public void Each_choice_names_its_file_and_none_stands_out()
    {
        var text = new VersionCardText(Item("Books/Ash Coast/Gazetteer_v2.pdf", "Books/Ash Coast/Gazetteer.pdf"));

        Assert.Equal(["Gazetteer_v2.pdf is current", "Gazetteer.pdf is current", "Separate book"], text.Choices.Select(c => c.Label));
        Assert.All(text.Choices, c => Assert.False(c.Primary));
        Assert.Equal("One card for both files, still opening Gazetteer.pdf. Gazetteer_v2.pdf is kept as another version.", text.KeepAsCopyHint);
    }

    [Fact]
    public void Files_of_one_name_are_told_apart_by_their_folders()
    {
        var text = new VersionCardText(Item(@"D:\Books\Reprint\Gazetteer.pdf", @"D:\Books\First printing\Gazetteer.pdf"));

        Assert.Equal(["Reprint/Gazetteer.pdf is current", "First printing/Gazetteer.pdf is current"], text.Choices.Take(2).Select(c => c.Label));
        Assert.Equal("Gazetteer of the Ash Coast now opens Reprint/Gazetteer.pdf.", text.MadeCurrent);
    }
}
