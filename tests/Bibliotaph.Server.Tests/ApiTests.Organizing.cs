using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Collections, filters, saved searches and Select mode, as the Windows app keeps them.</summary>
    public sealed class OrganizingTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task Collections_are_made_filled_and_changed_as_the_windows_app_changes_them()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/harbor.png", Png());
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var book = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.SingleOrDefault(b => b.Cover is not null), ct);

            using var unnamed = await client.PostAsJsonAsync("/api/collections", new CollectionName("  ", null), ct);
            var campaign = await CreateAsync(new CollectionName("Campaign", "The sea campaign"));
            var maps = await CreateAsync(new CollectionName("Maps", null, campaign.Id));
            var added = await SendAsync(client.PostAsJsonAsync($"/api/collections/{maps.Id}/books", new CollectionBooks([book.EntryId, 999_999], "Harbor"), ct));
            var again = await SendAsync(client.PostAsJsonAsync($"/api/collections/{maps.Id}/books", new CollectionBooks([book.EntryId], "Harbor"), ct));
            var inside = await client.GetFromJsonAsync<LibraryResults>($"/api/library?collection={campaign.Id}", ct);
            var onlyHere = await client.GetFromJsonAsync<LibraryResults>($"/api/library?collection={campaign.Id}&addedHere=true", ct);
            var page = await client.GetFromJsonAsync<CollectionPage>($"/api/collections/{campaign.Id}", ct);
            var details = await client.GetFromJsonAsync<BookDetails>($"/api/library/books/{book.EntryId}", ct);
            var list = await client.GetFromJsonAsync<CollectionList>("/api/collections", ct);
            using var intoItself = await client.PutAsJsonAsync($"/api/collections/{campaign.Id}/parent", new CollectionMove(maps.Id), ct);
            var moved = await SendAsync(client.PutAsJsonAsync($"/api/collections/{maps.Id}/parent", new CollectionMove(null), ct));
            var renamed = await SendAsync(client.PutAsJsonAsync($"/api/collections/{maps.Id}", new CollectionName("Charts", null), ct));
            var removed = await SendAsync(client.PostAsJsonAsync($"/api/collections/{maps.Id}/books/remove", new CollectionBooks([book.EntryId], "Harbor"), ct));
            var putBack = await SendAsync(client.PostAsync($"/api/library/held/{removed.Undo}", null, ct));
            var deleted = await SendAsync(client.DeleteAsync($"/api/collections/{maps.Id}", ct));
            var afterDelete = await client.GetFromJsonAsync<List<CollectionCard>>("/api/collections/cards", ct);
            await SendAsync(client.PostAsync($"/api/library/held/{deleted.Undo}", null, ct));
            var cards = await client.GetFromJsonAsync<List<CollectionCard>>("/api/collections/cards", ct);
            var other = Client(await AddUserAsync(admin: false));
            using var stranger = await other.GetAsync($"/api/collections/{campaign.Id}", ct);

            Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            Assert.Equal("Campaign › Maps", maps.Path);
            // A book from another library is left out rather than added.
            Assert.Equal("Added Harbor to Maps.", added.Message);
            Assert.NotNull(added.Undo);
            Assert.Equal(("Harbor is in Maps already.", null), (again.Message, again.Undo));
            Assert.Equal([book.EntryId], inside!.Books.Select(b => b.EntryId));
            Assert.Equal(CollectionText.EmptyTitle, onlyHere!.Empty?.Title);
            Assert.Equal(("Campaign", "The sea campaign"), (page!.Collection.Name, page.Subtitle));
            Assert.Equal(["Maps"], page.Children.Select(c => c.Name));
            Assert.Equal("1 book", page.Children[0].Count);
            Assert.Equal(["Campaign › Maps"], details!.Collections.Select(c => c.Path));
            Assert.Equal(["Campaign", "Campaign › Maps"], list!.Tree.Select(c => c.Path));
            Assert.Equal([0, 1], list.Depths);
            Assert.Equal(["Add to Campaign", "Add to Maps", CollectionText.NewItem], list.Menu.Select(m => m.Label));
            Assert.Equal(HttpStatusCode.NotFound, intoItself.StatusCode);
            Assert.Equal("Moved Maps to the top level.", moved.Message);
            Assert.Equal("Renamed Maps to Charts.", renamed.Message);
            Assert.Equal("Removed Harbor from Charts.", removed.Message);
            Assert.Equal("", putBack.Message);
            Assert.Equal("Deleted Charts. Its books are still in your library.", deleted.Message);
            Assert.Equal(["Campaign"], afterDelete!.Select(c => c.Name));
            Assert.Equal(["Campaign", "Charts"], cards!.Select(c => c.Name));
            Assert.Equal("1 book", cards!.Single(c => c.Name == "Charts").Count);
            Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);

            async Task<CollectionNode> CreateAsync(CollectionName name)
            {
                using var response = await client.PostAsJsonAsync("/api/collections", name, ct);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<CollectionCreated>(ct))!.Collection;
            }

            async Task<ActionResult> SendAsync(Task<HttpResponseMessage> request)
            {
                using var response = await request;
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<ActionResult>(ct))!;
            }
        }

        [Fact]
        public async Task A_type_ahead_filter_offers_the_values_its_books_have_and_filters_by_those_picked()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/harbor.png", Png());
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var book = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.SingleOrDefault(b => b.Cover is not null), ct);
            using (await client.PostAsJsonAsync($"/api/library/books/{book.EntryId}/fields/publisher", new FieldEdit("Harbor Press"), ct)) { }

            var offered = await client.GetFromJsonAsync<List<ValueSuggestion>>("/api/library/values/publisher?text=harb", ct);
            var nothing = await client.GetFromJsonAsync<List<ValueSuggestion>>("/api/library/values/publisher?text=zzz", ct);
            using var unknownField = await client.GetAsync("/api/library/values/title?text=harb", ct);
            var picked = Uri.EscapeDataString($$"""{"publisher":["{{offered![0].Value}}"]}""");
            var kept = await client.GetFromJsonAsync<LibraryResults>($"/api/library?picked={picked}", ct);
            var none = await client.GetFromJsonAsync<LibraryResults>($"/api/library?picked={Uri.EscapeDataString("""{"publisher":["Nobody"]}""")}", ct);
            var unreadable = await client.GetFromJsonAsync<LibraryResults>("/api/library?picked=nonsense", ct);

            Assert.Equal([("Harbor Press", "1")], offered.Select(s => (s.Label, s.Count)));
            Assert.Equal("Harbor Press, 1 book", offered[0].Spoken);
            Assert.Empty(nothing!);
            Assert.Equal(HttpStatusCode.NotFound, unknownField.StatusCode);
            Assert.Equal([book.EntryId], kept!.Books.Select(b => b.EntryId));
            Assert.Equal([new FilterChoice(offered[0].Value, "Harbor Press")], kept.Menus.Boxes.Single(b => b.Key == "publisher").Picked);
            Assert.True(kept.Menus.Boxes.Single(b => b.Key == "publisher").Offered);
            Assert.Empty(none!.Books);
            Assert.Equal([new FilterChoice("Nobody", "Nobody")], none.Menus.Boxes.Single(b => b.Key == "publisher").Picked);
            Assert.Single(unreadable!.Books);
        }

        [Fact]
        public async Task Saved_searches_pins_and_favorites_work_as_the_windows_app_keeps_them()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/harbor.png", Png());
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var book = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.SingleOrDefault(b => b.Cover is not null), ct);

            var noFavorites = await client.GetFromJsonAsync<LibraryResults>("/api/library?favorites=true", ct);
            using (await client.PutAsJsonAsync($"/api/library/books/{book.EntryId}/favorite", new SetFavorite(true), ct)) { }
            var favorites = await client.GetFromJsonAsync<LibraryResults>("/api/library?favorites=true", ct);
            using var unnamed = await client.PostAsJsonAsync("/api/searches", new SavedSearchSave(" ", new LibraryQuery(), false), ct);
            using var saving = await client.PostAsJsonAsync("/api/searches",
                new SavedSearchSave("Maps", new LibraryQuery { Q = "harbor", Sort = "Title", Kind = "Images", Favorites = true }, true), ct);
            var created = (await saving.Content.ReadFromJsonAsync<SavedSearchCreated>(ct))!;
            var id = created.Search.Id;
            var pinned = await client.GetFromJsonAsync<PinList>("/api/pins", ct);
            var updated = await SendAsync(client.PutAsJsonAsync($"/api/searches/{id}", new SavedSearchChange(new LibraryQuery { System = "dnd5e" }, false), ct));
            var afterUpdate = await client.GetFromJsonAsync<SavedSearch>($"/api/searches/{id}", ct);
            await SendAsync(client.PostAsync($"/api/library/held/{updated.Undo}", null, ct));
            var undone = await client.GetFromJsonAsync<SavedSearch>($"/api/searches/{id}", ct);
            await SendAsync(client.PutAsJsonAsync($"/api/searches/{id}/name", new SavedSearchRename("Harbor maps"), ct));
            await client.PostAsJsonAsync("/api/pins", new PinRef(PinKinds.Book, book.EntryId), ct);
            using var nowhere = await client.PostAsJsonAsync("/api/pins", new PinRef(PinKinds.Collection, 999_999), ct);
            using var moving = await client.PostAsJsonAsync("/api/pins/move", new PinMove(PinKinds.Book, book.EntryId, 0), ct);
            var moved = (await moving.Content.ReadFromJsonAsync<PinList>(ct))!;
            var deleted = await SendAsync(client.DeleteAsync($"/api/searches/{id}", ct));
            var afterDelete = await client.GetFromJsonAsync<List<SavedSearchInfo>>("/api/searches", ct);
            using var gone = await client.GetAsync($"/api/searches/{id}", ct);
            await SendAsync(client.PostAsync($"/api/library/held/{deleted.Undo}", null, ct));
            var restored = await client.GetFromJsonAsync<List<SavedSearchInfo>>("/api/searches", ct);
            using var unpinning = await client.PostAsJsonAsync("/api/pins/remove", new PinRef(PinKinds.Favorites, null), ct);
            var unpinned = (await unpinning.Content.ReadFromJsonAsync<PinList>(ct))!;

            Assert.Equal(SavedSearchText.FavoritesEmptyTitle, noFavorites!.Empty?.Title);
            Assert.Equal([book.EntryId], favorites!.Books.Select(b => b.EntryId));
            Assert.Equal(new ScopeText("Favorites", SavedSearchText.FavoritesSubtitle, "In Favorites"), favorites.Scope);
            Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            Assert.Equal("Saved Maps. It's pinned in the sidebar.", created.Result.Message);
            Assert.Equal(("harbor", "Title", "Images", true, true), (created.Search.Query.Q, created.Search.Query.Sort, created.Search.Query.Kind, created.Search.Query.Favorites, created.Search.List));
            Assert.Equal(("Maps (2)", true), (created.Search.CopyName, created.Search.Pinned));
            // Favorites starts pinned, as in the Windows app.
            Assert.Equal([("Favorites", "library?favorites=true", "Heart"), ("Maps", $"library?view={id}", "SlidersHorizontal")], pinned!.Items.Select(i => (i.Label, i.Href, i.Icon)));
            Assert.Equal("Updated Maps.", updated.Message);
            Assert.Equal(("dnd5e", null, "RecentlyAdded"), (afterUpdate!.Query.System, afterUpdate.Query.Q, afterUpdate.Query.Sort));
            Assert.Equal("harbor", undone!.Query.Q);
            Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
            Assert.Equal([PinKinds.Book, PinKinds.Favorites, PinKinds.Search], moved.Items.Select(i => i.Kind));
            Assert.Equal("Harbor maps", moved.Items[2].Label);
            Assert.Equal("Deleted the saved search Harbor maps. Your books are untouched.", deleted.Message);
            Assert.Empty(afterDelete!);
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Equal([("Harbor maps", true)], restored!.Select(s => (s.Name, s.Pinned)));
            Assert.Equal([PinKinds.Book, PinKinds.Search], unpinned.Items.Select(i => i.Kind));

            async Task<ActionResult> SendAsync(Task<HttpResponseMessage> request)
            {
                using var response = await request;
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<ActionResult>(ct))!;
            }
        }

        [Fact]
        public async Task Select_mode_packs_joins_and_exports_the_ticked_books_as_the_windows_app_does()
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
            var books = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 2 } two && two.All(b => b.Cover is not null) ? two : null, ct);
            List<long> ids = [.. books.Select(b => b.EntryId)];

            var none = await PostAsync<SelectionInfo>("/api/library/selection", new SelectionQuery([], 0));
            var one = await PostAsync<SelectionInfo>("/api/library/selection", new SelectionQuery([ids[0]], 0));
            var both = await PostAsync<SelectionInfo>("/api/library/selection", new SelectionQuery([.. ids, 999_999], 1));
            var packing = await PostAsync<PackTogetherPreview>("/api/library/selection/pack", new SelectedBooks(ids));
            using var unnamed = await client.PostAsJsonAsync("/api/library/selection/pack/make", new PackTogetherRequest(ids, " "), ct);
            var packed = await PostAsync<ActionResult>("/api/library/selection/pack/make", new PackTogetherRequest(ids, "Harbor maps"));
            var afterPack = await client.GetFromJsonAsync<LibraryResults>("/api/library", ct);
            using var again = await client.PostAsJsonAsync("/api/library/selection/pack/make", new PackTogetherRequest(ids, "Harbor maps"), ct);
            await PostAsync<ActionResult>($"/api/library/held/{packed.Undo}", null);
            var unpacked = await client.GetFromJsonAsync<LibraryResults>("/api/library", ct);
            var joining = await PostAsync<SameBookPreview>("/api/library/selection/same-book", new SelectedBooks(ids));
            var joined = await PostAsync<ActionResult>("/api/library/selection/same-book/join", new SameBookJoin(ids, joining.Opens));
            var afterJoin = await client.GetFromJsonAsync<LibraryResults>("/api/library", ct);
            await PostAsync<ActionResult>($"/api/library/held/{joined.Undo}", null);
            var apart = await client.GetFromJsonAsync<LibraryResults>("/api/library", ct);
            var csv = await PostAsync<ExportedCsv>("/api/library/selection/export", new SelectedBooks(ids));
            using var noAdventures = await client.PostAsJsonAsync("/api/library/selection/remove-adventures", new SelectedBooks(ids), ct);

            Assert.Equal("Click books to select them. Shift+click selects a range, Ctrl+A everything shown.", none.Summary);
            Assert.Equal(("1 selected", "1 book", false, false, false), (one.Summary, one.Books, one.CanJoin, one.CanPack, one.CanRemoveAdventures));
            // A ticked book no longer in the library is left out of what fits, but still counted, as the bar counts ticks.
            Assert.Equal(("3 selected, 1 not shown", true, true, false), (both.Summary, both.CanJoin, both.CanPack, both.CanRemoveAdventures));
            Assert.Equal(("IMAGE PACK", "Make a pack of 2 images", "Maps"), (packing.Eyebrow, packing.Heading, packing.Name));
            Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            Assert.Equal("Made a pack of 2 images: Harbor maps.", packed.Message);
            Assert.Equal(["Harbor maps"], afterPack!.Books.Select(b => b.Title));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal(2, unpacked!.Books.Count);
            Assert.Equal(("Join 2 cards as one book", null, 2), (joining.Heading, joining.Elsewhere, joining.Files.Count));
            Assert.Equal(["harbor.png", "tower.png"], joining.Files.Select(f => f.Name).Order());
            Assert.StartsWith("Joined 2 cards as ", joined.Message, StringComparison.Ordinal);
            Assert.Single(afterJoin!.Books);
            Assert.Equal(2, apart!.Books.Count);
            Assert.Equal(("Bibliotaph books.csv", "Exported 2 books to Bibliotaph books.csv."), (csv.Name, csv.Message));
            var lines = csv.Csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("Title,System,Type", lines[0], StringComparison.Ordinal);
            Assert.Contains(lines, l => l.EndsWith("OneDrive · Dana › RPG › Maps › harbor.png", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.NotFound, noAdventures.StatusCode);

            async Task<T> PostAsync<T>(string url, object? body)
            {
                using var response = body is null ? await client.PostAsync(url, null, ct) : await client.PostAsJsonAsync(url, body, ct);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>(ct))!;
            }
        }

        [Fact]
        public async Task Edit_metadata_sets_adds_and_removes_on_every_ticked_book_with_review_and_undo()
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
            var books = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 2 } two && two.All(b => b.Cover is not null) ? two : null, ct);
            List<long> ids = [.. books.Select(b => b.EntryId)];

            var form = await PostAsync<BulkEditForm>("/api/library/selection/edit", new SelectedBooks([.. ids, 999_999]));
            var system = await PostAsync<BulkReadResult>("/api/library/selection/edit/read", new BulkReadRequest("system", "PF", 2, true));
            var tags = await PostAsync<BulkReadResult>("/api/library/selection/edit/read", new BulkReadRequest("tags", "heist, harbor", 2, false));
            var year = await PostAsync<BulkReadResult>("/api/library/selection/edit/read", new BulkReadRequest("year", "soon", 2, true));
            var set = system.Values[0];
            List<BulkFieldChange> changes =
            [
                new("system", BulkEditAction.Set, set.Value, set.Text, set.Normalized, set.NewNote is not null),
                .. tags.Values.Select(t => new BulkFieldChange("tags", BulkEditAction.Add, t.Value, t.Text, t.Normalized, t.NewNote is not null)),
            ];
            var review = await PostAsync<List<BulkSummaryItem>>("/api/library/selection/edit/review", new BulkEditRequest(ids, changes));
            var applied = await PostAsync<BulkApplyResult>("/api/library/selection/edit/apply", new BulkEditRequest(ids, changes));
            var after = await PostAsync<BulkEditForm>("/api/library/selection/edit", new SelectedBooks(ids));
            var heist = after.Fields.Single(f => f.Key == "tags").Chips.Single(c => c.Text == "heist");
            BulkEditRequest removing = new(ids, [new("tags", BulkEditAction.Remove, heist.Value, heist.Text, heist.Normalized), new("system", BulkEditAction.Reset)]);
            var reviewRemove = await PostAsync<List<BulkSummaryItem>>("/api/library/selection/edit/review", removing);
            var removed = await PostAsync<BulkApplyResult>("/api/library/selection/edit/apply", removing);
            var afterRemove = await PostAsync<BulkEditForm>("/api/library/selection/edit", new SelectedBooks(ids));
            await PostAsync<ActionResult>($"/api/library/held/{removed.Done!.Undo}", null);
            var undone = await PostAsync<BulkEditForm>("/api/library/selection/edit", new SelectedBooks(ids));

            // A ticked book no longer in the library is left out.
            Assert.Equal(("EDIT METADATA", "Edit 2 books", "Apply to 2 books", 2), (form.Eyebrow, form.Heading, form.ApplyLabel, form.Books));
            Assert.DoesNotContain(form.Fields, f => f.Key == "title");
            var before = form.Fields.Single(f => f.Key == "system");
            Assert.Equal(("Not set", false, false), (before.Current, before.IsKnown, before.CanReset));
            Assert.Equal((null, "Will be Pathfinder on every book"), (system.Problem, system.Pending));
            Assert.Equal(["heist", "harbor"], tags.Values.Select(v => v.Text));
            Assert.All(tags.Values, v => Assert.True(v.Typed));
            Assert.Equal("Don't add heist", tags.Values[0].Adding.RemoveName);
            Assert.Equal("A year looks like 2019.", year.Problem);
            Assert.Equal(["Game system: Set to Pathfinder on 2 books.", "Your tags: Add heist to 2 books.", "Your tags: Add harbor to 2 books."],
                review.Select(l => $"{l.Field}: {l.Text}"));
            Assert.Equal((null, "Edited 2 books."), (applied.Problem, applied.Done?.Message));
            Assert.NotNull(applied.Done!.Undo);
            var system2 = after.Fields.Single(f => f.Key == "system");
            Assert.Equal(("Pathfinder", true), (system2.Current, system2.CanReset));
            Assert.Equal(("2 of 2", "0 of 2"), (heist.Idle.Count, heist.Removing.Count));
            Assert.Equal(["Your tags: Remove heist from 2 books.", "Game system: Back to suggestions on 2 books."],
                reviewRemove.Select(l => $"{l.Field}: {l.Text}").OrderBy(l => l.StartsWith("Game", StringComparison.Ordinal)));
            Assert.Contains(reviewRemove, l => l.Warning == "Takes away your own heist on 2 books.");
            Assert.Equal(["harbor"], afterRemove.Fields.Single(f => f.Key == "tags").Chips.Select(c => c.Text));
            Assert.Equal("Not set", afterRemove.Fields.Single(f => f.Key == "system").Current);
            Assert.Equal(["harbor", "heist"], undone.Fields.Single(f => f.Key == "tags").Chips.Select(c => c.Text).Order());
            Assert.Equal("Pathfinder", undone.Fields.Single(f => f.Key == "system").Current);

            async Task<T> PostAsync<T>(string url, object? body)
            {
                using var response = body is null ? await client.PostAsync(url, null, ct) : await client.PostAsJsonAsync(url, body, ct);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>(ct))!;
            }
        }
    }
}
