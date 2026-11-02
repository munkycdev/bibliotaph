using System.Net;
using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Nornis;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WorldEvent = Bibliotaph.Api.Contracts.NornisWorldEvent;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>A player reading a game master's book through a reading ticket, view-only and for an hour.</summary>
    public sealed partial class NornisTests
    {
        const string Renew = FakeNornis.Origin + "/worlds/ashes/books/shared";

        /// <summary>A game master's library on Dropbox, which is read through the server: one book in Ashes and one in no world.</summary>
        async Task<(User User, long Shared, long Other)> TableLibraryAsync(WebApplicationFactory<Program> server, CancellationToken ct)
        {
            var (user, _) = await GameMasterAsync(server);
            var drive = await server.Services.GetRequiredService<StorageConnections>().SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId,
                "Dropbox · Dana", "personal", "refresh", null, ct);
            _dropbox.Add("RPG/shared.pdf", await File.ReadAllBytesAsync(_pdfs.ChapterBook, ct));
            _dropbox.Add("RPG/other.pdf", await File.ReadAllBytesAsync(_pdfs.Indexed, ct));
            (await Browser(server, user).PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)).Dispose();
            var libraries = server.Services.GetRequiredService<UserLibraries>();
            await WaitForFilesAsync(libraries, user.Id, 2, ct);
            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 0));

            await using var lease = await libraries.OpenAsync(user.Id, ct);
            var library = lease.Library;
            var entries = await WaitAsync(async () => await library.Get<LibraryQueries>().ListAsync(
                new LibraryFilter(await library.Get<LibraryStore>().GetVisibleEntryIdsAsync(ct: ct)), ct: ct) is { Count: 2 } all ? all : null, ct);
            var paths = await library.Get<LibraryStore>().GetRelativePathsAsync(ct);
            long Document(string name) => paths.Single(p => p.RelativePath.EndsWith(name, StringComparison.Ordinal)).DocumentId;
            var ashes = (await WorldCollectionsAsync(library)).Single().Id;
            await library.Get<CollectionsService>().AddAsync(ashes, [entries.Single(e => e.DocumentId == Document("shared.pdf")).EntryId], ct);
            return (user, Document("shared.pdf"), Document("other.pdf"));
        }

        static HttpClient Minter(WebApplicationFactory<Program> server) => Nornis(server, NornisTokens.For(Subject, "tickets:write"));

        /// <summary>A player's browser, over https so it keeps and sends the guest cookie.</summary>
        static HttpClient Player(WebApplicationFactory<Program> server) =>
            server.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        static async Task<NornisTicket> MintAsync(WebApplicationFactory<Program> server, NornisTicketRequest request)
        {
            using var response = await Minter(server).PostAsJsonAsync(World(Ashes, "reading-tickets"), request, Ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<NornisTicket>(Ct))!;
        }

        static string Table(long documentId, string rest) => $"/api/table/{documentId}/{rest}";

        [Fact]
        public async Task A_reading_ticket_opens_its_one_book_once_for_a_player_and_the_library_records_nothing()
        {
            using var server = Server();
            var (user, shared, other) = await TableLibraryAsync(server, Ct);
            var player = Player(server);

            var ticket = await MintAsync(server, new NornisTicketRequest(shared, Renew, StartPage: 1, ViewerName: "Pip\nthe bard"));
            using var outside = await Minter(server).PostAsJsonAsync(World(Ashes, "reading-tickets"), new NornisTicketRequest(other, Renew), Ct);
            using var offSite = await Minter(server).PostAsJsonAsync(World(Ashes, "reading-tickets"),
                new NornisTicketRequest(shared, "https://nornis.test.example.com/renew"), Ct);
            using var longName = await Minter(server).PostAsJsonAsync(World(Ashes, "reading-tickets"),
                new NornisTicketRequest(shared, Renew, ViewerName: new string('a', 101)), Ct);
            using var noScope = await Reader(server).PostAsJsonAsync(World(Ashes, "reading-tickets"), new NornisTicketRequest(shared, Renew), Ct);
            using var session = await Browser(server, user).PostAsJsonAsync(World(Ashes, "reading-tickets"), new NornisTicketRequest(shared, Renew), Ct);

            var path = new Uri(ticket.Url).PathAndQuery;
            using var redeemed = await player.GetAsync(path, Ct);
            using var again = await Player(server).GetAsync(path, Ct);
            using var unknown = await Player(server).GetAsync("/table/not-a-ticket", Ct);
            var cookie = redeemed.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(GuestAuthentication.Cookie, StringComparison.Ordinal)).ToLowerInvariant();

            var opened = (await player.GetFromJsonAsync<GuestBook>(Table(shared, "reader"), Ct))!;
            using var otherBook = await player.GetAsync(Table(other, "reader"), Ct);
            var link = (await player.GetFromJsonAsync<ReaderLink>(Table(shared, "link"), Ct))!;
            using var bytes = new HttpRequestMessage(HttpMethod.Get, link.Url);
            bytes.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
            using var read = await player.SendAsync(bytes, Ct);
            var ownersLink = (await Browser(server, user).GetFromJsonAsync<ReaderLink>($"/api/library/documents/{other}/link", Ct))!;
            using var notShared = new HttpRequestMessage(HttpMethod.Get, ownersLink.Url);
            notShared.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
            using var notSharedRead = await player.SendAsync(notShared, Ct);
            var found = (await player.GetFromJsonAsync<ReaderFind>(Table(shared, "find?q=bog%20goblins"), Ct))!;
            using var printed = await player.GetAsync(Table(shared, "printed-pages"), Ct);

            Assert.StartsWith("http://localhost/table/", ticket.Url, StringComparison.Ordinal);
            Assert.InRange(ticket.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1.5), ReadingTickets.Lifetime);
            Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, offSite.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);

            Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
            Assert.Equal($"/guest/{shared}?page=1", redeemed.Headers.Location!.OriginalString);
            Assert.Contains("secure", cookie, StringComparison.Ordinal);
            Assert.Contains("httponly", cookie, StringComparison.Ordinal);
            Assert.Contains("samesite=lax", cookie, StringComparison.Ordinal);
            Assert.Contains("path=/", cookie, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
            Assert.Contains("This link has been used or has expired.", await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            Assert.Contains(FakeNornis.Origin, await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Gone, unknown.StatusCode);

            Assert.Equal((shared, 1, "Dana", "Ashes", Renew), (opened.Book.DocumentId, opened.Book.StartPage, opened.SharedBy, opened.World, opened.Renew));
            Assert.False(opened.Book.KeepsPlace);
            Assert.DoesNotContain("RPG", opened.Book.Subtitle, StringComparison.Ordinal);
            Assert.InRange(opened.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), GuestAuthentication.Hour);
            Assert.Equal(HttpStatusCode.NotFound, otherBook.StatusCode);
            Assert.Equal(HttpStatusCode.PartialContent, read.StatusCode);
            Assert.Equal("%PDF", await read.Content.ReadAsStringAsync(Ct));
            Assert.Equal(HttpStatusCode.Forbidden, notSharedRead.StatusCode);
            Assert.Contains(found.Pages, p => p.Page == 1);
            Assert.Equal(HttpStatusCode.OK, printed.StatusCode);

            // Nothing about the player is kept: no place, no open recorded.
            await using (var lease = await server.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, Ct))
            {
                Assert.Equal(0, await lease.Library.Get<ReadingService>().GetPositionAsync(shared, Ct));
                var entries = await lease.Library.Get<LibraryQueries>().ListAsync(
                    new LibraryFilter(await lease.Library.Get<LibraryStore>().GetVisibleEntryIdsAsync(ct: Ct)), ct: Ct);
                Assert.All(entries, e => Assert.Null(e.OpenedUtc));

                // Taking the book out of the world closes it to the player at once.
                var ashes = (await WorldCollectionsAsync(lease.Library)).Single().Id;
                await lease.Library.Get<CollectionsService>().RemoveAsync(ashes, [.. entries.Where(e => e.DocumentId == shared).Select(e => e.EntryId)], Ct);
            }
            using var removed = await player.GetAsync(Table(shared, "reader"), Ct);
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        }

        [Fact]
        public async Task The_guest_scheme_reaches_only_the_guest_reader_and_nothing_else_reaches_it()
        {
            using var server = Server();
            var (user, shared, _) = await TableLibraryAsync(server, Ct);
            var player = Player(server);
            using (await player.GetAsync(new Uri((await MintAsync(server, new NornisTicketRequest(shared, Renew))).Url).PathAndQuery, Ct)) { }
            player.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            using var reader = await player.GetAsync(Table(shared, "reader"), Ct);
            using var library = await player.GetAsync("/api/library/opening", Ct);
            using var owners = await player.GetAsync($"/api/library/documents/{shared}/link", Ct);
            using var storage = await player.GetAsync("/api/storage", Ct);
            using var nornis = await player.PostAsync("/api/nornis/v1/warm", null, Ct);
            using var session = await Browser(server, user).GetAsync(Table(shared, "reader"), Ct);
            using var token = await Nornis(server, NornisTokens.For(Subject)).GetAsync(Table(shared, "reader"), Ct);
            using var anonymous = await Player(server).GetAsync(Table(shared, "link"), Ct);

            Assert.Equal(HttpStatusCode.OK, reader.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, library.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, owners.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, storage.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, nornis.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        [Fact]
        public async Task A_ticket_is_good_for_two_minutes_and_once_and_a_guest_pass_ends_an_hour_on_without_sliding()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server, connected: false);
            var clock = new ManualClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
            var tickets = new ReadingTickets(server.Services.GetRequiredService<AccountsDatabase>(), clock);

            var (late, _) = await tickets.MintAsync(user.Id, Ashes, 7, 7, null, null, Renew, Ct);
            clock.Advance(ReadingTickets.Lifetime + TimeSpan.FromSeconds(1));
            var tooLate = await tickets.RedeemAsync(late, Ct);
            var (code, _) = await tickets.MintAsync(user.Id, Ashes, 7, 8, 3, "Pip", Renew, Ct);
            var first = await tickets.RedeemAsync(code, Ct);
            var second = await tickets.RedeemAsync(code, Ct);

            Assert.Null(tooLate);
            Assert.Equal((user.Id, Ashes.ToString("D"), 7L, 8L, (int?)3), (first!.UserId, first.WorldId, first.DocumentId, first.EntryId, first.StartPage));
            Assert.Null(second);
            await using (var db = server.Services.GetRequiredService<AccountsDatabase>().CreateContext())
                Assert.DoesNotContain(db.ReadingTickets, t => t.CodeHash == code);

            var protection = server.Services.GetRequiredService<IDataProtectionProvider>();
            var written = new DefaultHttpContext();
            var pass = new GuestPass(user.Id, Ashes.ToString("D"), 7, 8, Renew, clock.GetUtcNow().UtcDateTime + GuestAuthentication.Hour, null);
            GuestAuthentication.Write(written.Response, [pass], protection);
            var value = written.Response.Headers.SetCookie.ToString().Split(';')[0][(GuestAuthentication.Cookie.Length + 1)..];
            var asked = new DefaultHttpContext();
            asked.Request.Headers.Cookie = $"{GuestAuthentication.Cookie}={value}";

            clock.Advance(TimeSpan.FromMinutes(59));
            var stillGood = GuestAuthentication.Read(asked.Request, protection, clock);
            clock.Advance(TimeSpan.FromMinutes(2));
            var ended = GuestAuthentication.Read(asked.Request, protection, clock);

            Assert.Equal(pass, Assert.Single(stillGood));
            Assert.Empty(ended);
        }
    }
}
