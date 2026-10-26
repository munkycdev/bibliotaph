using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Tests;

public sealed class GoogleDriveTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly LibraryFixture _fixture;

    public GoogleDriveTests()
    {
        _fixture = new LibraryFixture(_folder.Path, TimeProvider.System);
        _fixture.Google.Add("RPG/Adventures/one.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 one"));
        _fixture.Google.Add("RPG/Adventures/one.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 another one"));
        _fixture.Google.Add("RPG/Adventures/two.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 two"));
        _fixture.Google.Add("RPG/Session notes", [], "application/vnd.google-apps.document");
        _fixture.Google.AddNamed("RPG", "Maps/Handouts.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 maps"));
    }

    public void Dispose()
    {
        _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _folder.Dispose();
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string At(string path) => StoragePath.Of(GoogleDriveProvider.Key, FakeGoogleDrive.PermissionId, path);

    async Task<UserDrives> DrivesAsync()
    {
        var (userId, _) = await _fixture.UserWithGoogleDriveAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        return drives;
    }

    [Fact]
    public async Task Listing_leaves_out_googles_own_documents_and_gives_every_file_a_path_of_its_own()
    {
        var listing = await new StorageSourceLister(await DrivesAsync()).ListAsync(At("RPG"), [], Ct);

        Assert.Equal(["Adventures/one (2).pdf", "Adventures/one.pdf", "Adventures/two.pdf", "Maps∕Handouts.pdf"],
            listing!.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(0, _fixture.Google.Downloads);
    }

    [Fact]
    public async Task A_file_is_read_by_its_path_whole_or_a_range_at_a_time()
    {
        var drives = await DrivesAsync();
        var reader = new StorageFileReader(drives, Path.Combine(_folder.Path, "downloads"), new Bibliotaph.Processing.SourceFileReader());
        var (drive, at) = drives.Locate(At("RPG/Adventures/one (2).pdf"))!.Value;

        using (var stream = reader.OpenRead(At("RPG/Adventures/one (2).pdf")))
            Assert.Equal("%PDF-1.7 another one", new StreamReader(stream).ReadToEnd());
        using (var stream = reader.OpenRead(At("RPG/Maps∕Handouts.pdf")))
            Assert.Equal("%PDF-1.7 maps", new StreamReader(stream).ReadToEnd());
        await using (var range = await drive.OpenReadAsync(at.Path, new ByteRange(9, 15), Ct))
            Assert.Equal("another", new StreamReader(range).ReadToEnd());

        Assert.False(drive.HasReadLinks);
        Assert.Throws<FileNotFoundException>(() => reader.OpenRead(At("RPG/Adventures/missing.pdf")));
        Assert.Null(await drive.ItemAsync("RPG/Nowhere/one.pdf", Ct));
        Assert.Null(await new StorageSourceLister(drives).ListAsync(At("RPG/Adventures/one.pdf"), [], Ct));
    }

    [Fact]
    public async Task A_new_book_is_found_when_the_drives_change_log_moves_on()
    {
        var (userId, _) = await _fixture.UserWithGoogleDriveAsync();
        var notifications = new DriveNotifications(_fixture.Connections, _fixture.Drives, _fixture.Libraries,
            Options.Create(new ServerOptions()), NullLogger<DriveNotifications>.Instance);
        await notifications.StartAsync(Ct);
        var changes = new GoogleDriveChanges(_fixture.Connections, _fixture.Tokens, _fixture.Http, notifications, TimeProvider.System,
            NullLogger<GoogleDriveChanges>.Instance);
        try
        {
            await using var lease = await _fixture.Libraries.OpenAsync(userId, Ct);
            var root = await lease.Library.Get<SourceRootStore>().AddAsync(At("RPG"), Ct);
            lease.Library.Indexing.RequestScan(root.Id);
            await WaitAsync(lease.Library, c => c.Files == 4 && c.Unhashed == 0);
            await changes.RoundAsync(Ct);

            _fixture.Google.Add("RPG/Adventures/three.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 three"));
            await changes.RoundAsync(Ct);

            await WaitAsync(lease.Library, c => c.Files == 5 && c.Unhashed == 0);
        }
        finally
        {
            await notifications.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Connecting_names_the_account_and_tokens_come_from_google()
    {
        var (_, drive) = await _fixture.UserWithGoogleDriveAsync();

        var identity = await StorageProviders.Find(GoogleDriveProvider.Key)!.IdentifyAsync(_fixture.Http.CreateClient("googledrive"), "access", Ct);

        Assert.Equal(new StorageIdentity(FakeGoogleDrive.PermissionId, "Google Drive · Dana", null), identity);
        Assert.Equal("google-access", await _fixture.Tokens.AccessTokenAsync(drive.Id, Ct));
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
