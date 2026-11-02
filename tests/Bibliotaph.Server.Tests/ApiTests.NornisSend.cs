using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Send to Nornis: which worlds a book's pages can be filed in, and the address Nornis's excerpt page opens at.</summary>
    public sealed partial class NornisTests
    {
        [Fact]
        public async Task Pages_go_to_Nornis_only_from_a_book_in_a_live_world_and_never_from_one_kept_from_AI()
        {
            using var server = Server();
            var world = await WorldLibraryAsync(server, Ct);
            var browser = Browser(server, world.User);
            long single, whole, tooMany;
            await using (var lease = await server.Services.GetRequiredService<UserLibraries>().OpenAsync(world.User.Id, Ct))
            {
                var sessions = lease.Library.Get<SessionsService>();
                var binder = await sessions.CreateAsync("Session one", ct: Ct);
                single = (await sessions.AddPagesAsync(binder.Id, world.Crown.DocumentId, 1, 3, "Bog ambush", ct: Ct))!.Value;
                whole = (await sessions.AddDocumentAsync(binder.Id, world.Crown.DocumentId, ct: Ct))!.Value;
                tooMany = (await sessions.AddPagesAsync(binder.Id, world.Crown.DocumentId, 0, 40, ct: Ct))!.Value;
            }

            var crown = (await browser.GetFromJsonAsync<NornisSend>($"/api/library/documents/{world.Crown.DocumentId}/nornis", Ct))!;
            var kept = (await browser.GetFromJsonAsync<NornisSend>($"/api/library/documents/{world.Kept.DocumentId}/nornis", Ct))!;
            var item = (await browser.GetFromJsonAsync<NornisSend>($"/api/binders/items/{single}/nornis", Ct))!;
            var wholeBook = (await browser.GetFromJsonAsync<NornisSend>($"/api/binders/items/{whole}/nornis", Ct))!;
            var tooLong = (await browser.GetFromJsonAsync<NornisSend>($"/api/binders/items/{tooMany}/nornis", Ct))!;
            await SendAsync(server, Event(Bibliotaph.Api.Contracts.NornisWorldEvent.Deleted, Ashes, "Ashes", 5));
            var gone = (await browser.GetFromJsonAsync<NornisSend>($"/api/library/documents/{world.Crown.DocumentId}/nornis", Ct))!;

            var ashes = Assert.Single(crown.Worlds);
            Assert.Equal((Ashes, "Ashes"), (ashes.WorldId, ashes.Name));
            Assert.Equal($"{FakeNornis.Origin}/bibliotaph/excerpt?world={Ashes}&document={world.Crown.DocumentId}", ashes.Url);
            Assert.Null(crown.Problem);
            Assert.Equal(NornisSend.KeptFromAi, kept.Problem);
            Assert.Equal((1, 3, "Bog ambush"), (item.From, item.To, item.Label));
            Assert.Equal($"{ashes.Url}&from=1&to=3&label=Bog%20ambush", NornisSend.Excerpt(Assert.Single(item.Worlds), 1, 3, item.Label));
            Assert.Empty(wholeBook.Worlds);
            Assert.Equal(NornisSend.TooManyPages, tooLong.Problem);
            Assert.Empty(gone.Worlds);
        }

        [Fact]
        public async Task Nothing_is_offered_once_Nornis_is_disconnected()
        {
            using var server = Server();
            var world = await WorldLibraryAsync(server, Ct);
            var connections = server.Services.GetRequiredService<StorageConnections>();
            foreach (var connection in (await connections.ListAsync(world.User.Id, Ct)).Where(c => c.Provider == NornisProvider.Key))
                await connections.RemoveAsync(world.User.Id, connection.Id, Ct);

            var send = await Browser(server, world.User).GetFromJsonAsync<NornisSend>($"/api/library/documents/{world.Crown.DocumentId}/nornis", Ct);

            Assert.Empty(send!.Worlds);
        }

        [Fact]
        public async Task A_connected_PC_is_told_the_web_library_s_own_document_for_the_file_it_has_open()
        {
            using var server = Server();
            var world = await WorldLibraryAsync(server, Ct);
            string hash;
            await using (var lease = await server.Services.GetRequiredService<UserLibraries>().OpenAsync(world.User.Id, Ct))
                hash = (await lease.Library.Get<LibraryStore>().GetContentHashAsync(world.Crown.DocumentId, Ct))!;
            var pc = await ConnectPcAsync(server, Browser(server, world.User));

            var book = await pc.NornisSendAsync(hash, null, Ct);
            var pages = await pc.NornisSendAsync(hash, (1, 3, "Bog ambush"), Ct);
            var missing = await pc.NornisSendAsync(new string('f', 64), null, Ct);
            using var anonymous = await server.CreateClient().GetAsync($"/api/pc/books/{hash}/nornis", Ct);
            using var browser = await Browser(server, world.User).GetAsync($"/api/pc/books/{hash}/nornis", Ct);

            var ashes = Assert.Single(book!.Send!.Worlds);
            Assert.Equal($"{FakeNornis.Origin}/bibliotaph/excerpt?world={Ashes}&document={world.Crown.DocumentId}", ashes.Url);
            Assert.Equal((1, 3, "Bog ambush"), (pages!.Send!.From, pages.Send.To, pages.Send.Label));
            Assert.Same(NornisSendAnswer.NotThere, missing);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.NotEqual(System.Net.HttpStatusCode.OK, browser.StatusCode);
        }

        /// <summary>A PC connected to this server's web library, as <see cref="ApiTests.ConnectPcAsync"/> is to the plain one.</summary>
        async Task<WebLibrary> ConnectPcAsync(WebApplicationFactory<Program> server, HttpClient web)
        {
            var path = Path.Combine(_folder.Path, $"pc-{Guid.NewGuid():N}.db");
            var catalog = new CatalogDatabase(path, Path.Combine(_folder.Path, "pc-backups"));
            await using (var db = catalog.CreateContext()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, Ct);
            var settings = new SettingsStore(new CatalogFile(path));
            await settings.SetAsync(SettingKeys.WebAddress, "http://localhost", Ct);
            var pc = new WebLibrary(new HttpClient(server.Server.CreateHandler()), new MemoryWebLibraryKey(), settings, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WebLibrary>.Instance);
            var started = (await pc.StartAsync("DESKTOP-1", Ct))!;
            var waiting = pc.WaitAsync(started, Ct);
            (await web.PostAsync($"/api/devices/connect/{started.Code}", null, Ct)).EnsureSuccessStatusCode();
            Assert.Equal(WebConnectOutcome.Connected, await waiting);
            return pc;
        }
    }
}
