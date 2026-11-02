using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WorldEvent = Bibliotaph.Api.Contracts.NornisWorldEvent;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>What Nornis reads from a world: its books, their covers, pages found for a question, and page text.</summary>
    public sealed partial class NornisTests
    {
        /// <summary>The books of a game master's library, and which of them are where.</summary>
        sealed record WorldLibrary(User User, LibraryEntry Crown, LibraryEntry Indexed, LibraryEntry Kept, long Ashes);

        /// <summary>
        /// A library of three books: the crown in a collection inside the world Ashes, the indexed book and one kept from
        /// AI in Ashes itself. Tides is a world with nothing in it.
        /// </summary>
        async Task<WorldLibrary> WorldLibraryAsync(WebApplicationFactory<Program> server, CancellationToken ct)
        {
            var (user, _) = await GameMasterAsync(server);
            var drive = await server.Services.GetRequiredService<StorageConnections>().SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId,
                "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/crown.pdf", await File.ReadAllBytesAsync(_pdfs.ChapterBook, ct));
            _graph.Add("RPG/indexed.pdf", await File.ReadAllBytesAsync(_pdfs.Indexed, ct));
            _graph.Add("Kept/known.pdf", await File.ReadAllBytesAsync(_pdfs.KnownText, ct));
            var browser = Browser(server, user);
            (await browser.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)).Dispose();
            (await browser.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "Kept"), ct)).Dispose();
            var libraries = server.Services.GetRequiredService<UserLibraries>();
            await WaitForFilesAsync(libraries, user.Id, 3, ct);

            await using var lease = await libraries.OpenAsync(user.Id, ct);
            var library = lease.Library;
            var kept = (await library.Get<SourceRootStore>().ListAsync(ct)).Single(r => r.Path.EndsWith("Kept", StringComparison.Ordinal));
            (await browser.PutAsJsonAsync($"/api/ai/folders/{kept.Id}", new AiFolderSend(false), ct)).Dispose();
            await SendAsync(server, Event(WorldEvent.Created, Ashes, "Ashes", 0), Event(WorldEvent.Created, Tides, "Tides", 0));
            var ashes = (await WorldCollectionsAsync(library)).Single(c => c.Name == "Ashes").Id;
            var collections = library.Get<CollectionsService>();
            var session = await collections.CreateAsync("Session one", ashes, null, ct);

            // The cards follow the files once each is read.
            var entries = await WaitAsync(async () => await library.Get<LibraryQueries>().ListAsync(
                new LibraryFilter(await library.Get<LibraryStore>().GetVisibleEntryIdsAsync(ct: ct)), ct: ct) is { Count: 3 } all ? all : null, ct);
            var paths = await library.Get<LibraryStore>().GetRelativePathsAsync(ct);
            LibraryEntry Book(string name) => entries.Single(e => e.DocumentId == paths.Single(p => p.RelativePath.EndsWith(name, StringComparison.Ordinal)).DocumentId);
            var (crown, indexed, known) = (Book("crown.pdf"), Book("indexed.pdf"), Book("known.pdf"));
            await collections.AddAsync(session.Id, [crown.EntryId], ct);
            await collections.AddAsync(ashes, [indexed.EntryId, known.EntryId], ct);

            // Pages are searchable once every page's text is indexed.
            await WaitAsync(async () => (await library.Get<LibraryAskQueries>().GetDocumentPagesAsync(crown.DocumentId, 0, 49, ct)) is { Count: 50 } pages
                && pages.All(p => p.Text.Length > 0) ? pages : null, ct);
            return new WorldLibrary(user, crown, indexed, known, ashes);
        }

        static string World(Guid world, string rest) => $"/api/nornis/v1/worlds/{world}/{rest}";

        static HttpClient Reader(WebApplicationFactory<Program> server) => Nornis(server, NornisTokens.For(Subject, "books:read"));

        static async Task<NornisPassages> PassagesAsync(WebApplicationFactory<Program> server, Guid world, NornisPassageQuestion question)
        {
            using var response = await Reader(server).PostAsJsonAsync(World(world, "passages"), question, Ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<NornisPassages>(Ct))!;
        }

        [Fact]
        public async Task A_world_reads_only_the_books_in_its_collection_and_never_the_text_of_one_kept_from_AI()
        {
            using var server = Server();
            var world = await WorldLibraryAsync(server, Ct);
            var reader = Reader(server);

            var books = await reader.GetFromJsonAsync<NornisBooks>(World(Ashes, "books"), Ct);
            var empty = await reader.GetFromJsonAsync<NornisBooks>(World(Tides, "books"), Ct);
            using var unknown = await reader.GetAsync(World(Guid.NewGuid(), "books"), Ct);
            var goblins = await PassagesAsync(server, Ashes, new NornisPassageQuestion("Where do the bog goblins wait?", Mode: NornisPassageQuestion.Search));
            var onlyIndexed = await PassagesAsync(server, Ashes,
                new NornisPassageQuestion("Where do the bog goblins wait?", EntryIds: [world.Indexed.EntryId.Value], Mode: NornisPassageQuestion.Search));
            var owlbear = await PassagesAsync(server, Ashes, new NornisPassageQuestion(Bibliotaph.Pdf.Host.Tests.SyntheticPdfs.KnownPhrase,
                Mode: NornisPassageQuestion.Search));
            var two = await PassagesAsync(server, Ashes, new NornisPassageQuestion("dwarves halls ash", Limit: 2, Mode: NornisPassageQuestion.Search));
            var pages = await reader.GetFromJsonAsync<NornisPageTexts>(World(Ashes, $"documents/{world.Crown.DocumentId}/pages?from=1&to=2"), Ct);
            using var kept = await reader.GetAsync(World(Ashes, $"documents/{world.Kept.DocumentId}/pages?from=0&to=1"), Ct);
            using var elsewhere = await reader.GetAsync(World(Tides, $"documents/{world.Crown.DocumentId}/pages?from=0&to=1"), Ct);
            using var tooMany = await reader.GetAsync(World(Ashes, $"documents/{world.Crown.DocumentId}/pages?from=0&to=40"), Ct);
            using var tooLong = await reader.PostAsJsonAsync(World(Ashes, "passages"), new NornisPassageQuestion(new string('a', 1_001)), Ct);
            using var noScope = await Nornis(server, NornisTokens.For(Subject, "worlds:write")).GetAsync(World(Ashes, "books"), Ct);

            Assert.Equal(new[] { world.Crown.EntryId.Value, world.Indexed.EntryId.Value, world.Kept.EntryId.Value }.Order(),
                books!.Books.Select(b => b.EntryId).Order());
            var crown = books.Books.Single(b => b.EntryId == world.Crown.EntryId.Value);
            Assert.Equal((world.Crown.DocumentId, 50, "pdf"), (crown.DocumentId, crown.PageCount, crown.Format.ToLowerInvariant()));
            Assert.Empty(empty!.Books);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            Assert.True(goblins.Covered);
            var first = goblins.Pages[0];
            Assert.Equal((world.Crown.EntryId.Value, world.Crown.DocumentId, 1, "2"), (first.EntryId, first.DocumentId, first.PageIndex, first.PageLabel));
            Assert.Contains("bog goblins wait", first.Text, StringComparison.Ordinal);
            Assert.All(goblins.Pages, p => Assert.Equal(world.Crown.EntryId.Value, p.EntryId));
            Assert.DoesNotContain(onlyIndexed.Pages, p => p.EntryId == world.Crown.EntryId.Value);
            Assert.DoesNotContain(owlbear.Pages, p => p.DocumentId == world.Kept.DocumentId);
            Assert.False(goblins.MeaningUsed);
            Assert.Equal(2, two.Pages.Count);

            Assert.Equal([1, 2], pages!.Pages.Select(p => p.PageIndex));
            Assert.Equal("2", pages.Pages[0].PageLabel);
            Assert.Contains("bog goblins wait in the reeds", pages.Pages[0].Text, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Forbidden, kept.StatusCode);
            Assert.Equal(NornisProblemsCode.KeptFromAi, await CodeAsync(kept));
            Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
        }

        [Fact]
        public async Task Weak_signals_cover_nothing_when_asking_and_give_the_closest_pages_when_searching()
        {
            using var server = Server();
            await WorldLibraryAsync(server, Ct);
            const string Question = "Which spreadsheet holds the quarterly invoice totals for the dwarves?";

            var asked = await PassagesAsync(server, Ashes, new NornisPassageQuestion(Question));
            var searched = await PassagesAsync(server, Ashes, new NornisPassageQuestion(Question, Mode: NornisPassageQuestion.Search));

            Assert.False(asked.Covered);
            Assert.Empty(asked.Pages);
            Assert.False(searched.Covered);
            Assert.NotEmpty(searched.Pages);
            Assert.Contains("dwarves", searched.Pages[0].Text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_gone_world_reads_nothing_and_its_books_covers_are_no_longer_Nornis_s()
        {
            using var server = Server();
            var world = await WorldLibraryAsync(server, Ct);
            var libraries = server.Services.GetRequiredService<UserLibraries>();
            var cover = await WaitAsync(async () =>
            {
                await using var lease = await libraries.OpenAsync(world.User.Id, Ct);
                var entries = await lease.Library.Get<LibraryQueries>().GetEntriesAsync([world.Crown.EntryId], Ct);
                return entries.SingleOrDefault()?.Cover;
            }, Ct);
            var reader = Reader(server);

            using var shown = await reader.GetAsync($"/api/nornis/v1/covers/{cover}", Ct);
            await SendAsync(server, Event(WorldEvent.Deleted, Ashes, "Ashes", 30));
            using var gone = await reader.GetAsync($"/api/nornis/v1/covers/{cover}", Ct);
            using var books = await reader.GetAsync(World(Ashes, "books"), Ct);
            using var pages = await reader.GetAsync(World(Ashes, $"documents/{world.Crown.DocumentId}/pages?from=0&to=1"), Ct);
            using var passages = await reader.PostAsJsonAsync(World(Ashes, "passages"), new NornisPassageQuestion("bog goblins"), Ct);

            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
            Assert.Equal("image/jpeg", shown.Content.Headers.ContentType?.MediaType);
            Assert.All([gone, books, pages, passages], r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        }

        [Fact]
        public async Task A_closed_library_answers_opening_within_two_seconds_and_goes_on_opening()
        {
            var held = new HeldRestore();
            using var server = Server(more: services => services.AddSingleton<IDatabaseRestore>(held));
            await GameMasterAsync(server);
            // The first call through the pipeline pays for warming it; refused for its scope, it never touches the library.
            (await Nornis(server, NornisTokens.For(Subject, "worlds:write")).GetAsync(World(Ashes, "books"), Ct)).Dispose();

            var watch = Stopwatch.StartNew();
            using var opening = await Reader(server).GetAsync(World(Ashes, "books"), Ct);
            watch.Stop();
            held.Release.TrySetResult();
            using var opened = await WaitAsync(async () =>
            {
                var response = await Reader(server).GetAsync(World(Ashes, "books"), Ct);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return response;
                response.Dispose();
                return null;
            }, Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, opening.StatusCode);
            Assert.Equal(NornisProblemsCode.Opening, await CodeAsync(opening));
            Assert.Equal(TimeSpan.FromSeconds(5), opening.Headers.RetryAfter?.Delta);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Answered in {watch.Elapsed}.");
            // Ashes isn't one of this user's worlds yet: once open, the library says so.
            Assert.Equal(HttpStatusCode.NotFound, opened.StatusCode);
        }

        /// <summary>The stable codes Nornis acts on.</summary>
        static class NornisProblemsCode
        {
            public const string Opening = Bibliotaph.Server.Nornis.NornisProblems.Opening;
            public const string KeptFromAi = Bibliotaph.Server.Nornis.NornisProblems.KeptFromAi;
        }

        /// <summary>A restore that lasts until the test lets it finish, as a large one does after a deploy.</summary>
        sealed class HeldRestore : IDatabaseRestore
        {
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task RestoreAsync(string relativePath, string target, CancellationToken ct) => await Release.Task.WaitAsync(ct);
        }
    }
}
