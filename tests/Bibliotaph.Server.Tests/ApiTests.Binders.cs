using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Binders, online library pages and books owned elsewhere.</summary>
    public sealed class BindersTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task Binders_are_listed_opened_arranged_and_deleted_with_undo_as_the_windows_app_does()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/Maps/harbor.png", Png());
            _graph.Add("RPG/Maps/tower.png", Png(SkiaSharp.SKColors.Crimson));
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var books = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 2 } two ? two : null, ct);

            using var untitled = await client.PostAsJsonAsync("/api/binders", new BinderEdit("  ", null, false, null), ct);
            var created = await SendAsync<BinderCreated>(HttpMethod.Post, "/api/binders", new BinderEdit("Harbor night", new DateOnly(2026, 10, 31), false, null));
            var atHand = await SendAsync<BinderCreated>(HttpMethod.Post, "/api/binders", new BinderEdit("Rules", null, true, null));
            long first, second;
            await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
            {
                var added = await lease.Library.Get<SessionsService>().AddAsync(created.Id,
                    [.. books.Select(b => new Bibliotaph.Catalog.NewSessionItem(new Bibliotaph.Core.EntryId(b.EntryId)))], ct: ct);
                (first, second) = (added[0], added[1]);
            }
            var list = await client.GetFromJsonAsync<BinderList>("/api/binders?today=2026-10-25", ct);
            var later = await client.GetFromJsonAsync<BinderList>("/api/binders?today=2026-11-01", ct);
            var home = await client.GetFromJsonAsync<HomeShelves>("/api/home", ct);
            await SendAsync<ActionResult>(HttpMethod.Post, $"/api/binders/{created.Id}/sections", new BinderName("Maps"));
            var withSection = await SendAsync<BinderPage>(HttpMethod.Post, $"/api/binders/{created.Id}/open", null);
            var maps = withSection.Rows.Single(r => r.Section is not null).Section!;
            await SendAsync<bool>(HttpMethod.Post, $"/api/binders/items/{first}/move", new BinderItemMove(SectionId: maps.Id));
            await SendAsync<bool>(HttpMethod.Post, $"/api/binders/items/{second}/move", new BinderItemMove(SectionId: maps.Id, First: true));
            await SendAsync<bool>(HttpMethod.Post, $"/api/binders/items/{first}/move", new BinderItemMove(By: -1));
            var afterUp = await client.GetFromJsonAsync<BinderPage>($"/api/binders/{created.Id}", ct);
            await SendAsync<bool>(HttpMethod.Post, $"/api/binders/items/{second}/move", new BinderItemMove(Onto: first));
            using (await client.PutAsJsonAsync($"/api/binders/items/{first}", new BinderItemEdit("Harbor map", "Read aloud"), ct)) { }
            using (await client.PutAsJsonAsync($"/api/binders/{created.Id}/notes", new BinderNotes("Bring dice"), ct)) { }
            var arranged = await client.GetFromJsonAsync<BinderPage>($"/api/binders/{created.Id}", ct);
            var removed = await SendAsync<ActionResult>(HttpMethod.Delete, $"/api/binders/items/{second}", null);
            var afterRemove = await client.GetFromJsonAsync<BinderPage>($"/api/binders/{created.Id}", ct);
            await SendAsync<ActionResult>(HttpMethod.Post, $"/api/library/held/{removed.Undo}", null);
            var scoped = await client.GetFromJsonAsync<LibraryResults>($"/api/library?binder={created.Id}", ct);
            var copy = await SendAsync<BinderCreated>(HttpMethod.Post, $"/api/binders/{created.Id}/duplicate", null);
            var copied = await client.GetFromJsonAsync<BinderPage>($"/api/binders/{copy.Id}", ct);
            var deleted = await SendAsync<ActionResult>(HttpMethod.Delete, $"/api/binders/{created.Id}", null);
            using var gone = await client.GetAsync($"/api/binders/{created.Id}", ct);
            await SendAsync<ActionResult>(HttpMethod.Post, $"/api/library/held/{deleted.Undo}", null);
            var restoredCard = (await client.GetFromJsonAsync<BinderList>("/api/binders?today=2026-10-25", ct))!.Upcoming.Single(c => c.Title == "Harbor night");
            var restored = await client.GetFromJsonAsync<BinderPage>($"/api/binders/{restoredCard.Id}", ct);

            Assert.Equal(HttpStatusCode.BadRequest, untitled.StatusCode);
            Assert.Equal(["Harbor night"], list!.Upcoming.Select(c => c.Title));
            Assert.Equal(["Rules"], list.AtHand.Select(c => c.Title));
            Assert.Empty(list.Earlier);
            Assert.Equal(("2 items", "At hand · any system", 2), (list.Upcoming[0].Count, list.AtHand[0].Date, list.Upcoming[0].Covers.Count));
            Assert.Equal(["Harbor night"], later!.Earlier.Select(c => c.Title));
            Assert.Equal("Show earlier binders (1)", later.ShowEarlier);
            Assert.Equal("Any game system", list.Systems[0].Label);
            Assert.Equal("Harbor night", home!.Preparing?.Title);
            Assert.Equal(["Adventure", "Maps", "Encounters", "Rules"], withSection.SuggestedSections);
            Assert.Equal(BinderText.Eyebrow(new DateOnly(2026, 10, 31)), withSection.Eyebrow);
            Assert.StartsWith("SATURDAY, ", withSection.Eyebrow, StringComparison.Ordinal);
            // Move to section goes to the end, a drop on a heading to the start, and a drop on an item takes its place.
            Assert.Equal([maps.Id, second, first], arranged!.Rows.Select(r => r.Item?.Id ?? r.Section!.Id));
            Assert.Equal([first, second], afterUp!.Rows.Select(r => r.Item?.Id).OfType<long>());
            var harbor = arranged.Rows[2].Item!;
            Assert.Equal(("02", "Harbor map", "Read aloud", "Bring dice"), (harbor.Number, harbor.Heading, harbor.Note, arranged.Notes));
            Assert.Equal(["Move to Before the first section"], harbor.Moves.Select(m => m.Label));
            Assert.StartsWith("Removed ", removed.Message, StringComparison.Ordinal);
            Assert.Equal("1 item · Your order, your notes", afterRemove!.Count);
            Assert.Equal(("Harbor night", "In Harbor night", 2), (scoped!.Scope?.Heading, scoped.Scope?.Chip, scoped.Books.Count));
            Assert.Equal(("Harbor night (copy)", "BINDER", 3), (copied!.Title, copied.Eyebrow, copied.Rows.Count));
            Assert.Equal("Deleted Harbor night. Its books are still in your library.", deleted.Message);
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Equal(3, restored!.Rows.Count);

            async Task<T> SendAsync<T>(HttpMethod method, string url, object? body)
            {
                using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>(ct))!;
            }
        }

        [Fact]
        public async Task Add_to_binder_leads_with_the_binder_worked_on_last_and_undo_takes_out_what_it_added()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/Maps/harbor.png", Png());
            _graph.Add("RPG/Maps/tower.png", Png(SkiaSharp.SKColors.Crimson));
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var books = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 2 } two ? two : null, ct);

            var empty = await client.GetFromJsonAsync<BinderMenu>("/api/binders/menu", ct);
            var first = await SendAsync<BinderCreated>(HttpMethod.Post, "/api/binders", new BinderEdit("Harbor night", null, false, null));
            var second = await SendAsync<BinderCreated>(HttpMethod.Post, "/api/binders", new BinderEdit("Tower climb", null, false, null));
            await SendAsync<BinderPage>(HttpMethod.Post, $"/api/binders/{first.Id}/open", null);
            var menu = await client.GetFromJsonAsync<BinderMenu>("/api/binders/menu", ct);
            var one = await SendAsync<ActionResult>(HttpMethod.Post, $"/api/binders/{first.Id}/add", new BinderAdd([books[0].EntryId], "Harbor map"));
            var both = await SendAsync<ActionResult>(HttpMethod.Post, $"/api/binders/{second.Id}/add", new BinderAdd([.. books.Select(b => b.EntryId)]));
            var page = await SendAsync<ActionResult>(HttpMethod.Post, $"/api/binders/{first.Id}/add", new BinderAdd([], DocumentId: books[1].DocumentId, Page: 0));
            var withAll = await client.GetFromJsonAsync<BinderPage>($"/api/binders/{first.Id}", ct);
            await SendAsync<ActionResult>(HttpMethod.Post, $"/api/library/held/{both.Undo}", null);
            var undone = await client.GetFromJsonAsync<BinderPage>($"/api/binders/{second.Id}", ct);
            using var missing = await client.PostAsJsonAsync("/api/binders/999999/add", new BinderAdd([books[0].EntryId]), ct);

            Assert.Equal([BinderMenuKind.New], empty!.Choices.Select(c => c.Kind));
            Assert.Equal(["Add to Harbor night", "Add to another binder…", "Add to a new binder…"], menu!.Choices.Select(c => c.Label));
            Assert.Equal(first.Id, menu.Choices[0].Id);
            Assert.Equal(2, menu.All.Count);
            Assert.Equal(("Add to a new binder", "Create and add"), (menu.NewHeading, menu.NewAction));
            Assert.Equal("Added Harbor map to Harbor night.", one.Message);
            Assert.Equal("Added 2 books to Tower climb.", both.Message);
            Assert.Equal("Added page 1 to Harbor night.", page.Message);
            Assert.Equal(2, withAll!.Rows.Count(r => r.Item is not null));
            Assert.Empty(undone!.Rows);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            async Task<T> SendAsync<T>(HttpMethod method, string url, object? body)
            {
                using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>(ct))!;
            }
        }

        [Fact]
        public async Task A_sent_online_library_page_previews_imports_with_its_covers_and_undoes_as_the_windows_app_does()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var folder = Path.Combine(AppContext.BaseDirectory, "online");

            var services = (await client.GetFromJsonAsync<List<OnlineService>>("/api/online/services", ct))!;
            using var page = new StringContent(await File.ReadAllTextAsync(Path.Combine(folder, "dndbeyond-library.html"), ct), System.Text.Encoding.UTF8, "text/html");
            using var pageResponse = await client.PostAsync("/api/online/pages", page, ct);
            var read = (await pageResponse.Content.ReadFromJsonAsync<OnlinePageRead>(ct))!;
            using var notPage = new StringContent("<html><body>Hello</body></html>", System.Text.Encoding.UTF8, "text/html");
            using var notPageResponse = await client.PostAsync("/api/online/pages", notPage, ct);
            var unread = (await notPageResponse.Content.ReadFromJsonAsync<OnlinePageRead>(ct))!;
            var preview = read.Preview!;
            using var picture = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(folder, "dndbeyond-library_files", "639000000000000001.jpeg"), ct));
            using var pictureResponse = await client.PutAsync($"/api/online/pages/{preview.Token}/images/639000000000000001.jpeg", picture, ct);
            var withCover = (await pictureResponse.Content.ReadFromJsonAsync<OnlinePreview>(ct))!;
            using var importResponse = await client.PostAsJsonAsync($"/api/online/pages/{preview.Token}/import",
                new OnlineImportChoice([.. preview.Groups.SelectMany(g => g.Rows).Select(r => r.Index)]), ct);
            var imported = (await importResponse.Content.ReadFromJsonAsync<OnlineImported>(ct))!;
            var books = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books;
            using (await client.PostAsync($"/api/library/held/{imported.Result.Undo}", null, ct)) { }
            var afterUndo = (await client.GetFromJsonAsync<OnlinePreview>($"/api/online/pages/{preview.Token}", ct))!;
            using var gone = await client.GetAsync("/api/online/pages/0123456789abcdef0123456789abcdef", ct);

            Assert.Equal(["D&D Beyond", "Demiplane", "Humble Bundle", "DriveThruRPG"], services.Select(s => s.Name));
            // Read on the service's own site, or bought from it as files: Settings groups them so.
            Assert.Equal([true, true, false, false], services.Select(s => s.ReadOnline));
            Assert.Contains("Only books you own are imported", services[0].Explanation, StringComparison.Ordinal);
            Assert.Null(read.Problem);
            Assert.Equal(("D&D Beyond", "Your D&D Beyond library: 2 books. 1 book shared with you was left out."), (preview.Service, preview.Summary));
            Assert.Equal(["New · 2"], preview.Groups.Select(g => g.Heading));
            Assert.Equal("Each becomes a book owned elsewhere, opening on D&D Beyond.", preview.Groups[0].Explanation);
            Assert.Equal("Gilded Press · adventure · 5e · 2019", preview.Groups[0].Rows[0].Detail);
            Assert.Equal((2, 0, 1), (preview.Covers, preview.CoversFound, withCover.CoversFound));
            // What Send to Bibliotaph fetches: the covers still missing, by their addresses in the page.
            Assert.Equal(2, preview.Wanted!.Count);
            Assert.DoesNotContain(withCover.Wanted!, url => url.EndsWith("639000000000000001.jpeg", StringComparison.Ordinal));
            Assert.Single(withCover.Wanted!);
            Assert.Equal(OnlineImportText.Unreadable, unread.Problem);
            Assert.Equal("Added 2 books owned on D&D Beyond.", imported.Result.Message);
            // The book whose picture wasn't sent is offered again, for its cover, and asks for just that picture.
            Assert.Equal(["Imported before, with more to fill in · 1", "Imported before · 1"], imported.Preview.Groups.Select(g => g.Heading));
            Assert.Equal("Gets its cover back.", imported.Preview.Groups[0].Rows.Single().Detail);
            Assert.Equal((1, 0), (imported.Preview.Covers, imported.Preview.CoversFound));
            Assert.Equal(2, books.Count(b => b.Title is "The Ember Vault" or "Atlas of the Drowned Coast"));
            Assert.Equal(["New · 2"], afterUndo.Groups.Select(g => g.Heading));
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        /// <summary>Signs in whoever the test header names, in place of the session cookie.</summary>
        [Fact]
        public async Task The_reader_says_why_a_book_owned_elsewhere_has_nothing_to_open_and_keeps_to_the_users_own_books()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using var add = await client.PostAsJsonAsync("/api/library/elsewhere", new ElsewhereAdd("Tomb of the Test", "", "", "", [], "", null), ct);
            var entryId = (await add.Content.ReadFromJsonAsync<ElsewhereAdded>(ct))!.EntryId!.Value;

            var elsewhere = (await client.GetFromJsonAsync<ReaderBook>($"/api/library/books/{entryId}/reader", ct))!;
            using var missing = await client.GetAsync("/api/library/books/999999/reader", ct);
            using var stranger = await Client(await AddUserAsync(admin: false)).GetAsync($"/api/library/books/{entryId}/reader", ct);
            var link = (await client.GetFromJsonAsync<ReaderLink>("/api/library/documents/999999/link", ct))!;
            var find = (await client.GetFromJsonAsync<ReaderFind>("/api/library/documents/999999/find?q=owlbear", ct))!;

            Assert.Equal("Nothing to open here.", elsewhere.Problem?.Title);
            Assert.Equal("Tomb of the Test", elsewhere.Title);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
            Assert.Null(link.Url);
            Assert.NotNull(link.Problem);
            Assert.Empty(find.Pages);
        }

        [Fact]
        public async Task A_book_owned_elsewhere_is_added_with_its_cover_and_removed_with_undo_as_the_windows_app_does()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            var choices = (await client.GetFromJsonAsync<ElsewhereChoices>("/api/library/elsewhere/choices", ct))!;
            using var notAPicture = await client.PostAsync("/api/library/elsewhere/cover", new ByteArrayContent([1, 2, 3]), ct);
            var refused = (await notAPicture.Content.ReadFromJsonAsync<ChosenCoverRead>(ct))!;
            using var picture = await client.PostAsync("/api/library/elsewhere/cover", new ByteArrayContent(Png()), ct);
            var read = (await picture.Content.ReadFromJsonAsync<ChosenCoverRead>(ct))!;
            using var untitled = await client.PostAsJsonAsync("/api/library/elsewhere", new ElsewhereAdd(" ", null, null, null, [], "", null), ct);
            var noTitle = (await untitled.Content.ReadFromJsonAsync<ElsewhereAdded>(ct))!;
            var added = await PostAsync<ElsewhereAdded>("/api/library/elsewhere",
                new ElsewhereAdd("Tomb of the Test", "", "", "Test Press", [choices.Owns[0]], "", read.Cover));
            var entryId = added.EntryId!.Value;
            var details = (await client.GetFromJsonAsync<BookDetails>($"/api/library/books/{entryId}", ct))!;

            using var removeCover = await client.DeleteAsync($"/api/library/books/{entryId}/chosen-cover", ct);
            var withoutCover = (await removeCover.Content.ReadFromJsonAsync<BookDetails>(ct))!;
            using var badCover = await client.PutAsync($"/api/library/books/{entryId}/chosen-cover", new ByteArrayContent([1, 2, 3]), ct);
            using var goodCover = await client.PutAsync($"/api/library/books/{entryId}/chosen-cover", new ByteArrayContent(Png()), ct);
            var withCover = (await goodCover.Content.ReadFromJsonAsync<BookDetails>(ct))!;

            using var remove = await client.DeleteAsync($"/api/library/books/{entryId}/elsewhere", ct);
            var removed = (await remove.Content.ReadFromJsonAsync<ActionResult>(ct))!;
            var afterRemove = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!;
            await PostAsync<ActionResult>($"/api/library/held/{removed.Undo}", null);
            var afterUndo = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!;

            Assert.Equal((ElsewhereText.Heading, "Print"), (choices.Heading, choices.Owns[0]));
            Assert.Equal((null, "That file isn't a picture Bibliotaph can read."), (refused.Cover, refused.Problem));
            Assert.NotNull(read.Cover);
            Assert.Null(noTitle.EntryId);
            Assert.NotNull(noTitle.Problem);
            Assert.Equal(("Tomb of the Test", "Test Press", true, true), (details.Card.Title, details.Publisher, details.Card.Elsewhere, details.HasChosenCover));
            Assert.NotNull(details.Card.Cover);
            Assert.False(withoutCover.HasChosenCover);
            Assert.Equal(HttpStatusCode.BadRequest, badCover.StatusCode);
            Assert.True(withCover.HasChosenCover);
            Assert.Equal("Removed Tomb of the Test from your library.", removed.Message);
            Assert.Empty(afterRemove.Books);
            Assert.Equal(["Tomb of the Test"], afterUndo.Books.Select(b => b.Title));

            async Task<T> PostAsync<T>(string url, object? body)
            {
                using var response = body is null ? await client.PostAsync(url, null, ct) : await client.PostAsJsonAsync(url, body, ct);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>(ct))!;
            }
        }
    }
}
