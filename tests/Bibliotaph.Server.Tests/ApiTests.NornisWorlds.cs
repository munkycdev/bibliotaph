using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorldEvent = Bibliotaph.Api.Contracts.NornisWorldEvent;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Nornis's world events, and the collections they make.</summary>
    public sealed partial class NornisTests
    {
        static readonly Guid Ashes = new("6f1d8a52-0000-4000-8000-000000000001");
        static readonly Guid Tides = new("6f1d8a52-0000-4000-8000-000000000002");

        static WorldEvent Event(string kind, Guid world, string name, int minute, Guid? id = null) =>
            new(id ?? Guid.NewGuid(), kind, world, name, new DateTimeOffset(2026, 11, 2, 12, minute, 0, TimeSpan.Zero));

        static async Task<HttpStatusCode> SendAsync(WebApplicationFactory<Program> server, params WorldEvent[] events)
        {
            using var response = await Nornis(server, NornisTokens.For(Subject, "worlds:write")).PostAsJsonAsync("/api/nornis/v1/worlds/events", events, Ct);
            return response.StatusCode;
        }

        /// <summary>The collections inside the Nornis collection, by name.</summary>
        static async Task<IReadOnlyList<CollectionInfo>> WorldCollectionsAsync(UserLibrary library)
        {
            var root = long.Parse((await library.Get<SettingsStore>().GetAsync(SettingKeys.NornisCollection, Ct))!, CultureInfo.InvariantCulture);
            return [.. (await library.Get<CollectionsService>().ListAsync(Ct)).Where(c => c.ParentId == root).OrderBy(c => c.Name, StringComparer.Ordinal)];
        }

        static async Task<int> InboxAsync(WebApplicationFactory<Program> server, bool pending)
        {
            await using var db = server.Services.GetRequiredService<AccountsDatabase>().CreateContext();
            return await db.NornisWorldEvents.CountAsync(e => (e.AppliedUtc == null) == pending, Ct);
        }

        [Fact]
        public async Task Events_sent_while_the_library_is_closed_wait_in_the_inbox_and_apply_as_it_opens()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server);
            var libraries = server.Services.GetRequiredService<UserLibraries>();

            var sent = await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes of the Old Kings", 0), Event(WorldEvent.Created, Tides, "Tides", 1));

            Assert.Equal(HttpStatusCode.Accepted, sent);
            Assert.Equal(0, libraries.OpenCount);
            Assert.Equal(2, await InboxAsync(server, pending: true));

            await using var lease = await libraries.OpenAsync(user.Id, Ct);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (await InboxAsync(server, pending: true) > 0 && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);

            Assert.Equal(["Ashes of the Old Kings", "Tides"], (await WorldCollectionsAsync(lease.Library)).Select(c => c.Name));
            Assert.Equal(2, await InboxAsync(server, pending: false));
        }

        [Fact]
        public async Task A_repeated_event_or_one_older_than_the_last_applied_changes_nothing()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server);
            await using var lease = await server.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, Ct);
            var rename = Guid.NewGuid();

            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 0), Event(WorldEvent.Renamed, Ashes, "Cinders", 10, rename));
            var repeated = await SendAsync(server, Event(WorldEvent.Renamed, Ashes, "Embers", 20, rename));
            var stale = await SendAsync(server, Event(WorldEvent.Renamed, Ashes, "Smoke", 5));

            Assert.Equal(HttpStatusCode.Accepted, repeated);
            Assert.Equal(HttpStatusCode.Accepted, stale);
            Assert.Equal("Cinders", Assert.Single(await WorldCollectionsAsync(lease.Library)).Name);
            Assert.Equal(3, await InboxAsync(server, pending: false));
        }

        [Fact]
        public async Task A_world_s_collection_the_game_master_renamed_keeps_their_name()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server);
            await using var lease = await server.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, Ct);
            var collections = lease.Library.Get<CollectionsService>();

            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 0), Event(WorldEvent.Created, Tides, "Tides", 0));
            var mine = (await WorldCollectionsAsync(lease.Library))[0];
            await collections.RenameAsync(mine.Id, "My Ashes campaign", "Thursdays", Ct);
            await SendAsync(server, Event(WorldEvent.Renamed, Ashes, "Cinders", 10), Event(WorldEvent.Renamed, Tides, "Low Tides", 10));
            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Embers", 20));

            var worlds = await WorldCollectionsAsync(lease.Library);
            Assert.Equal(["Low Tides", "My Ashes campaign"], worlds.Select(c => c.Name));
            Assert.Equal("Thursdays", worlds.Single(c => c.Id == mine.Id).Description);
        }

        [Fact]
        public async Task A_deleted_world_is_marked_gone_and_comes_back_when_Nornis_sends_it_again()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server);
            await using var lease = await server.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, Ct);
            var browser = Browser(server, user);
            var root = await NornisConnectRootAsync(lease.Library);

            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 0), Event(WorldEvent.Deleted, Ashes, "Ashes", 5));
            var gone = await browser.GetFromJsonAsync<List<CollectionCard>>($"/api/collections/cards?parent={root}", Ct);
            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 10));
            var back = await browser.GetFromJsonAsync<List<CollectionCard>>($"/api/collections/cards?parent={root}", Ct);
            var first = Assert.Single(await WorldCollectionsAsync(lease.Library));
            await lease.Library.Get<CollectionsService>().DeleteAsync(first.Id, Ct);
            await SendAsync(server, Event(WorldEvent.Renamed, Ashes, "Cinders", 15));
            var afterDeleting = await WorldCollectionsAsync(lease.Library);
            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Cinders", 20));
            var resynced = Assert.Single(await WorldCollectionsAsync(lease.Library));
            var plain = await lease.Library.Get<CollectionsService>().CreateAsync("Handouts", root, null, Ct);
            var cards = await browser.GetFromJsonAsync<List<CollectionCard>>($"/api/collections/cards?parent={root}", Ct);

            Assert.Equal("World gone", Assert.Single(gone!).Badge);
            Assert.Equal("Nornis world", Assert.Single(back!).Badge);
            Assert.Empty(afterDeleting);
            Assert.Equal("Cinders", resynced.Name);
            Assert.NotEqual(first.Id, resynced.Id);
            Assert.Equal("Nornis world", cards!.Single(c => c.Id == resynced.Id).Badge);
            Assert.Null(cards!.Single(c => c.Id == plain.Id).Badge);
        }

        static async Task<long> NornisConnectRootAsync(UserLibrary library) =>
            await Bibliotaph.Server.Nornis.NornisConnect.EnsureCollectionAsync(library, Ct);

        [Fact]
        public async Task A_malformed_batch_is_refused_and_nothing_of_it_is_written()
        {
            using var server = Server();
            await GameMasterAsync(server);
            var good = Event(WorldEvent.Created, Ashes, "Ashes", 0);

            var unnamed = await SendAsync(server, good, Event(WorldEvent.Created, Tides, " ", 0));
            var unknownKind = await SendAsync(server, good, Event("archived", Tides, "Tides", 0));
            var longName = await SendAsync(server, good, Event(WorldEvent.Created, Tides, new string('x', WorldEvent.LongestName + 1), 0));
            var tooMany = await SendAsync(server, [.. Enumerable.Range(0, WorldEvent.MostInBatch + 1).Select(i => Event(WorldEvent.Created, Guid.NewGuid(), "World", i % 60))]);
            var empty = await SendAsync(server);
            using var notJson = await Nornis(server, NornisTokens.For(Subject, "worlds:write"))
                .PostAsync("/api/nornis/v1/worlds/events", new StringContent("[{\"eventId\":\"nope\"}]", System.Text.Encoding.UTF8, "application/json"), Ct);
            using var noScope = await Nornis(server, NornisTokens.For(Subject, "books:read")).PostAsJsonAsync("/api/nornis/v1/worlds/events", new[] { good }, Ct);

            Assert.All([unnamed, unknownKind, longName, tooMany, empty, notJson.StatusCode], s => Assert.Equal(HttpStatusCode.BadRequest, s));
            Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
            Assert.Equal(0, await InboxAsync(server, pending: true) + await InboxAsync(server, pending: false));
        }

        [Fact]
        public async Task Resync_asks_Nornis_to_send_the_worlds_again()
        {
            using var server = Server();
            var (user, connection) = await GameMasterAsync(server);
            var browser = Browser(server, user);

            using var resync = await browser.PostAsync($"/api/storage/{connection!.Id}/nornis/resync", null, Ct);
            using var someoneElse = await Browser(server, (await SignInAsync(server, "someone-else")).User!).PostAsync($"/api/storage/{connection.Id}/nornis/resync", null, Ct);

            Assert.Equal(HttpStatusCode.Accepted, resync.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, someoneElse.StatusCode);
            Assert.Equal(1, _nornis.Resyncs);
        }

        static Task<SignInOutcome> SignInAsync(WebApplicationFactory<Program> server, string subject) =>
            server.Services.GetRequiredService<AccountService>().SignInFromNornisAsync(new ExternalIdentity(Auth.Auth0SignIn.Id, subject, "Sam", null), null, Ct);
    }
}
