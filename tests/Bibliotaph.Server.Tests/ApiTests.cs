using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Ai;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Tests;

public sealed class ApiTests : IDisposable, IClassFixture<SyntheticPdfs>
{
    readonly TempFolder _folder = new();
    readonly FakeGraph _graph = new();
    readonly FakeDropbox _dropbox = new();
    readonly FakeModelService _models = new();
    readonly SyntheticPdfs _pdfs;
    readonly WebApplicationFactory<Program> _factory;

    public ApiTests(SyntheticPdfs pdfs)
    {
        _pdfs = pdfs;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("Server:DataRoot", _folder.Path);
            host.ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(new FakeHttp(new FakeHosts(_graph, _dropbox))));
            host.ConfigureServices(services => services.AddSingleton(new ModelClientFactory(_models.ClientFor, TimeSpan.FromMilliseconds(50))));
            host.ConfigureServices(services => services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestSession>(AuthSetup.SessionScheme + "-test", _ => { }));
            host.ConfigureServices(services => services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultAuthenticateScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultChallengeScheme = AuthSetup.SessionScheme + "-test";
            }));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _folder.Dispose();
    }

    HttpClient Client(User? user = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add(TestSession.Header, $"{user.Id};{user.Name};{user.IsAdmin}");
        return client;
    }

    async Task<User> AddUserAsync(bool admin)
    {
        var accounts = _factory.Services.GetRequiredService<AccountService>();
        var (code, _) = await accounts.CreateInviteAsync(null, null, admin);
        var outcome = await accounts.SignInAsync(new ExternalIdentity("google", Guid.NewGuid().ToString(), "Dana", null), code, null);
        return outcome.User!;
    }

    [Fact]
    public async Task Without_a_session_the_API_answers_401()
    {
        using var response = await Client().GetAsync("/api/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Closing_an_account_deletes_its_library_and_ends_its_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        (await client.GetAsync("/api/library/summary", ct)).EnsureSuccessStatusCode();
        var folder = _factory.Services.GetRequiredService<ServerPaths>().UserRoot(user.Id);
        Assert.True(Directory.Exists(folder));

        using var closed = await client.DeleteAsync("/api/me", ct);

        closed.EnsureSuccessStatusCode();
        Assert.Equal("/", (await closed.Content.ReadFromJsonAsync<SignedOut>(ct))!.Next);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(0, _factory.Services.GetRequiredService<UserLibraries>().OpenCount);
        using var after = await client.GetAsync("/api/library/summary", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task An_admin_deletes_someone_elses_account_but_not_their_own()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AddUserAsync(admin: true);
        var user = await AddUserAsync(admin: false);
        var theirs = Client(user);
        (await theirs.GetAsync("/api/library/summary", ct)).EnsureSuccessStatusCode();
        var folder = _factory.Services.GetRequiredService<ServerPaths>().UserRoot(user.Id);
        var client = Client(admin);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        var listed = await client.GetFromJsonAsync<List<AccountInfo>>("/api/accounts", ct);
        using var own = await client.DeleteAsync($"/api/accounts/{admin.Id}", ct);
        using var deleted = await client.DeleteAsync($"/api/accounts/{user.Id}", ct);

        Assert.Contains(listed!, a => a.Id == admin.Id && a.IsYou);
        Assert.Contains(listed!, a => a.Id == user.Id && !a.IsYou);
        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
        deleted.EnsureSuccessStatusCode();
        Assert.StartsWith("Deleted Dana's account", (await deleted.Content.ReadFromJsonAsync<ActionResult>(ct))!.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
        using var after = await theirs.GetAsync("/api/library/summary", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.DoesNotContain(await client.GetFromJsonAsync<List<AccountInfo>>("/api/accounts", ct) ?? [], a => a.Id == user.Id);
    }

    [Fact]
    public async Task Only_an_admin_may_list_or_delete_accounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var other = await AddUserAsync(admin: false);
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        using var listed = await client.GetAsync("/api/accounts", ct);
        using var deleted = await client.DeleteAsync($"/api/accounts/{other.Id}", ct);

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
    }

    [Fact]
    public async Task Me_describes_the_signed_in_user()
    {
        var user = await AddUserAsync(admin: false);

        var me = await Client(user).GetFromJsonAsync<CurrentUser>("/api/me", TestContext.Current.CancellationToken);

        Assert.Equal(user.Id, me!.UserId);
        Assert.Equal("Google", Assert.Single(me.SignIns).Provider);
    }

    [Fact]
    public async Task A_new_users_library_is_empty()
    {
        var user = await AddUserAsync(admin: false);

        var summary = await Client(user).GetFromJsonAsync<LibrarySummary>("/api/library/summary", TestContext.Current.CancellationToken);

        Assert.Equal(new LibrarySummary(0, 0), summary);
        Assert.True(File.Exists(Path.Combine(_folder.Path, "users", user.Id, "catalog.db")));
    }

    [Fact]
    public async Task A_new_users_library_page_asks_for_a_folder()
    {
        var user = await AddUserAsync(admin: false);

        var results = await Client(user).GetFromJsonAsync<LibraryResults>("/api/library?sort=Title&kind=Books&level=3&unknownLevels=true",
            TestContext.Current.CancellationToken);

        Assert.Empty(results!.Books);
        Assert.Equal("add-folder", results.Empty?.Action);
        Assert.Equal("All systems", Assert.Single(results.Menus.Systems).Label);
    }

    [Fact]
    public async Task A_book_that_isnt_in_the_library_has_no_details()
    {
        var user = await AddUserAsync(admin: false);

        using var missing = await Client(user).GetAsync("/api/library/books/42", TestContext.Current.CancellationToken);
        using var stranger = await Client().GetAsync("/api/library/books/42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
    }

    [Fact]
    public async Task A_books_details_are_changed_as_the_windows_inspector_changes_them()
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
        var path = $"/api/library/books/{book.EntryId}";

        using var titled = await client.PostAsJsonAsync($"{path}/fields/title", new FieldEdit("Harbor of Lost Ships"), ct);
        var edit = (await titled.Content.ReadFromJsonAsync<EditResult>(ct))!;
        using var unknownField = await client.PostAsJsonAsync($"{path}/fields/nosuchfield", new FieldEdit("x"), ct);
        using var kept = await client.PostAsync($"{path}/fields/title/keep", null, ct);
        using var favorite = await client.PutAsJsonAsync($"{path}/favorite", new SetFavorite(true), ct);
        using var noted = await client.PutAsJsonAsync($"{path}/notes", new FieldEdit("Bring the tide tables."), ct);
        var notes = (await noted.Content.ReadFromJsonAsync<BookNotes>(ct))!;
        using var parts = await client.PostAsJsonAsync($"{path}/parts", new PartEdit("Chapter", "1", ""), ct);
        using var held = await client.PostAsync("/api/library/held/nothing", null, ct);
        using var corrected = await client.PostAsJsonAsync($"{path}/correction", new CorrectionEnd(true), ct);
        var after = await client.GetFromJsonAsync<BookDetails>(path, ct);
        var other = Client(await AddUserAsync(admin: false));
        other.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using var stranger = await other.PostAsJsonAsync($"{path}/fields/title", new FieldEdit("Mine"), ct);

        Assert.Null(edit.Problem);
        Assert.Equal("Harbor of Lost Ships", edit.Details!.Card.Title);
        Assert.Equal(HttpStatusCode.NotFound, unknownField.StatusCode);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, favorite.StatusCode);
        Assert.Equal(("Bring the tide tables.", "Notes (1)"), (notes.Text, notes.TabLabel));
        // Only a PDF's pages can be an adventure of their own.
        Assert.Equal(HttpStatusCode.NotFound, parts.StatusCode);
        Assert.Equal("That can't be done any more.", (await held.Content.ReadFromJsonAsync<ActionResult>(ct))!.Message);
        Assert.Equal(HttpStatusCode.NoContent, corrected.StatusCode);
        Assert.True(after!.Card.Favorite);
        Assert.Equal("Notes (1)", after.NotesLabel);
        Assert.Contains(after.Fields, f => f.Key == "title" && !f.Suggested && f.CanReset);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    [Fact]
    public async Task Forget_its_text_takes_what_reading_made_and_read_it_again_brings_it_back()
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
        var path = $"/api/library/books/{book.EntryId}";
        Assert.True(book.CanForgetText);
        Assert.False(book.CanReadAgain);

        using var forgot = await client.PostAsync($"{path}/forget-text", null, ct);
        var forgotten = await client.GetFromJsonAsync<BookDetails>(path, ct);
        using var again = await client.PostAsync($"{path}/read-again", null, ct);
        var back = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.SingleOrDefault(b => b.Cover is not null), ct);

        Assert.Equal("Its text is forgotten. Read it again brings it back.", (await forgot.Content.ReadFromJsonAsync<ActionResult>(ct))!.Message);
        Assert.Null(forgotten!.Card.Cover);
        Assert.True(forgotten.Card.CanReadAgain);
        Assert.False(forgotten.Card.CanForgetText);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(back.CanForgetText);
    }

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
    public async Task Removing_the_only_library_folder_and_its_storage_leaves_nothing_in_needs_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/Maps/IMG_0042.png", Png());
        _graph.Add("RPG/Maps/IMG_0043.png", Png(SkiaSharp.SKColors.Crimson));
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        var books = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 2 } two ? two : null, ct);
        await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
        {
            var library = lease.Library;
            var metadata = library.Get<MetadataStore>();
            var (heist, _) = await library.Get<VocabularyStore>().ProposeTermAsync("type", "Heist Kit", ct);
            foreach (var book in books)
            {
                var entry = new EntryId(book.EntryId);
                await metadata.ReplaceHintsAsync(entry, null, [new(MetadataFields.System, "dnd", AssertionOrigin.Embedded)], ct);
                await metadata.AddSuggestionsAsync(entry, null,
                [
                    new(MetadataFields.System, "pathfinder", AssertionOrigin.Ai, "for Pathfinder"),
                    new(MetadataFields.Types, heist.Key, AssertionOrigin.Ai, "a heist kit"),
                ], ct);
            }
            await library.Get<MetadataProjector>().ProjectAllAsync(ct);
        }
        var before = (await client.GetFromJsonAsync<LibraryStatus>("/api/library/status", ct))!;
        var folder = Assert.Single((await client.GetFromJsonAsync<List<LibraryFolder>>("/api/library/folders", ct))!);

        using (await client.DeleteAsync($"/api/library/folders/{folder.Id}", ct)) { }
        using (await client.DeleteAsync($"/api/storage/{drive.Id}", ct)) { }

        var status = (await client.GetFromJsonAsync<LibraryStatus>("/api/library/status", ct))!;
        var list = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        var files = (await client.GetFromJsonAsync<ReviewFiles>("/api/review/files", ct))!;
        var left = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!;
        Assert.Empty(left.Books);
        Assert.True(before.Review > 0);
        Assert.Equal((0, 0, 0), (status.Suggestions, status.Attention, list.Count));
        Assert.Empty(list.Terms);
        Assert.Empty(files.Cards);
    }

    [Fact]
    public async Task Needs_review_lists_and_decides_field_and_term_cards_with_undo_as_the_windows_app_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/Maps/IMG_0042.png", Png());
        _graph.Add("RPG/Maps/IMG_0043.png", Png(SkiaSharp.SKColors.Crimson));
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        var books = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 2 } two ? two : null, ct);

        // Two sources that disagree about the system on both books, and a type the vocabulary doesn't know yet.
        await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
        {
            var library = lease.Library;
            var metadata = library.Get<MetadataStore>();
            var (heist, _) = await library.Get<VocabularyStore>().ProposeTermAsync("type", "Heist Kit", ct);
            foreach (var book in books)
            {
                var entry = new EntryId(book.EntryId);
                await metadata.ReplaceHintsAsync(entry, null, [new(MetadataFields.System, "dnd", AssertionOrigin.Embedded)], ct);
                await metadata.AddSuggestionsAsync(entry, null,
                [
                    new(MetadataFields.System, "pathfinder", AssertionOrigin.Ai, "for Pathfinder"),
                    new(MetadataFields.Types, heist.Key, AssertionOrigin.Ai, "a heist kit"),
                ], ct);
            }
            await library.Get<MetadataProjector>().ProjectAllAsync(ct);
        }

        var list = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        var status = (await client.GetFromJsonAsync<LibraryStatus>("/api/library/status", ct))!;
        var first = list.Fields.First(f => f.FieldKey == "system");
        var title = list.Fields.First(f => f.FieldKey == "title");
        var term = Assert.Single(list.Terms);

        // A card whose suggestion changed since the page showed it is refused rather than decided.
        using var stale = await client.PostAsJsonAsync("/api/review/fields/accept", new ReviewFieldDecision(first.Id, "system|Conflict|pathfinder"), ct);
        var accepted = await PostAsync<ActionResult>("/api/review/fields/accept", new ReviewFieldDecision(first.Id, first.GroupKey));
        var afterAccept = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        await PostAsync<ActionResult>($"/api/library/held/{accepted.Undo}", null);
        var all = await PostAsync<ReviewAcceptedAll>("/api/review/fields/accept-all", new ReviewAcceptAll(first.Id, first.GroupKey, [first.Id]));
        var afterAll = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        await PostAsync<ActionResult>($"/api/library/held/{all.Undo}", null);
        var rejected = await PostAsync<ActionResult>("/api/review/fields/reject", new ReviewFieldDecision(first.Id, first.GroupKey));
        var edited = await PostAsync<ActionResult>("/api/review/fields/edit", new ReviewFieldEdit(title.Id, title.GroupKey, "Harbor  map"));
        var filed = await PostAsync<ActionResult>($"/api/review/terms/{term.Id}/map", new ReviewTermMap("map-pack"));
        var afterDecisions = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        await PostAsync<ActionResult>($"/api/library/held/{filed.Undo}", null);
        var added = await PostAsync<ActionResult>($"/api/review/terms/{term.Id}/add", null);
        var files = (await client.GetFromJsonAsync<ReviewFiles>("/api/review/files", ct))!;

        Assert.Equal(["terms", "field:system", "field:title"], list.Show.Select(s => s.Key));
        Assert.Equal((5, 5), (list.Count, status.Suggestions));
        Assert.Equal(("Sources disagree.", "Pathfinder", "Dungeons & Dragons", "Accept all 2"),
            (first.Reason, first.CurrentText, first.SuggestedText, first.AcceptAllLabel));
        Assert.Equal("From the PDF's own information.", first.Evidence);
        Assert.Contains("Pathfinder", list.Choices["system"]);
        Assert.False(list.Choices.ContainsKey("title"));
        Assert.Equal(("Type a title", "Keep as is", false), (title.EditLabel, title.RejectLabel, title.CanAccept));
        Assert.Equal(("Heist Kit", "New type", "Suggested for 2 books.", "“a heist kit”"), (term.Label, term.Chip, term.Detail, term.Example));
        Assert.Contains(term.Targets, t => t.Key == "map-pack");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Game system is now Dungeons & Dragons.", accepted.Message);
        Assert.DoesNotContain(afterAccept.Fields, f => f.Id == first.Id);
        Assert.Equal(("Accepted 2 suggestions.", 2), (all.Message, all.Count));
        Assert.Equal([first.Id], all.Cards.Select(c => c.Id));
        Assert.NotNull(all.Cards[0].Undo);
        Assert.DoesNotContain(afterAll.Fields, f => f.FieldKey == "system");
        Assert.Equal("Kept Pathfinder.", rejected.Message);
        Assert.Equal("Title is now Harbor map.", edited.Message);
        Assert.Equal("Filed Heist Kit under Map pack.", filed.Message);
        Assert.Empty(afterDecisions.Terms);
        Assert.Equal([list.Fields.First(f => f.FieldKey == "system" && f.Id != first.Id).Id, list.Fields.Last(f => f.FieldKey == "title").Id],
            afterDecisions.Fields.Select(f => f.Id));
        Assert.Equal("Added Heist Kit as a new type.", added.Message);
        Assert.Equal((false, 0), (files.CanRetryAll, files.Cards.Count));

        async Task<T> PostAsync<T>(string url, object? body)
        {
            using var response = body is null ? await client.PostAsync(url, null, ct) : await client.PostAsJsonAsync(url, body, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>(ct))!;
        }
    }

    [Fact]
    public async Task Needs_review_answers_the_cards_that_ask_one_question_with_undo_as_the_windows_app_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/Maps/IMG_0042.png", Png());
        _graph.Add("RPG/Maps/IMG_0043.png", Png(SkiaSharp.SKColors.Crimson));
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        var books = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 2 } two ? two : null, ct);
        var book = books.OrderBy(b => b.Title, StringComparer.Ordinal).First();

        // Cataloged wrong… left without a change, and the same system set on the books of one folder.
        await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
        {
            var library = lease.Library;
            await library.Get<CorrectionService>().LeaveUnfinishedAsync(new EntryId(book.EntryId), ct);
            var folder = (await library.Get<LibraryStore>().GetEntryFoldersAsync([new EntryId(book.EntryId)], ct))[0].Folder;
            await library.Get<CorrectionStore>().ProposeAsync(folder, "system", "pathfinder", 3, ct);
        }

        var list = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        var correction = list.Offers.Single(o => o.Kind == "corrections");
        var rule = list.Offers.Single(o => o.Kind == "rules");
        using var stale = await client.PostAsJsonAsync("/api/review/offers/answer", new ReviewOfferAnswer(rule.Id, "0000", "use"), ct);
        var fine = await PostAsync<ActionResult>("/api/review/offers/answer", new ReviewOfferAnswer(correction.Id, correction.Stamp, "fine"));
        var afterFine = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        await PostAsync<ActionResult>($"/api/library/held/{fine.Undo}", null);
        var afterUndo = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        var used = await PostAsync<ActionResult>("/api/review/offers/answer", new ReviewOfferAnswer(rule.Id, rule.Stamp, "use"));
        var status = (await client.GetFromJsonAsync<LibraryStatus>("/api/library/status", ct))!;

        Assert.Equal(["corrections", "rules"], list.Show.Select(s => s.Key).Where(k => !k.StartsWith("field:", StringComparison.Ordinal)));
        Assert.Equal((book.Title, "Cataloged wrong", "You said this book was cataloged wrong.", book.EntryId),
            (correction.Title, correction.Chip, correction.Heading, correction.Fix));
        Assert.Equal(["Fix it", "It's fine now"], correction.Choices.Select(c => c.Label));
        Assert.Equal(book.EntryId, correction.Book?.EntryId);
        Assert.Equal(("Folder rule?", "no"), (rule.Chip, rule.Dismiss));
        Assert.Equal("You set game system Pathfinder on 3 books in this folder. Suggest it for every file in it?", rule.Heading);
        Assert.Equal("OneDrive · Dana › RPG › Maps", rule.Path);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal($"Left {book.Title} as it is.", fine.Message);
        Assert.DoesNotContain(afterFine.Offers, o => o.Kind == "corrections");
        Assert.Contains(afterUndo.Offers, o => o.Id == correction.Id);
        Assert.Equal("Files in “Maps” now get game system Pathfinder as a suggestion.", used.Message);
        Assert.Equal(list.Count - 1, status.Suggestions);

        async Task<T> PostAsync<T>(string url, object? body)
        {
            using var response = body is null ? await client.PostAsync(url, null, ct) : await client.PostAsJsonAsync(url, body, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>(ct))!;
        }
    }

    [Fact]
    public async Task Needs_review_answers_a_file_that_looks_like_it_holds_adventures_as_the_windows_app_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/Maps/IMG_0042.png", Png());
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        var book = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is [var one] ? one : null, ct);

        await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
        {
            var library = lease.Library;
            var source = (await library.Get<LibraryStore>().GetSourceAsync(book.DocumentId, ct))!;
            await library.Get<PartProposalStore>().SaveAsync(source.ContentHash, PartProposalSource.Rules,
                new Compilation(new CollectionEvidence(0, "three short adventures"), [new ProposedPart("The Crypt", new PartPages(0, 0), "1-3")]), ct);
        }

        var list = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        var card = list.Offers.Single(o => o.Kind == "parts");
        using var unreadable = await client.PostAsJsonAsync("/api/review/offers/parts/accept",
            new ReviewPartsAccept(card.Id, card.Stamp, [new ReviewPartRow(0, "The Crypt", "nine", "")]), ct);
        using var notSplit = await client.PostAsJsonAsync("/api/review/offers/answer", new ReviewOfferAnswer(card.Id, card.Stamp, "notsplit"), ct);
        var kept = (await notSplit.Content.ReadFromJsonAsync<ActionResult>(ct))!;
        var afterNotSplit = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
        using (await client.PostAsync($"/api/library/held/{kept.Undo}", null, ct)) { }
        var afterUndo = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;

        Assert.Equal(("Adventures?", "This file looks like it holds 1 adventure.", "notsplit"), (card.Chip, card.Heading, card.Dismiss));
        Assert.StartsWith("The book says “three short adventures” (p. 1).", card.Detail);
        var part = Assert.Single(card.Proposed!);
        Assert.Equal(("The Crypt", "1", "", "p. 1", "Levels 1–3"), (part.Title, part.From, part.To, part.PagesText, part.Levels));
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
        Assert.Contains("The pages of The Crypt don't read as pages of this file.", await unreadable.Content.ReadAsStringAsync(ct));
        Assert.Equal($"Kept {book.Title} as one book.", kept.Message);
        Assert.DoesNotContain(afterNotSplit.Offers, o => o.Kind == "parts");
        Assert.Contains(afterUndo.Offers, o => o.Id == card.Id);
    }

    [Fact]
    public async Task A_cover_is_served_by_its_name_and_nothing_else_is()
    {
        var user = await AddUserAsync(admin: false);
        await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, TestContext.Current.CancellationToken))
            lease.Library.Get<CoverCache>().Write("abc123", [0xFF, 0xD8, 0xFF, 0xD9]);
        var client = Client(user);

        using var cover = await client.GetAsync("/api/library/covers/abc123.jpg", TestContext.Current.CancellationToken);
        using var missing = await client.GetAsync("/api/library/covers/def456.jpg", TestContext.Current.CancellationToken);
        using var outside = await client.GetAsync("/api/library/covers/..%2Fcatalog.db", TestContext.Current.CancellationToken);
        using var stranger = await Client().GetAsync("/api/library/covers/abc123.jpg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/jpeg", cover.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, (await cover.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
    }

    [Fact]
    public async Task A_change_without_the_client_header_is_refused_as_cross_site()
    {
        var admin = await AddUserAsync(admin: true);
        var client = Client(admin);

        using var bare = await client.PostAsJsonAsync("/api/invites", new CreateInvite("x"), TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using var withHeader = await client.PostAsJsonAsync("/api/invites", new CreateInvite("x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_may_invite()
    {
        var user = await AddUserAsync(admin: false);
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        using var response = await client.PostAsJsonAsync("/api/invites", new CreateInvite("x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_invitation_link_points_at_the_join_page()
    {
        var admin = await AddUserAsync(admin: true);
        var client = Client(admin);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        using var response = await client.PostAsJsonAsync("/api/invites", new CreateInvite("for a friend"), TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<CreatedInvite>(TestContext.Current.CancellationToken);

        Assert.Contains("/join?invite=", created!.Link, StringComparison.Ordinal);
        var listed = await client.GetFromJsonAsync<List<InviteInfo>>("/api/invites", TestContext.Current.CancellationToken);
        Assert.Contains(listed!, i => i.Note == "for a friend");
    }

    [Fact]
    public async Task An_unknown_API_path_is_404_not_the_web_page()
    {
        var user = await AddUserAsync(admin: false);

        using var response = await Client(user).GetAsync("/api/nothing-here", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_folder_picked_from_a_connected_drive_joins_the_library_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/Adventures/one.pdf", [1, 2, 3]);
        _graph.Add("RPG/Maps/map.png", [4, 5]);
        _graph.Add("RPG/notes.txt", [6]);
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        var storage = await client.GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct);
        var folders = await client.GetFromJsonAsync<StorageFolderList>($"/api/storage/{drive.Id}/folders?path=RPG", ct);
        using var added = await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG/Adventures"), ct);
        using var again = await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG/Adventures"), ct);
        var after = await client.GetFromJsonAsync<StorageFolderList>($"/api/storage/{drive.Id}/folders?path=RPG", ct);

        Assert.Equal("OneDrive · Dana", Assert.Single(storage!).Name);
        Assert.Equal(["Adventures", "Maps"], folders!.Folders.Select(f => f.Name));
        Assert.Equal(1, folders.Files);
        var library = Assert.Single((await added.Content.ReadFromJsonAsync<List<LibraryFolder>>(ct))!);
        Assert.Equal(("Adventures", "OneDrive · Dana › RPG › Adventures"), (library.Name, library.Location));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.True(after!.Folders.Single(f => f.Name == "Adventures").InLibrary);
    }

    [Fact]
    public async Task Disconnecting_a_drive_forgets_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        using var removed = await client.DeleteAsync($"/api/storage/{drive.Id}", ct);
        using var stranger = await Client(await AddUserAsync(admin: false)).GetAsync($"/api/storage/{drive.Id}/folders", ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct))!);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    [Fact]
    public async Task Graphs_validation_request_is_echoed_and_a_notification_with_no_match_is_accepted_and_ignored()
    {
        var ct = TestContext.Current.CancellationToken;

        using var validation = await Client().PostAsync("/storage/notifications/onedrive?validationToken=abc%20123", null, ct);
        using var unknown = await Client().PostAsync("/storage/notifications/onedrive",
            new StringContent("""{"value":[{"clientState":"nobody.secret","subscriptionId":"x"}]}""", System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal("abc 123", await validation.Content.ReadAsStringAsync(ct));
        Assert.Equal("text/plain", validation.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
    }

    [Fact]
    public async Task With_no_providers_configured_sign_in_lists_none()
    {
        var providers = await Client().GetFromJsonAsync<List<Bibliotaph.Api.Contracts.SignInProvider>>("/auth/providers", TestContext.Current.CancellationToken);

        Assert.Empty(providers!);
    }

    [Fact]
    public async Task With_Auth0_set_up_sign_in_is_one_button_and_signing_out_ends_its_session_too()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = _factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("Auth:auth0:Domain", "tenant.example.auth0.com");
            host.UseSetting("Auth:auth0:ClientId", "abc");
            host.UseSetting("Auth:auth0:ClientSecret", "secret");
            host.UseSetting("Server:PublicOrigin", "https://bibliotaph.online");
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var providers = await client.GetFromJsonAsync<List<Bibliotaph.Api.Contracts.SignInProvider>>("/auth/providers", ct);
        using var direct = await client.GetAsync("/auth/signin/google", ct);
        using var unguarded = await client.PostAsync("/auth/signout", null, ct);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using var signOut = await client.PostAsync("/auth/signout", null, ct);
        var signedOut = await signOut.Content.ReadFromJsonAsync<SignedOut>(ct);

        Assert.Equal([new Bibliotaph.Api.Contracts.SignInProvider("auth0", "Continue")], providers);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unguarded.StatusCode);
        Assert.Equal("https://tenant.example.auth0.com/v2/logout?client_id=abc&returnTo=https%3A%2F%2Fbibliotaph.online%2F", signedOut!.Next);
    }

    [Fact]
    public async Task Storage_is_offered_once_its_app_is_set_up_and_connecting_asks_for_that_providers_own_access()
    {
        var ct = TestContext.Current.CancellationToken;
        var unconfigured = await Client(await AddUserAsync(admin: false)).GetFromJsonAsync<List<StorageProviderInfo>>("/api/storage/providers", ct);
        using var factory = _factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("Storage:OneDrive:ClientId", "client");
            host.UseSetting("Storage:OneDrive:ClientSecret", "secret");
        });
        var user = await AddUserAsync(admin: false);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestSession.Header, $"{user.Id};{user.Name};{user.IsAdmin}");

        var providers = await client.GetFromJsonAsync<List<StorageProviderInfo>>("/api/storage/providers", ct);
        using var connect = await client.GetAsync("/storage/connect/onedrive?returnUrl=%2Fsettings", ct);
        using var unknown = await client.GetAsync("/storage/connect/elsewhere", ct);

        // WebDAV needs no app of its own, so it's always offered.
        var webDav = new StorageProviderInfo("webdav", "WebDAV", "your server", SignsInWithPassword: true);
        Assert.Equal([webDav], unconfigured);
        Assert.Equal([new StorageProviderInfo("onedrive", "OneDrive", "Microsoft"), webDav], providers);
        Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);
        var authorize = connect.Headers.Location!.ToString();
        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?", authorize, StringComparison.Ordinal);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(authorize).Query);
        Assert.Equal("offline_access Files.Read", query["scope"].ToString());
        Assert.EndsWith("/storage/callback/onedrive", query["redirect_uri"].ToString(), StringComparison.Ordinal);
        Assert.Equal("/settings?problem=storage", unknown.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_webdav_folder_connects_with_its_password_and_says_why_when_it_cant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client(await AddUserAsync(admin: false));
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        using var refused = await client.PostAsJsonAsync("/api/storage/webdav", new WebDavConnect(FakeWebDav.Root, FakeWebDav.User, "wrong"), ct);
        using var connected = await client.PostAsJsonAsync("/api/storage/webdav", new WebDavConnect(FakeWebDav.Root, FakeWebDav.User, FakeWebDav.Password), ct);
        var account = await connected.Content.ReadFromJsonAsync<StorageAccount>(ct);
        var listed = await client.GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("didn't accept", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        Assert.Equal("WebDAV · dana on dav.example.com", account!.Name);
        Assert.Equal(["read your files with the password you gave", "keep access while you're signed out"], account.Access);
        Assert.Null(account.Scopes);
        Assert.Equal(account.Id, Assert.Single(listed!).Id);
        Assert.DoesNotContain(FakeWebDav.Password, await connected.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Needs_review_takes_every_suggestion_once_settings_says_so_and_only_the_unsure_ones_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client(await AddUserAsync(admin: false));
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        var before = await client.GetFromJsonAsync<ReviewMode>("/api/review/mode", ct);
        using var set = await client.PutAsJsonAsync("/api/review/mode", new ReviewMode(true), ct);
        var after = await client.GetFromJsonAsync<ReviewMode>("/api/review/mode", ct);

        Assert.False(before!.All);
        Assert.True((await set.Content.ReadFromJsonAsync<ReviewMode>(ct))!.All);
        Assert.True(after!.All);
    }

    [Fact]
    public async Task A_backup_and_both_exports_download_named_for_the_users_day_without_the_servers_sealed_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
            await lease.Library.Get<SettingsStore>().SetAsync(SettingKeys.ServerAiKeys, "{\"https://api.example.com/v1\":\"CfDJ8-sealed-key\"}", ct);
        var client = Client(user);

        using var backup = await client.GetAsync("/api/library/backup?today=2026-10-26", ct);
        using var csv = await client.GetAsync("/api/library/export/csv?today=2026-10-26", ct);
        using var json = await client.GetAsync("/api/library/export/json", ct);

        Assert.Equal("Bibliotaph backup 2026-10-26.zip", backup.Content.Headers.ContentDisposition!.FileNameStar);
        using var folder = new TempFolder();
        await using (var zipped = await backup.Content.ReadAsStreamAsync(ct))
            System.IO.Compression.ZipFile.ExtractToDirectory(zipped, folder.Path);
        Assert.True(File.Exists(Path.Combine(folder.Path, "manifest.json")));
        await using (var catalog = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder.Path, "catalog.db")};Pooling=False"))
        {
            await catalog.OpenAsync(ct);
            await using var count = catalog.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM setting WHERE key = 'server.ai-keys'";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(ct))!);
        }
        Assert.Equal("Bibliotaph library 2026-10-26.csv", csv.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.StartsWith("Title,", (await csv.Content.ReadAsStringAsync(ct)).TrimStart('\uFEFF'), StringComparison.Ordinal);
        var exported = await json.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"format\": \"bibliotaph-catalog\"", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("CfDJ8-sealed-key", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_library_chooses_the_start_page_and_switches_a_folder_name_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/Adventures/IMG_0042.png", Png());
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 1 } one ? one : null, ct);

        var before = (await client.GetFromJsonAsync<LibraryPreferences>("/api/settings/library", ct))!;
        using (await client.PutAsJsonAsync("/api/settings/start", new StartPageChoice(true), ct)) { }
        var label = before.FolderNames.Single(l => l.Folder == "Adventures");
        using var switched = await client.PutAsJsonAsync("/api/settings/folder-names", new FolderNameSwitch(label.Folder, label.Vocabulary, label.Term, false), ct);
        var after = (await switched.Content.ReadFromJsonAsync<LibraryPreferences>(ct))!;

        Assert.False(before.StartOnLibrary);
        Assert.Equal(("Type: Adventure", "1 document under a folder with this name", true), (label.Meaning, label.Detail, label.Enabled));
        Assert.True((await client.GetFromJsonAsync<StartPageChoice>("/api/settings/start", ct))!.Library);
        Assert.True(after.StartOnLibrary);
        Assert.False(after.FolderNames.Single(l => l.Folder == "Adventures").Enabled);
        Assert.Empty(after.FolderRules);
    }

    [Fact]
    public async Task Settings_vocabulary_adds_renames_and_names_a_term_and_keeps_the_users_own_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client(await AddUserAsync(admin: false));
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

        using var added = await client.PostAsJsonAsync("/api/vocabulary/type", new NewTerm("Hexcrawl"), ct);
        var termId = (await added.Content.ReadFromJsonAsync<VocabularyChange>(ct))!.TermId!.Value;
        using (await client.PutAsJsonAsync($"/api/vocabulary/terms/{termId}", new TermRename("Hex crawl", "Hex"), ct)) { }
        using var named = await client.PostAsJsonAsync($"/api/vocabulary/terms/{termId}/aliases", new NewAlias("pointcrawl"), ct);
        var term = (await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/type", ct))!.Single(t => t.Id == termId);
        using var unknown = await client.GetAsync("/api/vocabulary/nonsense", ct);

        Assert.Null((await named.Content.ReadFromJsonAsync<VocabularyChange>(ct))!.Problem);
        Assert.Equal(("Hex crawl", "Hex", true), (term.Label, term.ShortLabel, term.Yours));
        var alias = Assert.Single(term.Aliases, a => a.Text == "pointcrawl");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (await client.DeleteAsync($"/api/vocabulary/aliases/{alias.Id}", ct)) { }
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/type", ct))!.Single(t => t.Id == termId).Aliases,
            a => a.Text == "pointcrawl");

        using var saved = await client.PostAsJsonAsync("/api/tags", new UserTagText("Undead", "zombie, lich"), ct);
        var tag = (await saved.Content.ReadFromJsonAsync<UserTagChange>(ct))!.Tag!;
        using var refused = await client.PostAsJsonAsync("/api/tags", new UserTagText("Ghosts", " "), ct);
        using (await client.PutAsJsonAsync($"/api/tags/{tag.Id}", new UserTagText("The undead", "zombie, lich, ghoul"), ct)) { }
        var listed = (await client.GetFromJsonAsync<List<UserTag>>("/api/tags", ct))!.Single();
        using var deleted = await client.DeleteAsync($"/api/tags/{tag.Id}", ct);
        using var again = await client.DeleteAsync($"/api/tags/{tag.Id}", ct);

        Assert.Equal(["zombie", "lich"], tag.Words);
        Assert.Equal(0, tag.Books);
        Assert.NotNull((await refused.Content.ReadFromJsonAsync<UserTagChange>(ct))!.Problem);
        Assert.Equal(("The undead", 3), (listed.Name, listed.Words.Count));
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (deleted.StatusCode, again.StatusCode));
    }

    [Fact]
    public async Task Settings_processing_pauses_a_lane_and_keeps_it_paused_when_the_library_opens_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        var libraries = _factory.Services.GetRequiredService<UserLibraries>();

        var before = (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))!;
        using var paused = await client.PutAsJsonAsync("/api/processing/paused", new ProcessingPauses(false, false, true), ct);
        var after = (await paused.Content.ReadFromJsonAsync<ProcessingState>(ct))!;
        await libraries.CloseIdleAsync(TimeSpan.Zero);
        var reopened = (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))!;

        Assert.Equal(("Nothing to process", false, new ProcessingPauses(false, false, false)), (before.Summary, before.HasFolders, before.Paused));
        Assert.Equal(new ProcessingPauses(false, false, true), after.Paused);
        Assert.Equal(new ProcessingPauses(false, false, true), reopened.Paused);
        Assert.Empty(reopened.Now);
    }

    [Fact]
    public async Task Settings_lists_what_each_connection_was_granted_in_plain_words()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        await connections.SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId, "Dropbox · Dana", "personal", "refresh",
            "account_info.read files.metadata.read files.content.read", ct);
        await connections.SaveAsync(user.Id, OneDriveProvider.Key, "drive", "OneDrive · Dana", "personal", "refresh",
            "Files.Read openid profile email offline_access Sites.Read.All", ct);

        var accounts = (await Client(user).GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct))!.ToDictionary(a => a.Provider);

        var dropbox = accounts[DropboxProvider.Key];
        Assert.Equal(["see your name", "see your file and folder names", "read your files", "keep access while you're signed out"], dropbox.Access!);
        Assert.Equal("account_info.read files.metadata.read files.content.read", dropbox.Scopes);
        // Anything granted that Bibliotaph has no words for is still listed, by its own name.
        Assert.Equal(["read your files", "keep access while you're signed out", "Sites.Read.All"], accounts[OneDriveProvider.Key].Access!);
    }

    [Fact]
    public async Task A_book_on_dropbox_is_read_through_the_server_by_a_link_that_says_whose_it_is()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId, "Dropbox · Dana", "personal", "refresh", null, ct);
        _dropbox.Add("RPG/one.pdf", System.Text.Encoding.ASCII.GetBytes("0123456789"));
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        var documentId = await FirstDocumentAsync(user.Id, ct);

        var link = (await client.GetFromJsonAsync<ReaderLink>($"/api/library/documents/{documentId}/link", ct))!;
        var anonymous = Client();
        using var ranged = new HttpRequestMessage(HttpMethod.Get, link.Url);
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var read = await anonymous.SendAsync(ranged, ct);
        using var whole = await anonymous.GetAsync(link.Url, ct);
        using var forged = await anonymous.GetAsync("/storage/read/not-a-link", ct);

        Assert.StartsWith("/storage/read/", link.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("RPG", link.Url, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.PartialContent, read.StatusCode);
        Assert.Equal("2345", await read.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, whole.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, forged.StatusCode);
    }

    [Fact]
    public async Task Dropboxs_webhook_is_answered_and_a_signed_notice_finds_whats_new()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = _factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("Storage:Dropbox:ClientId", "dropbox-client");
            host.UseSetting("Storage:Dropbox:ClientSecret", "dropbox-secret");
        });
        var accounts = factory.Services.GetRequiredService<AccountService>();
        var (code, _) = await accounts.CreateInviteAsync(null, null, admin: false);
        var user = (await accounts.SignInAsync(new ExternalIdentity("google", Guid.NewGuid().ToString(), "Dana", null), code, null, ct)).User!;
        var drive = await factory.Services.GetRequiredService<StorageConnections>()
            .SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId, "Dropbox · Dana", "personal", "refresh", null, ct);
        _dropbox.Add("RPG/one.pdf", [1, 2, 3]);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestSession.Header, $"{user.Id};{user.Name};{user.IsAdmin}");
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        var libraries = factory.Services.GetRequiredService<UserLibraries>();
        await WaitForFilesAsync(libraries, user.Id, 1, ct);
        _dropbox.Add("RPG/two.pdf", [4, 5, 6]);
        var body = System.Text.Encoding.UTF8.GetBytes($$$"""{"list_folder":{"accounts":["{{{FakeDropbox.AccountId}}}"]}}""");
        var signature = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData("dropbox-secret"u8, body));
        var webhook = factory.CreateClient();

        var challenge = await webhook.GetStringAsync("/storage/notifications/dropbox?challenge=echo-me", ct);
        using var unsigned = await webhook.PostAsync("/storage/notifications/dropbox", new ByteArrayContent(body), ct);
        using var signedNotice = new HttpRequestMessage(HttpMethod.Post, "/storage/notifications/dropbox") { Content = new ByteArrayContent(body) };
        signedNotice.Headers.Add("X-Dropbox-Signature", signature);
        using var signed = await webhook.SendAsync(signedNotice, ct);

        Assert.Equal("echo-me", challenge);
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        await WaitForFilesAsync(libraries, user.Id, 2, ct);
    }

    static async Task WaitForFilesAsync(UserLibraries libraries, string userId, int files, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            await using (var lease = await libraries.OpenAsync(userId, timeout.Token))
                if ((await lease.Library.Get<LibraryStore>().GetCountsAsync(timeout.Token)).Files >= files) return;
            await Task.Delay(50, timeout.Token);
        }
    }

    async Task<long> FirstDocumentAsync(string userId, CancellationToken ct)
    {
        var libraries = _factory.Services.GetRequiredService<UserLibraries>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            await using (var lease = await libraries.OpenAsync(userId, timeout.Token))
                if (await lease.Library.Get<LibraryStore>().GetRelativePathsAsync(timeout.Token) is [var first, ..])
                    return first.DocumentId;
            await Task.Delay(50, timeout.Token);
        }
    }

    [Fact]
    public async Task The_health_check_needs_no_session()
    {
        using var response = await Client().GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    static byte[] Png(SkiaSharp.SKColor? color = null)
    {
        using var bitmap = new SkiaSharp.SKBitmap(600, 400);
        bitmap.Erase(color ?? SkiaSharp.SKColors.SeaGreen);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Settings_ai_uses_a_model_service_on_the_internet_with_its_key_sealed_and_catalogs_books_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(_pdfs.KnownText, ct));
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        const string Service = "https://api.example.com/v1";

        var before = (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!;
        async Task<AiConnection> ConnectAsync(string endpoint, string? key)
        {
            using var response = await client.PostAsJsonAsync("/api/ai/connect", new AiConnect(endpoint, key), ct);
            return (await response.Content.ReadFromJsonAsync<AiConnection>(ct))!;
        }
        async Task<AiNote> SendAsync(HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            using var response = await client.SendAsync(request, ct);
            return (await response.Content.ReadFromJsonAsync<AiNote>(ct))!;
        }
        var local = await ConnectAsync("http://localhost:11434", null);
        var lan = await ConnectAsync("https://10.1.2.3/v1", null);
        var keyless = await ConnectAsync(Service, null);
        var found = await ConnectAsync("api.example.com/v1", FakeModelService.Key);
        var unticked = await SendAsync(HttpMethod.Put, "/api/ai", new AiSave(Service, "fake-mini", FakeModelService.Key, RemoteAllowed: false));
        var saved = await SendAsync(HttpMethod.Put, "/api/ai", new AiSave(Service, "fake-mini", FakeModelService.Key, RemoteAllowed: true));
        var page = (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!;
        var classified = await WaitAsync(async () => (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!.Progress is var p
            && p == "1 book classified with fake-mini. Nothing is waiting." ? p : null, ct);
        string? sealedKeys;
        await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
            sealedKeys = await lease.Library.Get<SettingsStore>().GetAsync("server.ai-keys", ct);
        using var tested = await client.PostAsJsonAsync("/api/ai/test", new AiTest(Service, "fake-mini", RemoteAllowed: true), ct);
        var report = (await tested.Content.ReadFromJsonAsync<AiTestReport>(ct))!;
        var off = await SendAsync(HttpMethod.Put, "/api/ai/enabled", new AiSwitch(false));
        using (await client.DeleteAsync($"/api/ai/key?endpoint={Uri.EscapeDataString(Service)}", ct)) { }
        var forgotten = (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!;

        Assert.Equal((false, null), (before.Enabled, before.Endpoint));
        Assert.Contains("https", local.Problem, StringComparison.Ordinal);
        Assert.Contains("private network", lan.Problem, StringComparison.Ordinal);
        Assert.Equal("The endpoint needs a key, or didn't accept the one saved.", keyless.Problem);
        Assert.Equal(("https://api.example.com", "Found an OpenAI-compatible server with 2 models."), (found.Endpoint, found.Text));
        Assert.Equal(["fake-mini", "fake-large"], found.Models);
        Assert.Equal("Tick “Send excerpts to this server” first.", unticked.Problem);
        Assert.Equal("Saved. Books are classified in the background, one at a time.", saved.Note);
        Assert.Equal((true, "https://api.example.com", "fake-mini", "abcd"), (page.Enabled, page.Endpoint, page.Model, page.KeyEnding));
        Assert.Equal("RPG", Assert.Single(page.Folders).Name);
        Assert.NotEmpty(classified);
        Assert.All(_models.Requests, r => Assert.Equal(("https://api.example.com", FakeModelService.Key), (r.Endpoint, r.Key)));
        Assert.DoesNotContain(FakeModelService.Key, sealedKeys, StringComparison.Ordinal);
        // The one book is too short to be a sample, so the test says so, as the Windows dialog does.
        Assert.Equal("There's no book with text to try yet. Add a folder, or wait for its books to be read.", report.Problem);
        Assert.Equal(AiText.SwitchedOff, off.Note);
        Assert.Null(forgotten.KeyEnding);
    }

    [Fact]
    public async Task Scanning_whole_books_waits_to_be_switched_on_then_shows_what_the_ai_found_inside_the_book()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/crown.pdf", await File.ReadAllBytesAsync(_pdfs.ChapterBook, ct));
        // The first chapter's marsh, quoted from its own page; nothing for the others.
        _models.Answer = request =>
        {
            if (request.SchemaName != ChapterPrompt.SchemaName) return "{}";
            var chapters = Regex.Matches(request.User, @"^Chapter (\d+): (.+) \(pages", RegexOptions.Multiline).Select(m =>
                m.Groups[2].Value.Contains("Drowned Road", StringComparison.Ordinal)
                    ? $$"""{"chapter": {{m.Groups[1].Value}}, "environment": [{"value": "Swamp", "page": 2, "quote": "The road sinks into the marsh"}]}"""
                    : $$"""{"chapter": {{m.Groups[1].Value}}}""");
            return $$"""{"chapters": [{{string.Join(",", chapters)}}]}""";
        };
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        const string Service = "https://api.example.com/v1";
        using (await client.PutAsJsonAsync("/api/ai", new AiSave(Service, "fake-mini", FakeModelService.Key, RemoteAllowed: true), ct)) { }
        await WaitAsync(async () => (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!.Progress is var p
            && p.StartsWith("1 book classified", StringComparison.Ordinal) ? p : null, ct);
        var book = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.Single();
        var path = $"/api/library/books/{book.EntryId}";
        var before = (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!;
        var unscanned = (await client.GetFromJsonAsync<BookDetails>(path, ct))!;
        var askedBefore = _models.Requests.Count(r => r.Request.SchemaName == ChapterPrompt.SchemaName);

        using (await client.PutAsJsonAsync("/api/ai/scan", new AiSwitch(true), ct)) { }
        var found = await WaitAsync(async () => (await client.GetFromJsonAsync<BookDetails>(path, ct))!.Chapters is { Count: > 0 } c
            && c.All(x => x.Note != "Not read yet.") ? c : null, ct);
        var scanned = (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!;
        using (await client.PutAsJsonAsync("/api/ai/scan", new AiSwitch(false), ct)) { }
        using var again = await client.PostAsync($"{path}/scan-again", null, ct);
        var rescan = (await again.Content.ReadFromJsonAsync<EditResult>(ct))!;

        // Off until switched on: every chapter is a request on the user's key.
        Assert.False(before.ScanWholeBooks);
        Assert.Empty(unscanned.Chapters);
        Assert.Equal(0, askedBefore);
        var road = Assert.Single(found, c => c.Title == "Chapter 1: The Drowned Road");
        Assert.Equal(["Environments: Swamp"], road.Lines);
        Assert.Null(road.Note);
        Assert.Contains(found, c => c.Note == "Nothing found.");
        Assert.True(scanned.ScanWholeBooks);
        Assert.Equal("1 whole book scanned.", scanned.ScanProgress);
        Assert.Equal(DetailsText.ScanAgain(false), rescan.Message);
        Assert.Empty(rescan.Details!.Chapters);
    }

    [Fact]
    public async Task Ask_this_book_finds_its_pages_streams_an_answer_that_cites_them_and_keeps_the_conversation()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/crown.pdf", await File.ReadAllBytesAsync(_pdfs.ChapterBook, ct));
        _models.Answer = request =>
        {
            if (request.System == AskPrompt.RewriteSystemMessage) return "Follow-up: what do the bog goblins eat?";
            if (request.System != AskPrompt.SystemMessage) return "{}";
            // Cites the page the goblins are on, by the marker it was sent with, quoting it: the last marker before the
            // words, which lines of any ending may come between.
            var at = request.User.IndexOf("bog goblins wait", request.User.IndexOf("[Page ", StringComparison.Ordinal), StringComparison.Ordinal);
            var page = Regex.Matches(request.User, @"\[Page ([^\]]+)\]").Last(m => m.Index < at).Groups[1].Value;
            var answer = $"They wait \"in the reeds\" by the drowned road [p. {page}].";
            request.Progress?.Report(new ModelProgress(ModelStep.Writing, answer[..12]));
            Thread.Sleep(400);
            return answer;
        };
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        // The stand-in makes no vectors, so pages are found by their words alone.
        using (await client.PutAsJsonAsync("/api/ai", new AiSave("https://api.example.com/v1", "fake-mini", FakeModelService.Key, RemoteAllowed: true,
            EmbeddingModel: "fake-embed"), ct)) { }
        var settings = await WaitAsync(async () => await client.GetFromJsonAsync<AiPage>("/api/ai", ct) is { } p
            && p.Progress.StartsWith("1 book classified", StringComparison.Ordinal) ? p : null, ct);
        var book = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.Single();
        var details = (await client.GetFromJsonAsync<BookDetails>($"/api/library/books/{book.EntryId}", ct))!;
        var path = $"/api/ask/{book.DocumentId}";
        async Task<List<AskUpdate>> AskAsync(string question)
        {
            using var response = await client.PostAsJsonAsync(path, new AskQuestion(question), ct);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
            return [.. (await response.Content.ReadAsStringAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<AskUpdate>(line, JsonSerializerOptions.Web)!)];
        }

        var opened = (await client.GetFromJsonAsync<AskBook>(path, ct))!;
        var first = await AskAsync("Where do the bog goblins wait?");
        var kept = (await client.GetFromJsonAsync<AskBook>(path, ct))!;
        var followUp = await AskAsync("And what do they eat?");
        using (await client.DeleteAsync(path, ct)) { }
        var cleared = (await client.GetFromJsonAsync<AskBook>(path, ct))!;
        using (await client.PutAsJsonAsync("/api/ai/enabled", new AiSwitch(false), ct)) { }
        var off = (await client.GetFromJsonAsync<AskBook>(path, ct))!;
        var offDetails = (await client.GetFromJsonAsync<BookDetails>($"/api/library/books/{book.EntryId}", ct))!;

        Assert.Equal("fake-embed", settings.EmbeddingModel);
        Assert.True(details.CanAsk);
        Assert.Equal((true, null), (opened.On, opened.Unavailable));
        Assert.Empty(opened.History);
        Assert.Contains(first, u => u.Step == "Looking through the book…");
        Assert.Contains(first, u => u.Step == "Writing…" && u.Answering);
        var passage = Assert.Single(first.SelectMany(u => u.Passages ?? []), p => p.Page == 1);
        Assert.StartsWith("p. 2", passage.Link, StringComparison.Ordinal);
        var last = first[^1];
        Assert.True(last.Done);
        var citation = Assert.Single(last.Answer!, p => p.Kind == AskPartKind.Citation);
        Assert.Equal((1, "[p. 2]"), (citation.Page, citation.Text));
        Assert.Contains(last.Answer!, p => p.Kind == AskPartKind.Quote);
        Assert.StartsWith("Read from page", last.PassagesHeading, StringComparison.Ordinal);
        Assert.Equal("Where do the bog goblins wait?", last.Turn!.Question);
        Assert.Equal("Where do the bog goblins wait?", Assert.Single(kept.History).Question);
        Assert.Contains(followUp, u => u.Step == "Working out the follow-up…");
        Assert.Contains(followUp, u => u.LookedFor == "Looked for: what do the bog goblins eat?");
        Assert.Empty(cleared.History);
        Assert.Equal((false, "Ask this book uses AI, which is off. Set it up in Settings > AI."), (off.On, off.Unavailable));
        Assert.False(offDetails.CanAsk);
    }

    [Fact]
    public async Task Ask_in_the_top_bar_reads_a_request_as_a_search_and_answers_a_question_from_the_books_the_library_shows()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
        _graph.Add("RPG/crown.pdf", await File.ReadAllBytesAsync(_pdfs.ChapterBook, ct));
        _models.Answer = request =>
        {
            if (request.SchemaJson == PlainSearchPrompt.SchemaJson)
                return """{"filters": [{"field": "players", "value": "4", "exclude": false}], "words": [{"text": "heist", "exclude": false}]}""";
            if (request.System != AskPrompt.LibrarySystemMessage) return "{}";
            // Cites the page the goblins are on, by the book and page it was sent with.
            var at = request.User.IndexOf("bog goblins wait", request.User.IndexOf("[Page ", StringComparison.Ordinal), StringComparison.Ordinal);
            var page = Regex.Matches(request.User, @"\[Page ([^\]]+)\]").Last(m => m.Index < at).Groups[1].Value;
            return $"They wait in the reeds by the drowned road [p. {page}].";
        };
        var client = Client(user);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        var before = (await client.GetFromJsonAsync<AskStatus>("/api/ask", ct))!;
        using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
        using (await client.PutAsJsonAsync("/api/ai", new AiSave("https://api.example.com/v1", "fake-mini", FakeModelService.Key, RemoteAllowed: true,
            EmbeddingModel: "fake-embed"), ct)) { }
        var settings = await WaitAsync(async () => await client.GetFromJsonAsync<AiPage>("/api/ai", ct) is { } p
            && p.Progress.StartsWith("1 book classified", StringComparison.Ordinal) ? p : null, ct);
        var book = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.Single();
        async Task<List<AskUpdate>> AskAsync(string question, LibraryQuery scope)
        {
            using var response = await client.PostAsJsonAsync("/api/ask/library", new AskLibraryQuestion(question, scope), ct);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
            return [.. (await response.Content.ReadAsStringAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<AskUpdate>(line, JsonSerializerOptions.Web)!)];
        }

        var ready = (await client.GetFromJsonAsync<AskStatus>("/api/ask", ct))!;
        using var read = await client.PostAsJsonAsync("/api/ask/search", new AskQuestion("a heist for four players"), ct);
        var reading = (await read.Content.ReadFromJsonAsync<AskReading>(ct))!;
        var answered = await AskAsync("Where do the bog goblins wait?", new LibraryQuery());
        var elsewhere = await AskAsync("Where do the bog goblins wait?", new LibraryQuery { Type = "Map" });
        using (await client.PutAsJsonAsync("/api/ai/library", new AiSwitch(true), ct)) { }
        var reads = (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!;

        Assert.False(before.Ready);
        Assert.True(ready.Ready);
        Assert.Equal([new AskChip("players", "4", false), new AskChip(null, "heist", false)], reading.Parts);
        Assert.Null(reading.Problem);
        Assert.Contains(answered, u => u.Step == "Looking through your books…");
        var passage = Assert.Single(answered.SelectMany(u => u.Passages ?? []), p => p.Page == 1);
        Assert.Equal((book.DocumentId, book.EntryId), (passage.DocumentId, passage.EntryId));
        Assert.Contains(" · p. 2", passage.Link, StringComparison.Ordinal);
        var last = answered[^1];
        Assert.True(last.Done);
        var citation = Assert.Single(last.Answer!, p => p.Kind == AskPartKind.Citation);
        Assert.Equal((1, book.DocumentId, book.EntryId), (citation.Page, citation.DocumentId, citation.EntryId));
        Assert.EndsWith(", p. 2]", citation.Text, StringComparison.Ordinal);
        Assert.EndsWith(" in 1 book", last.PassagesHeading, StringComparison.Ordinal);
        Assert.StartsWith("Searched ", answered.Select(u => u.Coverage).OfType<string>().Single(), StringComparison.Ordinal);
        // A Library filtered to maps has no book to ask.
        Assert.Equal("None of these books has pages with text yet.", elsewhere.Select(u => u.Coverage).OfType<string>().Single());
        Assert.Empty(elsewhere.SelectMany(u => u.Passages ?? []));
        // Reading the library for meaning waits to be switched on: each batch of pages is a request on the user's key.
        Assert.False(settings.ReadLibrary);
        Assert.True(reads.ReadLibrary);
    }

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
            new AppPaths(Path.Combine(_folder.Path, "pc")), machineName: "DESKTOP-1", channels: [pcWeb]);

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

    static async Task<T> WaitAsync<T>(Func<Task<T?>> done, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (await done() is { } result) return result;
            await Task.Delay(100, timeout.Token);
        }
    }

    sealed class TestSession(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var value)) return Task.FromResult(AuthenticateResult.NoResult());
            var parts = value.ToString().Split(';');
            var user = new User { Id = parts[0], Name = parts[1], IsAdmin = bool.Parse(parts[2]) };
            var principal = AuthSetup.SessionPrincipal(user);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(principal.Identities), Scheme.Name)));
        }
    }
}
