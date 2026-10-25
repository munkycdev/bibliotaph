using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.OneDrive;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Tests;

public sealed class OneDriveTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly LibraryFixture _fixture;

    public OneDriveTests()
    {
        _fixture = new LibraryFixture(_folder.Path, TimeProvider.System);
        _fixture.Graph.Add("RPG/Adventures/one.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 one"));
        _fixture.Graph.Add("RPG/Adventures/two.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 two"));
        _fixture.Graph.Add("RPG/Maps/map.png", [0x89, 0x50, 0x4E, 0x47]);
        _fixture.Graph.Add("RPG/notes.txt", Encoding.ASCII.GetBytes("notes"));
        _fixture.Graph.Add("Other/three.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 three"));
    }

    public void Dispose()
    {
        _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _folder.Dispose();
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("drive1", "", "/onedrive/drive1")]
    [InlineData("drive1", "RPG/Adventures", "/onedrive/drive1/RPG/Adventures")]
    public void A_drive_path_reads_back_as_its_drive_and_folder(string drive, string inner, string expected)
    {
        var path = DrivePath.Of(drive, inner);

        Assert.Equal(expected, path);
        Assert.True(DrivePath.TryParse(path, out var parsedDrive, out var parsedInner));
        Assert.Equal((drive, inner), (parsedDrive, parsedInner));
    }

    [Fact]
    public void A_local_path_isnt_a_drive_path() => Assert.False(DrivePath.TryParse("/data/books", out _, out _));

    [Fact]
    public async Task Listing_walks_every_folder_but_leaves_nested_library_folders_to_themselves()
    {
        var (userId, _) = await _fixture.UserWithDriveAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        var lister = new OneDriveSourceLister(drives);

        var listing = await lister.ListAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), [DrivePath.Of(FakeGraph.DriveId, "RPG/Maps")], Ct);

        Assert.Equal(["Adventures/one.pdf", "Adventures/two.pdf", "notes.txt"], listing!.Files.Select(f => f.RelativePath).Order());
        Assert.All(listing.Files, f => Assert.False(f.OnlineOnly));
        Assert.Equal(0, _fixture.Graph.Downloads);
    }

    [Fact]
    public async Task A_folder_gone_from_the_drive_or_a_drive_not_connected_cant_be_reached()
    {
        var (userId, _) = await _fixture.UserWithDriveAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        var lister = new OneDriveSourceLister(drives);

        Assert.Null(await lister.ListAsync(DrivePath.Of(FakeGraph.DriveId, "Gone"), [], Ct));
        Assert.Null(await lister.ListAsync(DrivePath.Of("another-drive", "RPG"), [], Ct));
    }

    [Fact]
    public async Task A_book_is_read_from_a_temporary_download_that_goes_when_the_stream_closes()
    {
        var (userId, _) = await _fixture.UserWithDriveAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        var temporary = Path.Combine(_folder.Path, "downloads");
        var reader = new OneDriveFileReader(drives, temporary, new Bibliotaph.Processing.SourceFileReader());

        using (var stream = reader.OpenRead(DrivePath.Of(FakeGraph.DriveId, "RPG/Adventures/one.pdf")))
        {
            Assert.True(stream.CanSeek);
            Assert.Equal("%PDF-1.7 one", new StreamReader(stream).ReadToEnd());
            Assert.Single(Directory.GetFiles(temporary));
        }

        Assert.Empty(Directory.GetFiles(temporary));
        Assert.Throws<FileNotFoundException>(() => reader.OpenRead(DrivePath.Of(FakeGraph.DriveId, "RPG/missing.pdf")));
        Assert.Empty(Directory.GetFiles(temporary));
    }

    [Fact]
    public async Task A_library_folder_on_a_drive_is_scanned_and_its_books_hashed()
    {
        var (userId, _) = await _fixture.UserWithDriveAsync();
        await using var lease = await _fixture.Libraries.OpenAsync(userId, Ct);
        var root = await lease.Library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        lease.Library.Indexing.RequestScan(root.Id);

        var counts = await WaitAsync(lease.Library, c => c.Files == 3 && c.Unhashed == 0);

        Assert.Equal(3, counts.Documents);
        Assert.Equal(3, _fixture.Graph.Downloads);
        Assert.Empty(Directory.Exists(_fixture.Paths.Downloads) ? Directory.GetFiles(_fixture.Paths.Downloads) : []);
    }

    [Fact]
    public async Task A_change_on_the_drive_is_found_when_the_drive_says_it_changed()
    {
        var (userId, _) = await _fixture.UserWithDriveAsync();
        await using var lease = await _fixture.Libraries.OpenAsync(userId, Ct);
        var root = await lease.Library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        lease.Library.Indexing.RequestScan(root.Id);
        await WaitAsync(lease.Library, c => c.Files == 3 && c.Unhashed == 0);

        _fixture.Graph.Add("RPG/Adventures/four.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 four"));
        await _fixture.Libraries.DriveChangedAsync(userId, FakeGraph.DriveId, Ct);

        await WaitAsync(lease.Library, c => c.Files == 4 && c.Unhashed == 0);
    }

    [Fact]
    public async Task A_refused_refresh_token_marks_the_drive_as_needing_connecting_again()
    {
        var (userId, drive) = await _fixture.UserWithDriveAsync();
        _fixture.Graph.RefuseRefresh = true;

        await Assert.ThrowsAsync<OneDriveException>(() => _fixture.Tokens.AccessTokenAsync(drive.Id, Ct));

        Assert.True(Assert.Single(await _fixture.Connections.ListAsync(userId, Ct)).NeedsReconnect);
    }

    [Fact]
    public async Task A_rotated_refresh_token_is_kept_and_an_access_token_is_reused()
    {
        var (_, drive) = await _fixture.UserWithDriveAsync();

        Assert.Equal("access", await _fixture.Tokens.AccessTokenAsync(drive.Id, Ct));
        Assert.Equal("access", await _fixture.Tokens.AccessTokenAsync(drive.Id, Ct));

        Assert.Equal(1, _fixture.Graph.TokenRequests);
        var saved = await _fixture.Connections.FindAsync(drive.Id, Ct);
        Assert.Equal("rotated", _fixture.Connections.RefreshToken(saved!));
    }

    [Fact]
    public async Task A_notification_is_believed_only_with_its_connections_secret()
    {
        var (_, drive) = await _fixture.UserWithDriveAsync();
        var connection = (await _fixture.Connections.FindAsync(drive.Id, Ct))!;

        Assert.True(ClientState.Parse(ClientState.For(connection)) is { } parsed && ClientState.Matches(connection, parsed.Secret));
        Assert.False(ClientState.Matches(connection, "guessed"));
        Assert.Null(ClientState.Parse("no-secret"));
        Assert.True(ClientState.For(connection).Length <= 128);
    }

    [Fact]
    public async Task Subscribing_needs_a_public_https_address()
    {
        var (_, drive) = await _fixture.UserWithDriveAsync();
        var connection = (await _fixture.Connections.FindAsync(drive.Id, Ct))!;

        await Notifications("https://localhost:5179").EnsureSubscribedAsync(connection, Ct);
        Assert.Empty(_fixture.Graph.Subscriptions);

        await Notifications("https://bibliotaph.example").EnsureSubscribedAsync(connection, Ct);
        Assert.Equal("POST /v1.0/subscriptions", Assert.Single(_fixture.Graph.Subscriptions));
        Assert.Equal("subscription1", (await _fixture.Connections.FindAsync(drive.Id, Ct))!.SubscriptionId);
    }

    DriveNotifications Notifications(string origin) =>
        new(_fixture.Connections, _fixture.Drives, _fixture.Libraries, Options.Create(new ServerOptions { PublicOrigin = origin }),
            NullLogger<DriveNotifications>.Instance);

    static async Task<LibraryCounts> WaitAsync(Libraries.UserLibrary library, Func<LibraryCounts, bool> done)
    {
        var store = library.Get<LibraryStore>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        LibraryCounts counts;
        while (!done(counts = await store.GetCountsAsync(timeout.Token)))
            await Task.Delay(50, timeout.Token);
        return counts;
    }
}
