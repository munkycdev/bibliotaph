using Bibliotaph.Core;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Files that reading downloaded from OneDrive, waiting to go back to online-only (Dale's review, row 2).</summary>
public sealed partial class LibraryStoreTests
{
    [Fact]
    public async Task A_file_marked_to_go_back_online_only_is_listed_once_read_and_unmarked_when_it_goes_back()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("cloud.pdf", onlineOnly: true), File("local.pdf"), File("later.pdf", 3000, onlineOnly: true)], ct: Ct);
        var unhashed = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).ToDictionary(f => Path.GetFileName(f.FullPath));
        await _library.MarkToReturnAsync(unhashed["cloud.pdf"].LocationId, Ct);
        await _library.MarkToReturnAsync(unhashed["later.pdf"].LocationId, Ct);
        var cloud = (await _library.AttachHashAsync(unhashed["cloud.pdf"], Hash('a'), Ct))!.Value.DocumentId;
        await _library.AttachHashAsync(unhashed["local.pdf"], Hash('b'), Ct);

        // Only once read: a marked file not hashed yet still has its stages ahead of it.
        var waiting = Assert.Single(await _library.ListToReturnAsync(Ct));
        Assert.Equal((unhashed["cloud.pdf"].LocationId, Path.Combine(_dir, "Library", "cloud.pdf"), 1000L), (waiting.LocationId, waiting.FullPath, waiting.SizeBytes));
        Assert.Equal([cloud], waiting.DocumentIds);

        // A scan between reading and going back sees it downloaded; going back makes it online-only again.
        await _library.ReconcileRootAsync(root, [File("cloud.pdf"), File("local.pdf"), File("later.pdf", 3000, onlineOnly: true)], ct: Ct);
        Assert.Equal(FileLocationState.Present, (await LocationsAsync()).Single(l => l.RelativePath == "cloud.pdf").State);
        await _library.ReturnedToOnlineOnlyAsync(waiting.LocationId, Ct);
        var location = (await LocationsAsync()).Single(l => l.RelativePath == "cloud.pdf");
        Assert.Equal((FileLocationState.OnlineOnly, false), (location.State, location.ReturnToOnlineOnly));
        Assert.Empty(await _library.ListToReturnAsync(Ct));

        // Unmarking keeps it as it is.
        await _library.UnmarkToReturnAsync(unhashed["later.pdf"].LocationId, Ct);
        Assert.False((await LocationsAsync()).Single(l => l.RelativePath == "later.pdf").ReturnToOnlineOnly);
    }

    [Fact]
    public async Task A_marked_file_that_changes_is_no_longer_marked()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("cloud.pdf", onlineOnly: true)], ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single();
        await _library.MarkToReturnAsync(file.LocationId, Ct);
        await _library.AttachHashAsync(file, Hash('a'), Ct);

        // Whether reading the new content downloads it is for the hasher to see then.
        await _library.ReconcileRootAsync(root, [File("cloud.pdf", size: 2000)], ct: Ct);

        Assert.False((await LocationsAsync()).Single().ReturnToOnlineOnly);
        Assert.Empty(await _library.ListToReturnAsync(Ct));
    }

    [Fact]
    public async Task A_marked_ZIP_waits_for_the_documents_inside_it_and_goes_back_with_them()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("Bundle.zip", onlineOnly: true)], ct: Ct);
        var zip = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single();
        await _library.MarkToReturnAsync(zip.LocationId, Ct);
        var pending = (await _library.ReconcileArchiveAsync(zip, [Member("Book.pdf", 1), Member("Maps/Harbor.png", 2)], Ct))!;
        var book = (await _library.AttachHashAsync(pending[0], Hash('a'), Ct))!.Value.DocumentId;
        var map = (await _library.AttachHashAsync(pending[1], Hash('b'), Ct))!.Value.DocumentId;
        Assert.Empty(await _library.ListToReturnAsync(Ct));
        await _library.AttachArchiveHashAsync(zip, Hash('f'), Ct);

        var waiting = Assert.Single(await _library.ListToReturnAsync(Ct));
        Assert.Equal(zip.LocationId, waiting.LocationId);
        Assert.Equal([book, map], waiting.DocumentIds.Order());

        // Downloaded, as a scan between reading and going back sees it; its files follow it.
        await _library.ReconcileRootAsync(root, [File("Bundle.zip")], ct: Ct);
        Assert.All(await LocationsAsync(), l => Assert.Equal(FileLocationState.Present, l.State));
        await _library.ReturnedToOnlineOnlyAsync(zip.LocationId, Ct);
        Assert.All(await LocationsAsync(), l => Assert.Equal(FileLocationState.OnlineOnly, l.State));
    }
}
