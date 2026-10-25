using Bibliotaph.Catalog.Entities;
using Bibliotaph.Server.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Server.Tests;

public sealed class UserLibrariesTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    readonly UserLibraries _libraries;

    static readonly string Alice = new('a', 32);
    static readonly string Bob = new('b', 32);

    public UserLibrariesTests() => _libraries = new UserLibraries(new ServerPaths(_folder.Path), new NoRestore(), _clock, NullLoggerFactory.Instance);

    public void Dispose()
    {
        _libraries.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _folder.Dispose();
    }

    [Fact]
    public async Task A_first_open_makes_the_users_folder_with_both_databases()
    {
        await using var lease = await _libraries.OpenAsync(Alice);

        Assert.True(File.Exists(Path.Combine(_folder.Path, "users", Alice, "catalog.db")));
        Assert.True(File.Exists(Path.Combine(_folder.Path, "users", Alice, "index.db")));
        await using var db = lease.Library.Catalog.CreateContext();
        Assert.Equal(0, await db.Entries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_user_has_their_own_files()
    {
        await using (var alice = await _libraries.OpenAsync(Alice))
        {
            await using var db = alice.Library.Catalog.CreateContext();
            db.SourceRoots.Add(new SourceRoot { Path = "/onedrive/drive/Books", AddedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var bob = await _libraries.OpenAsync(Bob);
        await using var bobs = bob.Library.Catalog.CreateContext();
        Assert.Equal(0, await bobs.SourceRoots.CountAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(bob.Library.Paths.Root, Path.Combine(_folder.Path, "users", Alice));
    }

    [Fact]
    public async Task Two_requests_share_one_open_library()
    {
        await using var first = await _libraries.OpenAsync(Alice);
        await using var second = await _libraries.OpenAsync(Alice);

        Assert.Same(first.Library, second.Library);
        Assert.Equal(1, _libraries.OpenCount);
    }

    [Fact]
    public async Task An_idle_library_is_closed_and_opens_again_on_the_next_request()
    {
        await (await _libraries.OpenAsync(Alice)).DisposeAsync();
        _clock.Advance(TimeSpan.FromMinutes(21));

        Assert.Equal(1, await _libraries.CloseIdleAsync(TimeSpan.FromMinutes(20)));
        Assert.Equal(0, _libraries.OpenCount);

        await using var again = await _libraries.OpenAsync(Alice);
        await using var db = again.Library.Catalog.CreateContext();
        Assert.Equal(0, await db.Entries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_library_in_use_or_used_recently_stays_open()
    {
        await using var held = await _libraries.OpenAsync(Alice);
        await (await _libraries.OpenAsync(Bob)).DisposeAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, await _libraries.CloseIdleAsync(TimeSpan.FromMinutes(20)));
        _clock.Advance(TimeSpan.FromMinutes(30));

        // Bob's is idle now; Alice's is still held.
        Assert.Equal(1, await _libraries.CloseIdleAsync(TimeSpan.FromMinutes(20)));
        Assert.Equal(1, _libraries.OpenCount);
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFAB")]
    [InlineData("")]
    public async Task Anything_but_a_user_id_is_refused_before_it_reaches_a_path(string id) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _libraries.OpenAsync(id));
}
