using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Lookups shared between a connected PC and the web library, so a book is looked up online once.</summary>
    public sealed class SharedLookupTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task A_book_another_device_looked_up_takes_what_it_found_and_only_looking_it_up_again_asks_online()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(_pdfs.KnownText, ct));
            _catalogue.Name = "RPGGeek or Open Library";
            _catalogue.FindsByTitleAlone = true;
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var (pc, _) = await ConnectPcAsync(web, ct);
            using (await web.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var book = await WaitAsync(async () => (await web.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 1 } one ? one[0] : null, ct);
            await WaitAsync(async () => (await web.GetFromJsonAsync<BookDetails>($"/api/library/books/{book.EntryId}", ct))!.Lookup is { Count: > 0 } facts ? facts : null, ct);
            var libraries = _factory.Services.GetRequiredService<UserLibraries>();
            string hash;
            await using (var lease = await libraries.OpenAsync(user.Id, ct))
            {
                hash = Assert.Single(await lease.Library.Get<LibraryStore>().GetDocumentHashesAsync(ct)).ContentHash;
                await lease.Library.Get<SharedResults>().SyncNowAsync(ct);
            }
            // What the web library asked, as the PC is given it.
            var first = (await pc.ReadResultsAsync(0, ct))!;
            var own = Assert.Single(first.Results, r => r.Kind == SharedResultKinds.Lookup);
            var asked = JsonSerializer.Deserialize<SharedLookup>(own.Content, JsonSerializerOptions.Web)!;

            // A library that looked its books up before lookups were shared sends them once, when it next passes them on.
            await using (var lease = await libraries.OpenAsync(user.Id, ct))
            {
                var cache = lease.Library.Get<Bibliotaph.Index.SharedResultCache>();
                await cache.DeleteStateAsync("lookup.own:" + hash, ct);
                await cache.DeleteStateAsync("lookup.backfilled", ct);
                await lease.Library.Get<SharedResults>().SyncNowAsync(ct);
                await lease.Library.Get<SharedResults>().SyncNowAsync(ct);
            }
            var earlier = Assert.Single((await pc.ReadResultsAsync(first.Last, ct))!.Results, r => r.Kind == SharedResultKinds.Lookup);
            var backfilled = JsonSerializer.Deserialize<SharedLookup>(earlier.Content, JsonSerializerOptions.Web)!;

            // The PC looks the same file up later and finds it on RPGGeek.
            var record = new BookRecord("RPGGeek", "7400", "", "Shared Tome", ["Ada Quill"], ["Example Press"], "2020");
            var found = new SharedResult(hash, SharedResultKinds.Lookup, own.PromptVersion, "lookup-on-the-pc", DateTime.UtcNow.AddMinutes(5),
                JsonSerializer.Serialize(asked with { Records = [new SharedLookupRecord(record, true)] }, JsonSerializerOptions.Web));
            Assert.True(await pc.WriteResultsAsync([found], ct));
            await using (var lease = await libraries.OpenAsync(user.Id, ct)) await lease.Library.Get<SharedResults>().SyncNowAsync(ct);
            var titlesBefore = _catalogue.Titles.Count;

            // Looked up again by itself, as when there is a catalogue more to ask, it takes the PC's lookup instead of asking.
            await using (var lease = await libraries.OpenAsync(user.Id, ct)) await lease.Library.Indexing.RerunAsync(Stage.LookUp, ct);
            var taken = await WaitAsync(async () => (await web.GetFromJsonAsync<BookDetails>($"/api/library/books/{book.EntryId}", ct))!
                .Lookup?.SingleOrDefault(f => f.Label == "Uses" && f.Value.Contains("Shared Tome", StringComparison.Ordinal)), ct);
            var titlesTaken = _catalogue.Titles.Count;

            // Looking every book up again asks online afresh, and what the catalogue says now replaces the PC's.
            using (await web.PostAsync("/api/processing/run-again/lookup", null, ct)) { }
            await WaitAsync(() => Task.FromResult(_catalogue.Titles.Count > titlesTaken ? "asked" : null), ct);
            var afresh = await WaitAsync(async () => (await web.GetFromJsonAsync<BookDetails>($"/api/library/books/{book.EntryId}", ct))!
                .Lookup?.SingleOrDefault(f => f.Label == "Result" && f.Value == BookLookups.NotFoundOnline), ct);

            Assert.Equal(("RPGGeek or Open Library", own.PromptVersion), (asked.AskedOf, Pipeline.Version(Stage.LookUp)));
            Assert.NotNull(asked.Search);
            Assert.Equal(asked.AskedOf, backfilled.AskedOf);
            Assert.Equal(asked.Search!.Titles, backfilled.Search!.Titles);
            Assert.Equal("RPGGeek: Shared Tome", taken.Value);
            Assert.Equal(titlesBefore, titlesTaken);
            Assert.NotNull(afresh);
        }
    }
}
