using System.Net;
using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Tests;

public sealed class WebDavTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly LibraryFixture _fixture;

    public WebDavTests()
    {
        _fixture = new LibraryFixture(_folder.Path, TimeProvider.System);
        _fixture.WebDav.Add("RPG/Adventures/one.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 one"));
        _fixture.WebDav.Add("RPG/Adventures/two and a half.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 two and a half"));
        _fixture.WebDav.Add("RPG/Maps\\Handouts.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 maps"));
    }

    public void Dispose()
    {
        _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _folder.Dispose();
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string At(string path) => StoragePath.Of(WebDavProvider.Key, FakeWebDav.AccountId, path);

    async Task<UserDrives> DrivesAsync(string password = FakeWebDav.Password)
    {
        var (userId, _) = await _fixture.UserWithWebDavAsync(password);
        var drives = _fixture.Drives.For(userId);
        await drives.ReloadAsync(Ct);
        return drives;
    }

    [Fact]
    public async Task Listing_finds_every_file_with_a_backslash_read_as_a_name()
    {
        var listing = await new StorageSourceLister(await DrivesAsync()).ListAsync(At("RPG"), [], Ct);

        Assert.Equal(["Adventures/one.pdf", "Adventures/two and a half.pdf", "Maps∖Handouts.pdf"],
            listing!.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(0, _fixture.WebDav.Downloads);
    }

    [Fact]
    public async Task A_file_is_read_by_its_path_whole_or_a_range_at_a_time()
    {
        var drives = await DrivesAsync();
        var reader = new StorageFileReader(drives, Path.Combine(_folder.Path, "downloads"), new Bibliotaph.Processing.SourceFileReader());
        var (drive, at) = drives.Locate(At("RPG/Adventures/two and a half.pdf"))!.Value;

        using (var stream = reader.OpenRead(At("RPG/Adventures/two and a half.pdf")))
            Assert.Equal("%PDF-1.7 two and a half", new StreamReader(stream).ReadToEnd());
        using (var stream = reader.OpenRead(At("RPG/Maps∖Handouts.pdf")))
            Assert.Equal("%PDF-1.7 maps", new StreamReader(stream).ReadToEnd());
        await using (var range = await drive.OpenReadAsync(at.Path, new ByteRange(9, 11), Ct))
            Assert.Equal("two", new StreamReader(range).ReadToEnd());

        Assert.False(drive.HasReadLinks);
        // A folder asked for without its slash is redirected on the same server, which is followed.
        Assert.True((await drive.ItemAsync("RPG/Adventures", Ct))!.IsFolder);
        Assert.Throws<FileNotFoundException>(() => reader.OpenRead(At("RPG/Adventures/missing.pdf")));
        Assert.Null(await drive.ItemAsync("RPG/Nowhere/one.pdf", Ct));
        Assert.Null(await new StorageSourceLister(drives).ListAsync(At("RPG/Adventures/one.pdf"), [], Ct));
    }

    [Fact]
    public async Task A_refused_password_asks_for_the_account_to_be_connected_again()
    {
        var drives = await DrivesAsync(password: "changed");
        var drive = drives.Find(WebDavProvider.Key, FakeWebDav.AccountId)!;

        var refused = await Assert.ThrowsAsync<StorageException>(() => drive.ChildrenAsync("RPG", Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
        Assert.True(Assert.Single(await _fixture.Connections.ListAsync(drives.UserId, Ct)).NeedsReconnect);
    }

    [Fact]
    public async Task A_new_book_is_found_when_the_folders_etag_changes()
    {
        var (userId, _) = await _fixture.UserWithWebDavAsync();
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
            await WaitAsync(lease.Library, c => c.Files == 3 && c.Unhashed == 0);
            await changes.RoundAsync(Ct);

            _fixture.WebDav.Add("RPG/Adventures/three.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 three"));
            await changes.RoundAsync(Ct);

            await WaitAsync(lease.Library, c => c.Files == 4 && c.Unhashed == 0);
        }
        finally
        {
            await notifications.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Connecting_checks_the_folder_and_names_the_account_by_address_and_user()
    {
        var http = _fixture.Http.CreateClient(WebDavProvider.Key);

        var connected = await WebDavProvider.ConnectAsync(http, "dav.example.com/remote.php/dav/files/dana", FakeWebDav.User, FakeWebDav.Password, Ct);
        var again = await WebDavProvider.ConnectAsync(http, FakeWebDav.Root, FakeWebDav.User, FakeWebDav.Password, Ct);

        Assert.Null(connected.Problem);
        Assert.Equal(FakeWebDav.Root, connected.Credential!.Url);
        Assert.Equal("WebDAV · dana on dav.example.com", connected.Identity!.DisplayName);
        Assert.Equal(connected.Identity.AccountId, again.Identity!.AccountId);
        Assert.DoesNotContain('/', connected.Identity.AccountId);
    }

    [Theory]
    [InlineData("http://dav.example.com/remote.php/dav/files/dana/", FakeWebDav.Password, "over https")]
    [InlineData("https://192.168.1.20/dav/", FakeWebDav.Password, "private network")]
    [InlineData("https://localhost/dav/", FakeWebDav.Password, "private network")]
    [InlineData(FakeWebDav.Root, "wrong", "didn't accept")]
    [InlineData(FakeWebDav.Root + "RPG/Adventures/one.pdf", FakeWebDav.Password, "is a file")]
    [InlineData(FakeWebDav.Root + "Nowhere/", FakeWebDav.Password, "no folder")]
    public async Task Connecting_says_why_an_address_or_password_cant_be_used(string address, string password, string problem)
    {
        var found = await WebDavProvider.ConnectAsync(_fixture.Http.CreateClient(WebDavProvider.Key), address, FakeWebDav.User, password, Ct);

        Assert.Null(found.Identity);
        Assert.Contains(problem, found.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("168.63.129.16", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2002:c0a8:0101::1", false)]
    [InlineData("fd00::1", false)]
    public void Only_public_addresses_are_reached(string address, bool reached) =>
        Assert.Equal(reached, PublicNetwork.IsPublic(IPAddress.Parse(address)));

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
