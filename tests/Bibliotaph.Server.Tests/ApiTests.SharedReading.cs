using System.Net;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>What a connected PC and the web library read, shared between them.</summary>
    public sealed class SharedReadingTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task A_connected_PC_shares_what_its_AI_read_and_the_web_library_gives_it_to_the_same_book()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(_pdfs.KnownText, ct));
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await web.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var book = await WaitAsync(async () => (await web.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 1 } one ? one[0] : null, ct);
            var libraries = _factory.Services.GetRequiredService<UserLibraries>();
            string hash;
            await using (var lease = await libraries.OpenAsync(user.Id, ct))
                hash = Assert.Single(await lease.Library.Get<LibraryStore>().GetDocumentHashesAsync(ct)).ContentHash;

            var path = Path.Combine(_folder.Path, "pc-catalog.db");
            var catalog = new CatalogDatabase(path, Path.Combine(_folder.Path, "pc-backups"));
            await using (var db = catalog.CreateContext()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
            var settings = new SettingsStore(new CatalogFile(path));
            await settings.SetAsync(SettingKeys.WebAddress, "http://localhost", ct);
            var key = new MemoryWebLibraryKey();
            var pc = new WebLibrary(new HttpClient(_factory.Server.CreateHandler()), key, settings, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WebLibrary>.Instance);
            var started = (await pc.StartAsync("DESKTOP-1", ct))!;
            var waiting = pc.WaitAsync(started, ct);
            (await web.PostAsync($"/api/devices/connect/{started.Code}", null, ct)).EnsureSuccessStatusCode();
            Assert.Equal(WebConnectOutcome.Connected, await waiting);

            // What the PC's AI read in its copy of the same file.
            var finished = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
            var reading = new SharedResult(hash, SharedResultKinds.Classification, ClassifierPrompt.Version, "run-on-the-pc", finished,
                JsonSerializer.Serialize(new SharedClassification("qwen3-8b", ClassifierPrompt.SchemaVersion, [0], finished.AddMinutes(-1),
                    [new SharedClaim("levels", "3", null, "characters of 3rd level", [0], false)]), JsonSerializerOptions.Web));
            var sent = await pc.WriteResultsAsync([reading], ct);
            // The run is recorded a moment before its suggestions are projected, so wait for what the user sees.
            var levels = await WaitAsync(async () => (await web.GetFromJsonAsync<BookDetails>($"/api/library/books/{book.EntryId}", ct))!
                .Fields.SingleOrDefault(f => f.Key == "levels" && f.Suggested), ct);
            bool recorded;
            await using (var lease = await libraries.OpenAsync(user.Id, ct))
                recorded = await lease.Library.Get<ClassificationStore>().HasSharedRunAsync(hash, ClassifierPrompt.Version, ct);

            // Sent again, or older, it isn't kept; the PC isn't given its own back; only a PC's key reaches the results.
            var keyed = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
            keyed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Find());
            keyed.DefaultRequestHeaders.Add(ApiHeaders.Client, "windows");
            using var again = await keyed.PostAsJsonAsync("/api/results", new SharedResultUpload([reading, reading with { Id = "older", FinishedUtc = finished.AddDays(-1) }]), ct);
            var kept = (await again.Content.ReadFromJsonAsync<SharedResultsKept>(ct))!;
            var page = (await pc.ReadResultsAsync(0, ct))!;
            using var session = await web.GetAsync("/api/results", ct);

            Assert.True(sent);
            Assert.True(recorded);
            Assert.Contains("3", levels.Display, StringComparison.Ordinal);
            Assert.Equal(0, kept.Kept);
            Assert.Empty(page.Results);
            Assert.False(page.More);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }

        [Fact]
        public async Task A_files_OCR_is_kept_for_any_device_to_ask_for_and_isnt_in_the_list_of_results()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var path = Path.Combine(_folder.Path, "pc-catalog.db");
            var catalog = new CatalogDatabase(path, Path.Combine(_folder.Path, "pc-backups"));
            await using (var db = catalog.CreateContext()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
            var settings = new SettingsStore(new CatalogFile(path));
            await settings.SetAsync(SettingKeys.WebAddress, "http://localhost", ct);
            var key = new MemoryWebLibraryKey();
            var pc = new WebLibrary(new HttpClient(_factory.Server.CreateHandler()), key, settings, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WebLibrary>.Instance);
            var started = (await pc.StartAsync("DESKTOP-1", ct))!;
            var waiting = pc.WaitAsync(started, ct);
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            (await web.PostAsync($"/api/devices/connect/{started.Code}", null, ct)).EnsureSuccessStatusCode();
            Assert.Equal(WebConnectOutcome.Connected, await waiting);
            var hash = new string('a', 64);
            var ocr = new SharedResult(hash, SharedResultKinds.PageText, 1, $"{hash}@pc", new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
                JsonSerializer.Serialize(new SharedOcr([new SharedOcrPage(0, "The warrens of the goblin king", 0.9, ["warrens"], [72, 700, 140, 688])]), JsonSerializerOptions.Web));

            var sent = await pc.WriteResultsAsync([ocr], ct);
            var back = await pc.ReadResultAsync(SharedResultKinds.PageText, hash, ct);
            var missing = await pc.ReadResultAsync(SharedResultKinds.PageText, new string('b', 64), ct);
            var notAsked = await pc.ReadResultAsync(SharedResultKinds.Classification, hash, ct);
            var listed = (await pc.ReadResultsAsync(0, ct))!;

            Assert.True(sent);
            Assert.Equal(ocr.Content, back?.Content);
            Assert.Null(missing);
            Assert.Null(notAsked);
            // Counted, so the list moves past it, but not handed out in it.
            Assert.Empty(listed.Results);
            Assert.True(listed.Last > 0);
        }

        [Fact]
        public async Task A_connected_PC_lists_the_web_librarys_books_and_takes_what_the_server_read_from_them()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/Adventures/known.pdf", await File.ReadAllBytesAsync(_pdfs.KnownText, ct));
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await web.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var (pc, key) = await ConnectPcAsync(web, ct);
            var hash = await WaitAsync(async () => (await pc.ReadShelfAsync(null, ct)).Shelf?.Folders.SelectMany(f => f.Files).FirstOrDefault()?.ContentHash, ct);

            var shelf = (await pc.ReadShelfAsync(null, ct)).Shelf!;
            var same = await pc.ReadShelfAsync(shelf.Version, ct);
            var reading = await WaitAsync(async () => await pc.ReadReadingAsync(hash, ct) is { Reading: { Cover: not null } r } ? r : null, ct);
            var cover = await pc.ReadCoverAsync(reading.Cover!, ct);
            var notACover = await pc.ReadCoverAsync("..%2Fcatalog.db", ct);
            var unknown = await pc.ReadReadingAsync(new string('b', 64), ct);
            var keyed = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
            keyed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var file = await keyed.GetAsync($"/api/pc/books/{hash}/file", ct);
            using var session = await web.GetAsync("/api/pc/library", ct);

            var folder = Assert.Single(shelf.Folders);
            Assert.Equal(("RPG", "OneDrive · Dana › RPG"), (folder.Name, folder.Location));
            Assert.Equal("Adventures/known.pdf", Assert.Single(folder.Files).Path);
            Assert.Equal((true, null), (same.Reached, same.Shelf));
            Assert.Equal("Complete", reading.TextStatus);
            Assert.Contains(reading.Pages, p => !string.IsNullOrWhiteSpace(p.Text));
            Assert.Equal(reading.PageCount, reading.Pages.Count);
            Assert.NotEmpty(cover!);
            Assert.Null(notACover);
            Assert.Equal(WebReading.NotYet, unknown);
            // OneDrive gives a link anyone may read, so the PC downloads from there, not through the server.
            Assert.Equal(HttpStatusCode.Redirect, file.StatusCode);
            Assert.StartsWith("https://public.files.1drv.com/", file.Headers.Location!.ToString(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }
    }
}
