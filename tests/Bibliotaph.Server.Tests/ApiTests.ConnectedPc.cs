using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Connecting a Windows PC, and the two merging each other's work.</summary>
    public sealed class ConnectedPcTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task A_Windows_PC_connects_once_the_user_approves_it_and_stops_when_disconnected_on_the_web_or_in_the_app()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var catalog = new CatalogDatabase(Path.Combine(_folder.Path, "pc-catalog.db"), Path.Combine(_folder.Path, "pc-backups"));
            await using (var db = catalog.CreateContext()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
            var settings = new SettingsStore(new CatalogFile(catalog.FilePath));
            await settings.SetAsync(SettingKeys.WebAddress, "http://localhost", ct);
            WebLibrary Pc(IWebLibraryKey? key = null) => new(new HttpClient(_factory.Server.CreateHandler()), key ?? new MemoryWebLibraryKey(), settings,
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebLibrary>.Instance);

            var pcKey = new MemoryWebLibraryKey();
            var pc = Pc(pcKey);
            var started = (await pc.StartAsync("Dana's  PC", ct))!;
            Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", started.Code);
            Assert.EndsWith($"/connect?code={started.Code}", started.ApproveUrl, StringComparison.Ordinal);
            var shown = await web.GetFromJsonAsync<DeviceConnectRequest>($"/api/devices/connect/{started.Code.Replace("-", "").ToLowerInvariant()}", ct);
            Assert.Equal("Dana's PC", shown!.Name);
            var waiting = pc.WaitAsync(started, ct);
            (await web.PostAsync($"/api/devices/connect/{started.Code}", null, ct)).EnsureSuccessStatusCode();

            Assert.Equal(WebConnectOutcome.Connected, await waiting);
            Assert.Equal(new WebLibraryStatus(WebLibraryState.Connected, "Dana", "Dana's PC"), await pc.CheckAsync(ct));
            var device = Assert.Single((await web.GetFromJsonAsync<List<ConnectedDevice>>("/api/devices", ct))!);
            Assert.Equal("Dana's PC", device.Name);
            using (var asked = await web.PostAsync($"/api/devices/connect/{started.Code}", null, ct))
                Assert.Equal(HttpStatusCode.NotFound, asked.StatusCode);
            // A PC's key reaches only what a PC needs, not the rest of the library's API.
            var keyed = Client();
            keyed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pcKey.Find());
            Assert.StartsWith("btd_", pcKey.Find(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyed.GetAsync("/api/library/summary", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyed.GetAsync("/api/devices", ct)).StatusCode);
            (await keyed.GetAsync("/api/devices/me", ct)).EnsureSuccessStatusCode();

            var declined = Pc();
            var again = (await declined.StartAsync("Another PC", ct))!;
            var answer = declined.WaitAsync(again, ct);
            (await web.DeleteAsync($"/api/devices/connect/{again.Code}", ct)).EnsureSuccessStatusCode();
            Assert.Equal(WebConnectOutcome.Declined, await answer);
            Assert.False(declined.HasKey);

            (await web.DeleteAsync($"/api/devices/{device.Id}", ct)).EnsureSuccessStatusCode();
            Assert.Equal(WebLibraryState.Disconnected, (await pc.CheckAsync(ct)).State);
            Assert.False(pc.HasKey);

            var third = Pc();
            var asking = (await third.StartAsync("Dana's PC", ct))!;
            var approved = third.WaitAsync(asking, ct);
            (await web.PostAsync($"/api/devices/connect/{asking.Code}", null, ct)).EnsureSuccessStatusCode();
            Assert.Equal(WebConnectOutcome.Connected, await approved);
            await third.DisconnectAsync(ct);
            Assert.Empty((await web.GetFromJsonAsync<List<ConnectedDevice>>("/api/devices", ct))!);
            Assert.Equal(WebLibraryState.NotConnected, (await third.CheckAsync(ct)).State);
        }

        [Fact]
        public async Task A_connected_PC_and_the_web_library_merge_each_others_work_through_the_API()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var path = Path.Combine(_folder.Path, "pc-catalog.db");
            var catalog = new CatalogDatabase(path, Path.Combine(_folder.Path, "pc-backups"));
            await using (var db = catalog.CreateContext()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
            var contexts = new CatalogFile(path);
            var settings = new SettingsStore(contexts);
            await settings.SetAsync(SettingKeys.WebAddress, "http://localhost", ct);
            var pcWeb = new WebLibrary(new HttpClient(_factory.Server.CreateHandler()), new MemoryWebLibraryKey(), settings, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WebLibrary>.Instance);
            var started = (await pcWeb.StartAsync("DESKTOP-1", ct))!;
            var waiting = pcWeb.WaitAsync(started, ct);
            (await web.PostAsync($"/api/devices/connect/{started.Code}", null, ct)).EnsureSuccessStatusCode();
            Assert.Equal(WebConnectOutcome.Connected, await waiting);
            using var pc = new LibrarySync(new LibrarySyncStore(contexts, new MetadataStore(contexts), new VocabularyStore(contexts)), settings,
                machineName: "DESKTOP-1", channels: [pcWeb]);

            // Work done on the PC reaches the web library when the PC's file is sent.
            await new CollectionStore(contexts).CreateAsync("Saltmarsh campaign", ct: ct);
            var sent = await pc.SyncNowAsync(ct);
            Assert.True(sent.Wrote);
            Assert.Contains((await web.GetFromJsonAsync<CollectionList>("/api/collections", ct))!.Tree, c => c.Name == "Saltmarsh campaign");

            // Work done on the web reaches the PC once the web library has written its own file.
            (await web.PostAsJsonAsync("/api/collections", new CollectionName("Rime of the Frostmaiden", null), ct)).EnsureSuccessStatusCode();
            var merged = await WaitAsync(async () =>
            {
                if (!await pcWeb.HasChangesAsync(ct)) return null;
                var pass = await pc.SyncNowAsync(ct);
                await using var db = contexts.CreateDbContext();
                return db.Collections.Any(c => c.Name == "Rime of the Frostmaiden") ? pass : null;
            }, ct);
            Assert.Contains(SyncFiles.WebMachine, merged.MergedFrom);

            // Only a PC's key reaches the sync files, and a disconnected PC's file goes.
            Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync("/api/sync", ct)).StatusCode);
            var device = Assert.Single((await web.GetFromJsonAsync<List<ConnectedDevice>>("/api/devices", ct))!);
            (await web.DeleteAsync($"/api/devices/{device.Id}", ct)).EnsureSuccessStatusCode();
            var files = await _factory.Services.GetRequiredService<UserLibraries>().SyncFilesAsync(user.Id, ct);
            Assert.Equal([SyncFiles.WebDevice], (await files.ListAsync(ct)).Select(f => f.Device));
            Assert.StartsWith("This PC was disconnected", (await pcWeb.ReadPeersAsync(ct)).Problem, StringComparison.Ordinal);
            Assert.False(pcWeb.IsOn);
        }

        [Fact]
        public async Task What_a_connected_PCs_AI_is_doing_shows_in_processing_naming_only_the_web_librarys_books()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(_pdfs.KnownText, ct));
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await web.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var (pc, key) = await ConnectPcAsync(web, ct);
            var hash = await WaitAsync(async () => (await pc.ReadShelfAsync(null, ct)).Shelf?.Folders.SelectMany(f => f.Files).FirstOrDefault()?.ContentHash, ct);
            var title = await WaitAsync(async () => (await web.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 1 } one ? one[0].Title : null, ct);
            async Task<IReadOnlyList<PcProcessing>> PcsAsync() => (await web.GetFromJsonAsync<ProcessingState>("/api/processing", ct))!.Pcs!;

            var before = await PcsAsync();
            var said = await pc.ReportActivityAsync(new PcActivity("Classify", hash, false, null, 40, 260, 0, 12), ct);
            var cataloguing = await PcsAsync();
            // A book only the PC has is never named.
            await pc.ReportActivityAsync(new PcActivity("ChapterScan", new string('b', 64), false, null, 300, 0, 3, 9), ct);
            var scanning = await PcsAsync();
            var keyed = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
            keyed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var notAStep = await keyed.PutAsJsonAsync("/api/pc/activity", new PcActivity("Probe", null, false, null, 0, 1, 0, 0), ct);
            using var session = await web.PutAsJsonAsync("/api/pc/activity", new PcActivity(null, null, false, null, 0, 1, 0, 0), ct);
            await pc.ReportActivityAsync(null, ct);
            var done = await PcsAsync();

            Assert.True(said);
            Assert.Empty(before);
            var shown = Assert.Single(cataloguing);
            Assert.Equal(("DESKTOP-1", "Classify", $"Asking the AI about {title}"), (shown.Name, shown.Step, shown.Doing));
            Assert.Equal((40L, 260L, 0L, 12L), (shown.Classified, shown.ToClassify, shown.ScannedWhole, shown.ToScanWhole));
            Assert.Equal("Scanning the chapters of one of its own books", Assert.Single(scanning).Doing);
            Assert.Equal(HttpStatusCode.BadRequest, notAStep.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
            Assert.Empty(done);
        }

        [Fact]
        public async Task A_connected_PC_downloads_a_book_from_storage_without_read_links_through_the_server()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId, "Dropbox · Dana", "personal", "refresh", null, ct);
            var bytes = await File.ReadAllBytesAsync(_pdfs.KnownText, ct);
            _dropbox.Add("RPG/known.pdf", bytes);
            var web = Client(user);
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await web.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var (pc, key) = await ConnectPcAsync(web, ct);
            var hash = await WaitAsync(async () => (await pc.ReadShelfAsync(null, ct)).Shelf?.Folders.SelectMany(f => f.Files).FirstOrDefault()?.ContentHash, ct);
            var keyed = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
            keyed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

            using var file = await keyed.GetAsync($"/api/pc/books/{hash}/file", ct);
            using var missing = await keyed.GetAsync($"/api/pc/books/{new string('b', 64)}/file", ct);

            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        [Fact]
        public async Task A_connected_PC_looks_its_books_up_where_the_server_can_and_falls_back_to_its_own_catalogue_once_disconnected()
        {
            var ct = TestContext.Current.CancellationToken;
            var web = Client(await AddUserAsync(admin: false));
            web.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var (pc, key) = await ConnectPcAsync(web, ct);
            var path = Path.Combine(_folder.Path, "pc-lookups.db");
            await using (var db = new CatalogDatabase(path, Path.Combine(_folder.Path, "pc-backups")).CreateContext())
                await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
            var settings = new SettingsStore(new CatalogFile(path));
            var direct = new ScriptedCatalogue { Name = "Open Library" };
            var catalogue = new WebLibraryCatalogue(pc, direct, settings);
            var changed = 0;
            catalogue.NameChanged += (_, _) => changed++;
            _catalogue.Record = new BookRecord("Google Books", "vol1", "9781234567897", "The Owlbear Mill", ["Ada Quill"], ["Gull Rock Games"], "2019");

            // Where it asks is learned before anything is looked up, so books known nowhere can be asked again.
            Assert.Equal("Open Library", catalogue.Name);
            await catalogue.RefreshAsync(ct);
            var record = await catalogue.FindAsync(["9781234567897"], ct);
            _catalogue.Busy = true;
            var busy = await Assert.ThrowsAsync<CatalogueUnavailableException>(() => catalogue.FindAsync(["9781234567897"], ct));
            var keyed = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
            keyed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            keyed.DefaultRequestHeaders.Add(ApiHeaders.Client, "windows");
            using var notAnIsbn = await keyed.PostAsJsonAsync("/api/pc/lookups", new PcLookupRequest(["978-1-234"]), ct);
            using var nothing = await keyed.PostAsJsonAsync("/api/pc/lookups", new PcLookupRequest([]), ct);
            // A book printing only a product code is looked up by it, where a catalogue's records list codes.
            using var byCode = await keyed.PostAsJsonAsync("/api/pc/lookups", new PcLookupRequest([], ["The Owlbear Mill"], ["pzo1110"]), ct);
            // And one printing neither, by its title alone.
            using var byTitle = await keyed.PostAsJsonAsync("/api/pc/lookups", new PcLookupRequest([], ["The Owlbear Mill"]), ct);
            using var signedIn = await web.GetAsync("/api/pc/lookups", ct);

            Assert.Equal(("Open Library or Google Books", 1), (catalogue.Name, changed));
            Assert.Equal(("Google Books", "The Owlbear Mill", "Gull Rock Games"), (record!.Source, record.Title, record.Publishers[0]));
            Assert.Equal(["9781234567897"], _catalogue.Asked);
            Assert.Equal("Google Books is busy.", busy.Message);
            Assert.Equal(HttpStatusCode.BadRequest, notAnIsbn.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, byCode.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, byTitle.StatusCode);
            Assert.Equal(["PZO1110"], _catalogue.Codes);
            Assert.Equal(HttpStatusCode.Unauthorized, signedIn.StatusCode);
            Assert.Equal("Open Library or Google Books", await settings.GetAsync(SettingKeys.LookUpWebCatalogues, ct));

            // Disconnected, the PC asks Open Library itself.
            await pc.DisconnectAsync(ct);
            Assert.Equal("Open Library", catalogue.Name);
            Assert.Null(await catalogue.FindAsync(["9781234567897"], ct));
            Assert.Single(direct.Asked);
        }

        [Fact]
        public async Task A_PC_counts_its_web_library_out_of_reach_until_it_answers_again()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = Path.Combine(_folder.Path, "pc-reach.db");
            await using (var db = new CatalogDatabase(path, Path.Combine(_folder.Path, "pc-backups")).CreateContext())
                await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
            var settings = new SettingsStore(new CatalogFile(path));
            await settings.SetAsync(SettingKeys.WebAddress, "http://localhost", ct);
            var key = new MemoryWebLibraryKey();
            key.Remember("not-a-key");
            var network = new Unplugged(_factory.Server.CreateHandler()) { Down = true };
            var pc = new WebLibrary(new HttpClient(network), key, settings, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WebLibrary>.Instance);
            var changes = 0;
            pc.ReachabilityChanged += (_, _) => changes++;

            Assert.Null(await pc.LookUpAsync(["9781234567897"], [], [], ct));
            var down = pc.IsUnreachable;
            network.Down = false;
            // Any answer, a refused key included, means it can be reached.
            await pc.LookUpAsync(["9781234567897"], [], [], ct);

            Assert.True(down);
            Assert.False(pc.IsUnreachable);
            Assert.Equal(2, changes);
        }
    }
}
