using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;

namespace Bibliotaph.Server.Tests;

public sealed class DropboxTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly LibraryFixture _fixture;

    public DropboxTests()
    {
        _fixture = new LibraryFixture(_folder.Path, TimeProvider.System);
        _fixture.Dropbox.Add("RPG/Adventures/one.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 one"));
        _fixture.Dropbox.Add("RPG/Adventures/two.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 two"));
        _fixture.Dropbox.Add("RPG/Adventures/three.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 three"));
        _fixture.Dropbox.Add("RPG/Maps/map.png", [0x89, 0x50, 0x4E, 0x47]);
        _fixture.Dropbox.Add("RPG/notes.txt", Encoding.ASCII.GetBytes("notes"));
    }

    public void Dispose()
    {
        _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _folder.Dispose();
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string At(string path) => StoragePath.Of(DropboxProvider.Key, FakeDropbox.AccountId, path);

    [Fact]
    public async Task Listing_follows_every_page_of_every_folder_but_leaves_nested_library_folders_to_themselves()
    {
        var (userId, _) = await _fixture.UserWithDropboxAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);

        var listing = await new StorageSourceLister(drives).ListAsync(At("RPG"), [At("RPG/Maps")], Ct);

        Assert.Equal(["Adventures/one.pdf", "Adventures/three.pdf", "Adventures/two.pdf", "notes.txt"], listing!.Files.Select(f => f.RelativePath).Order());
        Assert.True(_fixture.Dropbox.Pages > 2);
        Assert.Equal(0, _fixture.Dropbox.Downloads);
        Assert.Null(await new StorageSourceLister(drives).ListAsync(At("Gone"), [], Ct));
    }

    [Fact]
    public async Task A_book_with_a_name_beyond_ascii_downloads_with_its_path_escaped_in_the_header()
    {
        _fixture.Dropbox.Add("RPG/Château.pdf", Encoding.UTF8.GetBytes("%PDF-1.7 château"));
        var (userId, _) = await _fixture.UserWithDropboxAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        var reader = new StorageFileReader(drives, Path.Combine(_folder.Path, "downloads"), new Bibliotaph.Processing.SourceFileReader());

        using (var stream = reader.OpenRead(At("RPG/Château.pdf")))
            Assert.Equal("%PDF-1.7 château", new StreamReader(stream).ReadToEnd());

        var header = Assert.Single(_fixture.Dropbox.Headers);
        Assert.True(header.All(char.IsAscii));
        Assert.Contains("\\u00E2", header, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<FileNotFoundException>(() => reader.OpenRead(At("RPG/missing.pdf")));
    }

    [Fact]
    public async Task A_range_is_read_without_the_rest_of_the_file()
    {
        var (userId, _) = await _fixture.UserWithDropboxAsync();
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        var (drive, at) = drives.Locate(At("RPG/Adventures/one.pdf"))!.Value;

        await using var stream = await drive.OpenReadAsync(at.Path, new ByteRange(2, 4), Ct);

        Assert.Equal("DF-", new StreamReader(stream).ReadToEnd());
        Assert.False(drive.HasReadLinks);
        Assert.False((await drive.ItemAsync(at.Path, Ct))!.IsFolder);
        Assert.Null(await drive.ItemAsync("RPG/missing.pdf", Ct));
    }

    [Fact]
    public async Task A_team_account_reads_from_the_team_space()
    {
        var (userId, _) = await _fixture.UserWithDropboxAsync("team:4242");
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);

        await new StorageSourceLister(drives).ListAsync(At("RPG"), [], Ct);

        Assert.Contains("""{".tag":"root","root":"4242"}""", _fixture.Dropbox.Headers);
    }

    [Fact]
    public async Task A_library_folder_on_dropbox_is_scanned_and_found_again_when_dropbox_says_it_changed()
    {
        var (userId, _) = await _fixture.UserWithDropboxAsync();
        await using var lease = await _fixture.Libraries.OpenAsync(userId, Ct);
        var root = await lease.Library.Get<SourceRootStore>().AddAsync(At("RPG"), Ct);
        lease.Library.Indexing.RequestScan(root.Id);
        await WaitAsync(lease.Library, c => c.Files == 4 && c.Unhashed == 0);

        _fixture.Dropbox.Add("RPG/Adventures/four.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 four"));
        await _fixture.Libraries.DriveChangedAsync(userId, DropboxProvider.Key, FakeDropbox.AccountId, Ct);

        await WaitAsync(lease.Library, c => c.Files == 5 && c.Unhashed == 0);
    }

    [Fact]
    public async Task Connecting_reads_who_the_account_is_and_tokens_come_from_dropbox()
    {
        var (_, drive) = await _fixture.UserWithDropboxAsync();
        var http = new FakeHttp(new FakeHosts(_fixture.Graph, _fixture.Dropbox)).CreateClient("dropbox");

        var identity = await StorageProviders.Find(DropboxProvider.Key)!.IdentifyAsync(http, "access", Ct);

        Assert.Equal(new StorageIdentity(FakeDropbox.AccountId, "Dropbox · Dana", "personal"), identity);
        Assert.Equal("dropbox-access", await _fixture.Tokens.AccessTokenAsync(drive.Id, Ct));
    }

    [Fact]
    public void A_webhook_notice_is_believed_only_with_the_apps_signature()
    {
        var body = Encoding.UTF8.GetBytes($$$"""{"list_folder":{"accounts":["{{{FakeDropbox.AccountId}}}"]},"delta":{"users":[12345]}}""");
        var signature = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body));

        Assert.True(DropboxWebhook.Signed(body, signature, "secret"));
        Assert.False(DropboxWebhook.Signed(body, signature, "another"));
        Assert.False(DropboxWebhook.Signed(body, "not hex", "secret"));
        Assert.Equal([FakeDropbox.AccountId], DropboxWebhook.Accounts(body));
        Assert.Empty(DropboxWebhook.Accounts(Encoding.UTF8.GetBytes("not json")));
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
