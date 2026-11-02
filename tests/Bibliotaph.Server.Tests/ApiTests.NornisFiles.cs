using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Storage;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WorldEvent = Bibliotaph.Api.Contracts.NornisWorldEvent;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Each world's files in Nornis as a library folder whose books join the world's collection.</summary>
    public sealed partial class NornisTests
    {
        /// <summary>The library's books in a collection, by the names of their files.</summary>
        static async Task<IReadOnlyList<string>> BooksInAsync(UserLibrary library, long collectionId)
        {
            var store = library.Get<LibraryStore>();
            var paths = await store.GetRelativePathsAsync(Ct);
            var entries = await library.Get<LibraryQueries>().ListAsync(new LibraryFilter(await store.GetVisibleEntryIdsAsync(ct: Ct)), ct: Ct);
            var names = new List<string>();
            foreach (var entry in entries)
                if ((await library.Get<CollectionsService>().GetForEntryAsync(entry.EntryId, Ct)).Contains(collectionId))
                    names.Add(Path.GetFileName(paths.First(p => p.DocumentId == entry.DocumentId).RelativePath));
            return [.. names.Order(StringComparer.Ordinal)];
        }

        [Fact]
        public async Task A_world_s_files_are_a_library_folder_whose_books_join_its_collection_and_a_change_scans_it_again()
        {
            _nornis.AddWorld(Ashes.ToString(), "Ashes");
            _nornis.AddFile(Ashes.ToString(), "file-1", "Crown.pdf", await File.ReadAllBytesAsync(_pdfs.ChapterBook, Ct));
            using var server = Server();
            var (user, _) = await GameMasterAsync(server);
            var libraries = server.Services.GetRequiredService<UserLibraries>();
            await using var lease = await libraries.OpenAsync(user.Id, Ct);
            var browser = Browser(server, user);

            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 0));
            var ashes = Assert.Single(await WorldCollectionsAsync(lease.Library)).Id;
            var first = await WaitAsync(async () => await BooksInAsync(lease.Library, ashes) is { Count: 1 } books ? books : null, Ct);
            _nornis.AddFile(Ashes.ToString(), "file-2", "Known.pdf", await File.ReadAllBytesAsync(_pdfs.KnownText, Ct));
            await SendAsync(server, Event(WorldEvent.FilesChanged, Ashes, "Ashes", 5));
            var both = await WaitAsync(async () => await BooksInAsync(lease.Library, ashes) is { Count: 2 } books ? books : null, Ct);
            await SendAsync(server, Event(WorldEvent.Renamed, Ashes, "Cinders", 10));
            var folder = Assert.Single((await browser.GetFromJsonAsync<List<LibraryFolder>>("/api/library/folders", Ct))!);

            Assert.Equal(["Crown.pdf"], first);
            Assert.Equal(["Crown.pdf", "Known.pdf"], both);
            Assert.Equal(("Cinders", "Nornis · Dana › Cinders"), (folder.Name, folder.Location));
            Assert.Equal(StoragePath.Of(NornisProvider.Key, FakeNornis.UserId, Ashes.ToString()),
                Assert.Single(await lease.Library.Get<SourceRootStore>().ListAsync(Ct)).Path);
        }

        [Fact]
        public async Task A_world_s_folder_the_game_master_removed_stays_removed()
        {
            _nornis.AddWorld(Ashes.ToString(), "Ashes");
            using var server = Server();
            var (user, _) = await GameMasterAsync(server);
            await using var lease = await server.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, Ct);
            var browser = Browser(server, user);
            var roots = lease.Library.Get<SourceRootStore>();

            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 0));
            var added = Assert.Single(await roots.ListAsync(Ct));
            (await browser.DeleteAsync($"/api/library/folders/{added.Id}", Ct)).Dispose();
            await SendAsync(server, Event(WorldEvent.Renamed, Ashes, "Cinders", 5), Event(WorldEvent.Created, Ashes, "Cinders", 10),
                Event(WorldEvent.FilesChanged, Ashes, "Cinders", 15));

            Assert.Empty(await roots.ListAsync(Ct));
            Assert.Equal("Cinders", Assert.Single(await WorldCollectionsAsync(lease.Library)).Name);
        }

        [Fact]
        public async Task The_page_s_policy_lets_the_reader_read_Nornis_s_files_only_from_its_blob_host()
        {
            using var server = Server();
            using var withHost = _factory.WithWebHostBuilder(host => host.UseSetting("Storage:Nornis:BlobHost", "Files.Nornis.Test"));
            using var notAHost = _factory.WithWebHostBuilder(host => host.UseSetting("Storage:Nornis:BlobHost", "files.nornis.test/x *"));

            static async Task<string> ConnectAsync(WebApplicationFactory<Program> app)
            {
                using var response = await app.CreateClient().GetAsync("/", Ct);
                var policy = response.Headers.GetValues("Content-Security-Policy").Single();
                return policy.Split(';').Select(p => p.Trim()).Single(p => p.StartsWith("connect-src", StringComparison.Ordinal));
            }

            Assert.EndsWith("https://*.microsoftpersonalcontent.com https://files.nornis.test", await ConnectAsync(withHost), StringComparison.Ordinal);
            Assert.EndsWith("https://*.microsoftpersonalcontent.com", await ConnectAsync(notAHost), StringComparison.Ordinal);
            Assert.EndsWith("https://*.microsoftpersonalcontent.com", await ConnectAsync(server), StringComparison.Ordinal);
        }
    }
}
