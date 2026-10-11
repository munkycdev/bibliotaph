using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

/// <summary>The reader's prompt for a book that can't be opened: why, naming the folder, and what to do.</summary>
public sealed class AwayFileTests
{
    static readonly string Library = Path.Combine(Path.GetTempPath(), "Made-up Library");

    [Fact]
    public void A_file_in_an_offline_folder_is_offline_even_when_another_copy_is_missing_and_names_its_drive()
    {
        var gone = new DocumentLocation(Path.Combine(Library, "Old", "Tomb.pdf"), FileLocationState.Missing, SourceRootAvailability.Online, RootId: 1, RootPath: Library);
        var away = new DocumentLocation(@"E:\RPG Books\Tomb.pdf", FileLocationState.Present, SourceRootAvailability.Offline, RootId: 2, RootPath: @"E:\RPG Books");

        var offline = AwayFile.From([gone, away]);
        Assert.Equal((AwayReason.Offline, @"E:\RPG Books", "E:"), (offline.Reason, offline.Folder, offline.Drive));
        Assert.Equal([2L], offline.FoldersToLook);
        Assert.False(offline.CanShowFolder);
        Assert.Equal("Plug in the drive to read this book.", offline.Title);
        Assert.Contains(@"E:\RPG Books, on drive E:", offline.Message, StringComparison.Ordinal);
        Assert.Equal("Try again", offline.Action);
        Assert.Equal(@"Its folder, E:\RPG Books, is offline. Plug in drive E: and it opens again.", offline.Line);
    }

    [Fact]
    public void A_network_folder_has_no_drive_to_plug_in()
    {
        var share = AwayFile.From([new DocumentLocation(@"\\attic\books\Tomb.pdf", FileLocationState.Present, SourceRootAvailability.Offline, RootId: 3, RootPath: @"\\attic\books")]);
        Assert.Null(share.Drive);
        Assert.Equal("This book's folder can't be reached right now.", share.Title);
        Assert.Contains(@"\\attic\books", share.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_says_where_it_was_last_and_looks_in_every_folder()
    {
        var last = Path.Combine(Library, "Adventures", "Tomb.pdf");
        var missing = AwayFile.From([new DocumentLocation(last, FileLocationState.Missing, SourceRootAvailability.Online, RootId: 1, RootPath: Library)]);
        Assert.Equal((AwayReason.Missing, Library, last), (missing.Reason, missing.Folder, missing.LastPath));
        Assert.Null(missing.FoldersToLook);
        Assert.True(missing.CanShowFolder);
        Assert.Equal("Find it", missing.Action);
        Assert.Contains(Path.Combine(Library, "Adventures"), missing.Message, StringComparison.Ordinal);

        // A file inside a ZIP was last where its ZIP was.
        var zipped = AwayFile.From([new DocumentLocation(Path.Combine(Library, "Bundle.zip", "Tomb.pdf"), FileLocationState.Missing, SourceRootAvailability.Online,
            Path.Combine(Library, "Bundle.zip"), 1, Library)]);
        Assert.Equal(Library, zipped.LastFolder);

        var nowhere = AwayFile.From([]);
        Assert.Equal((AwayReason.Missing, false), (nowhere.Reason, nowhere.CanShowFolder));
        Assert.Equal("Its file is gone from your library folders.", nowhere.Line);
    }

    [Fact]
    public void A_new_version_being_read_isnt_a_file_that_has_gone()
    {
        var reading = AwayFile.From([], beingRead: true);
        Assert.Equal(AwayReason.BeingRead, reading.Reason);
        Assert.Empty(reading.FoldersToLook!);
        Assert.Equal("New version: still being read.", reading.Line);
    }
}
