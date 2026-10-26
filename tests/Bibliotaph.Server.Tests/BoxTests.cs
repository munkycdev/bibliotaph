using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Tests;

public sealed class BoxTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly LibraryFixture _fixture;

    public BoxTests()
    {
        _fixture = new LibraryFixture(_folder.Path, TimeProvider.System);
        _fixture.Box.Add("RPG/Adventures/one.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 one"));
        _fixture.Box.Add("RPG/Adventures/two.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 two"));
        _fixture.Box.Add("RPG/Adventures/three.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 three"));
        _fixture.Box.Add("RPG/Maps/Handouts.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 maps"));
    }

    public void Dispose()
    {
        _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _folder.Dispose();
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string At(string path) => StoragePath.Of(BoxProvider.Key, FakeBox.UserId, path);

    async Task<UserDrives> DrivesAsync()
    {
        var (userId, _) = await _fixture.UserWithBoxAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        return drives;
    }

    [Fact]
    public async Task Listing_finds_every_file_across_pages()
    {
        var listing = await new StorageSourceLister(await DrivesAsync()).ListAsync(At("RPG"), [], Ct);

        Assert.Equal(["Adventures/one.pdf", "Adventures/three.pdf", "Adventures/two.pdf", "Maps/Handouts.pdf"],
            listing!.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(0, _fixture.Box.Downloads);
    }

    [Fact]
    public async Task A_file_is_read_by_its_path_whole_or_a_range_at_a_time()
    {
        var drives = await DrivesAsync();
        var reader = new StorageFileReader(drives, Path.Combine(_folder.Path, "downloads"), new Bibliotaph.Processing.SourceFileReader());
        var (drive, at) = drives.Locate(At("RPG/Adventures/three.pdf"))!.Value;

        using (var stream = reader.OpenRead(At("RPG/Adventures/three.pdf")))
            Assert.Equal("%PDF-1.7 three", new StreamReader(stream).ReadToEnd());
        await using (var range = await drive.OpenReadAsync(at.Path, new ByteRange(9, 13), Ct))
            Assert.Equal("three", new StreamReader(range).ReadToEnd());

        Assert.False(drive.HasReadLinks);
        Assert.True((await drive.ItemAsync("RPG/Maps", Ct))!.IsFolder);
        Assert.Throws<FileNotFoundException>(() => reader.OpenRead(At("RPG/Adventures/missing.pdf")));
        Assert.Null(await drive.ItemAsync("RPG/Nowhere/one.pdf", Ct));
        Assert.Null(await new StorageSourceLister(drives).ListAsync(At("RPG/Adventures/one.pdf"), [], Ct));
    }

    [Fact]
    public async Task A_new_book_is_found_when_the_change_stream_moves_on()
    {
        var (userId, _) = await _fixture.UserWithBoxAsync();
        var notifications = new DriveNotifications(_fixture.Connections, _fixture.Drives, _fixture.Libraries,
            Options.Create(new ServerOptions()), NullLogger<DriveNotifications>.Instance);
        await notifications.StartAsync(Ct);
        var changes = new PolledDriveChanges(_fixture.Connections, _fixture.Drives, notifications, TimeProvider.System,
            NullLogger<PolledDriveChanges>.Instance);
        try
        {
            await using var lease = await _fixture.Libraries.OpenAsync(userId, Ct);
            var root = await lease.Library.Get<SourceRootStore>().AddAsync(At("RPG"), Ct);
            lease.Library.Indexing.RequestScan(root.Id);
            await WaitAsync(lease.Library, c => c.Files == 4 && c.Unhashed == 0);
            await changes.RoundAsync(Ct);

            _fixture.Box.Add("RPG/Adventures/four.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 four"));
            await changes.RoundAsync(Ct);

            await WaitAsync(lease.Library, c => c.Files == 5 && c.Unhashed == 0);
        }
        finally
        {
            await notifications.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Connecting_names_the_account_and_each_new_refresh_token_is_kept()
    {
        var (_, drive) = await _fixture.UserWithBoxAsync();

        var identity = await ((OAuthStorageProvider)StorageProviders.Find(BoxProvider.Key)!).IdentifyAsync(_fixture.Http.CreateClient(BoxProvider.Key), "access", Ct);

        Assert.Equal(new StorageIdentity(FakeBox.UserId, "Box · Dana", null), identity);
        Assert.Equal("box-access", await _fixture.Tokens.AccessTokenAsync(drive.Id, Ct));
        // Box's refresh tokens work once, so the one handed back with the access token is the one to use next.
        var saved = await _fixture.Connections.FindAsync(drive.Id, Ct);
        Assert.Equal(_fixture.Box.LastRefreshToken, _fixture.Connections.RefreshToken(saved!));
    }

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
