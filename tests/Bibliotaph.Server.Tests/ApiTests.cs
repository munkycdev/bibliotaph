using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Accounts;
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

public sealed class ApiTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly FakeGraph _graph = new();
    readonly WebApplicationFactory<Program> _factory;

    public ApiTests() =>
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("Server:DataRoot", _folder.Path);
            host.ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(new FakeHttp(_graph)));
            host.ConfigureServices(services => services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestSession>(AuthSetup.SessionScheme + "-test", _ => { }));
            host.ConfigureServices(services => services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultAuthenticateScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultChallengeScheme = AuthSetup.SessionScheme + "-test";
            }));
        });

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
    public async Task Me_describes_the_signed_in_user()
    {
        var user = await AddUserAsync(admin: false);

        var me = await Client(user).GetFromJsonAsync<CurrentUser>("/api/me", TestContext.Current.CancellationToken);

        Assert.Equal(user.Id, me!.UserId);
        Assert.Equal("google", Assert.Single(me.SignIns).Provider);
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
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Id, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
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
    public async Task Collections_are_made_filled_and_changed_as_the_windows_app_changes_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await AddUserAsync(admin: false);
        var connections = _factory.Services.GetRequiredService<StorageConnections>();
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Id, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
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
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Id, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
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
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Id, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
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
        var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Id, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
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
    public async Task The_health_check_needs_no_session()
    {
        using var response = await Client().GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Signs in whoever the test header names, in place of the session cookie.</summary>
    static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(600, 400);
        bitmap.Erase(SkiaSharp.SKColors.SeaGreen);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
