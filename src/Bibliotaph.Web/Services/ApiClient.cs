using System.Net;
using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Adds the header the server requires on every call from Bibliotaph's own pages.</summary>
sealed class ClientHeaderHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add(ApiHeaders.Client, "1");
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>The server's API. The session cookie goes with every call because the client is served from the same origin.</summary>
public sealed class ApiClient(HttpClient http)
{
    /// <summary>The signed-in user, or null when nobody is.</summary>
    public async Task<CurrentUser?> MeAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/me", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CurrentUser>(ct);
    }

    public async Task<IReadOnlyList<SignInProvider>> ProvidersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<SignInProvider>>("auth/providers", ct) ?? [];

    public async Task<LibrarySummary> LibrarySummaryAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LibrarySummary>("api/library/summary", ct) ?? new LibrarySummary(0, 0);

    public async Task<IReadOnlyList<InviteInfo>> InvitesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<InviteInfo>>("api/invites", ct) ?? [];

    public async Task<CreatedInvite> CreateInviteAsync(CreateInvite request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/invites", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedInvite>(ct))!;
    }

    public async Task<IReadOnlyList<StorageAccount>> StorageAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<StorageAccount>>("api/storage", ct) ?? [];

    public async Task DisconnectStorageAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/storage/{Uri.EscapeDataString(id)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<StorageFolderList> StorageFoldersAsync(string id, string path, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/storage/{Uri.EscapeDataString(id)}/folders?path={Uri.EscapeDataString(path)}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StorageFolderList>(ct))!;
    }

    public async Task<IReadOnlyList<LibraryFolder>> LibraryFoldersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<LibraryFolder>>("api/library/folders", ct) ?? [];

    public async Task<IReadOnlyList<LibraryFolder>> AddLibraryFolderAsync(AddLibraryFolder request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/library/folders", request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<LibraryFolder>>(ct))!;
    }

    public async Task<IReadOnlyList<LibraryFolder>> RemoveLibraryFolderAsync(long id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/library/folders/{id}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<LibraryFolder>>(ct))!;
    }

    public async Task<LibraryStatus?> LibraryStatusAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/library/status", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<LibraryStatus>(ct);
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("api/library/rescan", null, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>What a type-ahead filter offers for what was typed, among the books the query keeps.</summary>
    public Task<List<ValueSuggestion>> SuggestAsync(string field, string text, LibraryQuery query, CancellationToken ct = default)
    {
        var filters = LibraryUrl.QueryString(query, forApi: true);
        var url = $"api/library/values/{Uri.EscapeDataString(field)}{(filters.Length == 0 ? "?" : filters + "&")}text={Uri.EscapeDataString(text)}";
        return SendAsync<List<ValueSuggestion>>(HttpMethod.Get, url, null, ct);
    }

    public async Task<LibraryResults> LibraryAsync(LibraryQuery query, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/library{LibraryUrl.QueryString(query, forApi: true)}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<LibraryResults>(ct))!;
    }

    public async Task<HomeShelves> HomeAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<HomeShelves>("api/home", ct) ?? new HomeShelves([], [], "");

    /// <summary>A book's details, or null when it isn't in the library (any more).</summary>
    public async Task<BookDetails?> BookAsync(long entryId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/library/books/{entryId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<BookDetails>(ct);
    }

    static string Book(long entryId) => $"api/library/books/{entryId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>A value typed for a field, a list comma-separated: the details as they are now, or what's wrong with it.</summary>
    public Task<EditResult> SetFieldAsync(long entryId, string key, string text, CancellationToken ct = default) =>
        SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/fields/{Uri.EscapeDataString(key)}", new FieldEdit(text), ct);

    /// <summary>Keep this, Use this, Not right or Reset to suggestion on a field's evidence; <paramref name="text"/> is the value meant.</summary>
    public Task<EditResult> FieldActionAsync(long entryId, string key, string action, string text = "", CancellationToken ct = default) =>
        SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/fields/{Uri.EscapeDataString(key)}/{action}", new FieldEdit(text), ct);

    public async Task<string> ResolutionAsync(string key, string text, CancellationToken ct = default) =>
        (await SendAsync<Resolution>(HttpMethod.Get, $"api/library/fields/{Uri.EscapeDataString(key)}/resolution?text={Uri.EscapeDataString(text)}", null, ct)).Text;

    public Task EndCorrectionAsync(long entryId, bool decided, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{Book(entryId)}/correction", new CorrectionEnd(decided), ct);

    public Task SetFavoriteAsync(long entryId, bool favorite, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"{Book(entryId)}/favorite", new SetFavorite(favorite), ct);

    public Task<BookNotes> NotesAsync(long entryId, CancellationToken ct = default) => SendAsync<BookNotes>(HttpMethod.Get, $"{Book(entryId)}/notes", null, ct);

    /// <summary>The book's own note, saved; the Notes tab as it is now.</summary>
    public Task<BookNotes> SetNoteAsync(long entryId, string text, CancellationToken ct = default) =>
        SendAsync<BookNotes>(HttpMethod.Put, $"{Book(entryId)}/notes", new FieldEdit(text), ct);

    public Task<BookDetails> ReprocessAsync(long entryId, CancellationToken ct = default) =>
        SendAsync<BookDetails>(HttpMethod.Post, $"{Book(entryId)}/reprocess", null, ct);

    public Task<EditResult> AddPartAsync(long entryId, PartEdit part, CancellationToken ct = default) =>
        SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/parts", part, ct);

    public Task<ActionResult> SetPartPagesAsync(long partId, PartEdit pages, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, $"{Book(partId)}/pages", pages, ct);

    public Task<ActionResult> RemovePartAsync(long partId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Delete, $"{Book(partId)}/part", null, ct);

    /// <summary>A pack's cover: one of its files, or null for the mosaic.</summary>
    public Task<BookDetails> UsePackCoverAsync(long packId, long? member, CancellationToken ct = default) =>
        SendAsync<BookDetails>(HttpMethod.Put, $"{Book(packId)}/cover", new PackCover(member), ct);

    public Task<ActionResult> SplitPackAsync(long packId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Book(packId)}/split", null, ct);

    public Task<ActionResult> MakeCurrentAsync(long entryId, long documentId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/copies/{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/current", null, ct);

    public Task<ActionResult> NotSameBookAsync(long entryId, long documentId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/copies/{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/not-same", null, ct);

    /// <summary>An Undo, or Change them, by the token the server gave for it.</summary>
    public Task<ActionResult> RunHeldAsync(string token, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"api/library/held/{Uri.EscapeDataString(token)}", null, ct);

    // The sidebar's pins, and saved searches.

    public Task<PinList> PinsAsync(CancellationToken ct = default) => SendAsync<PinList>(HttpMethod.Get, "api/pins", null, ct);

    public Task<PinList> PinAsync(PinRef pin, CancellationToken ct = default) => SendAsync<PinList>(HttpMethod.Post, "api/pins", pin, ct);

    public Task<PinList> UnpinAsync(PinRef pin, CancellationToken ct = default) => SendAsync<PinList>(HttpMethod.Post, "api/pins/remove", pin, ct);

    public Task<PinList> MovePinAsync(PinMove move, CancellationToken ct = default) => SendAsync<PinList>(HttpMethod.Post, "api/pins/move", move, ct);

    static string Search(long id) => $"api/searches/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public Task<List<SavedSearchInfo>> SavedSearchesAsync(CancellationToken ct = default) => SendAsync<List<SavedSearchInfo>>(HttpMethod.Get, "api/searches", null, ct);

    /// <summary>A saved search, or null when it has gone.</summary>
    public async Task<SavedSearch?> SavedSearchAsync(long id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(Search(id), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<SavedSearch>(ct);
    }

    public Task<SavedSearchCreated> SaveSearchAsync(SavedSearchSave search, CancellationToken ct = default) =>
        SendAsync<SavedSearchCreated>(HttpMethod.Post, "api/searches", search, ct);

    public Task<ActionResult> UpdateSearchAsync(long id, SavedSearchChange change, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, Search(id), change, ct);

    public Task<ActionResult> RenameSearchAsync(long id, string name, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, $"{Search(id)}/name", new SavedSearchRename(name), ct);

    public Task<ActionResult> DeleteSearchAsync(long id, CancellationToken ct = default) => SendAsync<ActionResult>(HttpMethod.Delete, Search(id), null, ct);

    public Task<SelectionInfo> SelectionAsync(SelectionQuery query, CancellationToken ct = default) =>
        SendAsync<SelectionInfo>(HttpMethod.Post, "api/library/selection", query, ct);

    public Task<SameBookPreview> SameBookAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        SendAsync<SameBookPreview>(HttpMethod.Post, "api/library/selection/same-book", new SelectedBooks(books), ct);

    public Task<ActionResult> JoinAsync(SameBookJoin join, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/library/selection/same-book/join", join, ct);

    public Task<PackTogetherPreview> PackPreviewAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        SendAsync<PackTogetherPreview>(HttpMethod.Post, "api/library/selection/pack", new SelectedBooks(books), ct);

    public Task<ActionResult> PackAsync(PackTogetherRequest request, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/library/selection/pack/make", request, ct);

    public Task<ActionResult> RemoveAdventuresAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/library/selection/remove-adventures", new SelectedBooks(books), ct);

    public Task<ExportedCsv> ExportAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        SendAsync<ExportedCsv>(HttpMethod.Post, "api/library/selection/export", new SelectedBooks(books), ct);

    public Task<BulkEditForm> BulkEditAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        SendAsync<BulkEditForm>(HttpMethod.Post, "api/library/selection/edit", new SelectedBooks(books), ct);

    public Task<BulkReadResult> BulkReadAsync(BulkReadRequest request, CancellationToken ct = default) =>
        SendAsync<BulkReadResult>(HttpMethod.Post, "api/library/selection/edit/read", request, ct);

    public Task<IReadOnlyList<BulkSummaryItem>> BulkReviewAsync(BulkEditRequest request, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<BulkSummaryItem>>(HttpMethod.Post, "api/library/selection/edit/review", request, ct);

    public Task<BulkApplyResult> BulkApplyAsync(BulkEditRequest request, CancellationToken ct = default) =>
        SendAsync<BulkApplyResult>(HttpMethod.Post, "api/library/selection/edit/apply", request, ct);

    static string Binder(long id) => $"api/binders/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The Binders page's groups, upcoming and earlier as of <paramref name="today"/> where the user is.</summary>
    public Task<BinderList> BindersAsync(DateOnly today, CancellationToken ct = default) =>
        SendAsync<BinderList>(HttpMethod.Get, $"api/binders?today={today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}", null, ct);

    // Needs review.

    public Task<ReviewSuggestions> ReviewAsync(CancellationToken ct = default) => SendAsync<ReviewSuggestions>(HttpMethod.Get, "api/review", null, ct);

    public Task<ActionResult> AcceptSuggestionAsync(ReviewFieldDecision decision, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/review/fields/accept", decision, ct);

    public Task<ActionResult> RejectSuggestionAsync(ReviewFieldDecision decision, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/review/fields/reject", decision, ct);

    public Task<ActionResult> EditSuggestionAsync(ReviewFieldEdit edit, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/review/fields/edit", edit, ct);

    public Task<ReviewAcceptedAll> AcceptAllSuggestionsAsync(ReviewAcceptAll request, CancellationToken ct = default) =>
        SendAsync<ReviewAcceptedAll>(HttpMethod.Post, "api/review/fields/accept-all", request, ct);

    public Task<ActionResult> AddTermAsync(long id, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"api/review/terms/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/add", null, ct);

    public Task<ActionResult> RejectTermAsync(long id, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"api/review/terms/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/reject", null, ct);

    public Task<ActionResult> FileTermAsync(long id, string target, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"api/review/terms/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/map", new ReviewTermMap(target), ct);

    public Task<ActionResult> AnswerOfferAsync(ReviewOfferAnswer answer, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/answer", answer, ct);

    public Task<ActionResult> AcceptPartsAsync(ReviewPartsAccept accept, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/parts/accept", accept, ct);

    public Task<ActionResult> SetPartPagesAsync(ReviewPartPagesSet set, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/parts/pages", set, ct);

    public Task<ActionResult> UsePlaceAsync(ReviewPlaceUse use, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/places/use", use, ct);

    // Books owned elsewhere.

    public Task<ElsewhereChoices> ElsewhereChoicesAsync(CancellationToken ct = default) =>
        SendAsync<ElsewhereChoices>(HttpMethod.Get, "api/library/elsewhere/choices", null, ct);

    public Task<ElsewhereAdded> AddElsewhereAsync(ElsewhereAdd add, CancellationToken ct = default) =>
        SendAsync<ElsewhereAdded>(HttpMethod.Post, "api/library/elsewhere", add, ct);

    /// <summary>A picture picked in the Add dialog, read as a cover before the book is made.</summary>
    public Task<ChosenCoverRead> ReadCoverAsync(Stream picture, string contentType, CancellationToken ct = default) =>
        SendAsync<ChosenCoverRead>(HttpMethod.Post, "api/library/elsewhere/cover", Picture(picture, contentType), ct);

    public Task<BookDetails> ChooseCoverAsync(long entryId, Stream picture, string contentType, CancellationToken ct = default) =>
        SendAsync<BookDetails>(HttpMethod.Put, $"api/library/books/{Id(entryId)}/chosen-cover", Picture(picture, contentType), ct);

    public Task<BookDetails> RemoveChosenCoverAsync(long entryId, CancellationToken ct = default) =>
        SendAsync<BookDetails>(HttpMethod.Delete, $"api/library/books/{Id(entryId)}/chosen-cover", null, ct);

    public Task<ActionResult> RemoveElsewhereAsync(long entryId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Delete, $"api/library/books/{Id(entryId)}/elsewhere", null, ct);

    static StreamContent Picture(Stream picture, string contentType)
    {
        var content = new StreamContent(picture);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType.Length > 0 ? contentType : "application/octet-stream");
        return content;
    }

    static string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public Task<ReviewFiles> AttentionAsync(CancellationToken ct = default) => SendAsync<ReviewFiles>(HttpMethod.Get, "api/review/files", null, ct);

    public Task<ReviewFiles> RetryAsync(IReadOnlyList<long> documents, CancellationToken ct = default) =>
        SendAsync<ReviewFiles>(HttpMethod.Post, "api/review/files/retry", new ReviewRetry(documents), ct);

    /// <summary>Add to binder's menu: the binder worked on last first, then another and a new one.</summary>
    public Task<BinderMenu> BinderMenuAsync(CancellationToken ct = default) => SendAsync<BinderMenu>(HttpMethod.Get, "api/binders/menu", null, ct);

    public Task<ActionResult> AddToBinderAsync(long id, BinderAdd add, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Binder(id)}/add", add, ct);

    public Task<BinderCreated> CreateBinderAsync(BinderEdit edit, CancellationToken ct = default) =>
        SendAsync<BinderCreated>(HttpMethod.Post, "api/binders", edit, ct);

    /// <summary>Opening a binder's page makes it the current one, which Add to binder adds to.</summary>
    public Task<BinderPage> OpenBinderAsync(long id, CancellationToken ct = default) => SendAsync<BinderPage>(HttpMethod.Post, $"{Binder(id)}/open", null, ct);

    public Task<BinderPage> BinderAsync(long id, CancellationToken ct = default) => SendAsync<BinderPage>(HttpMethod.Get, Binder(id), null, ct);

    public Task<ActionResult> EditBinderAsync(long id, BinderEdit edit, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, Binder(id), edit, ct);

    public Task SetBinderNotesAsync(long id, string notes, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"{Binder(id)}/notes", new BinderNotes(notes), ct);

    public Task<BinderCreated> DuplicateBinderAsync(long id, CancellationToken ct = default) =>
        SendAsync<BinderCreated>(HttpMethod.Post, $"{Binder(id)}/duplicate", null, ct);

    public Task<ActionResult> DeleteBinderAsync(long id, CancellationToken ct = default) => SendAsync<ActionResult>(HttpMethod.Delete, Binder(id), null, ct);

    public Task<ActionResult> AddSectionAsync(long id, string name, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Binder(id)}/sections", new BinderName(name), ct);

    public Task<ActionResult> RenameSectionAsync(long id, long section, string name, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, $"{Binder(id)}/sections/{section.ToString(System.Globalization.CultureInfo.InvariantCulture)}", new BinderName(name), ct);

    public Task<ActionResult> DeleteSectionAsync(long id, long section, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Delete, $"{Binder(id)}/sections/{section.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, ct);

    static string BinderItem(long id) => $"api/binders/items/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public Task SetBinderItemAsync(long item, string label, string note, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, BinderItem(item), new BinderItemEdit(label, note), ct);

    public Task<bool> MoveBinderItemAsync(long item, BinderItemMove move, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Post, $"{BinderItem(item)}/move", move, ct);

    public Task<ActionResult> RemoveBinderItemAsync(long item, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Delete, BinderItem(item), null, ct);

    static string Collection(long id) => $"api/collections/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public Task<CollectionList> CollectionsAsync(CancellationToken ct = default) => SendAsync<CollectionList>(HttpMethod.Get, "api/collections", null, ct);

    /// <summary>The cards of the collections at the top, or inside <paramref name="parent"/>.</summary>
    public Task<List<CollectionCard>> CollectionCardsAsync(long? parent = null, CancellationToken ct = default) =>
        SendAsync<List<CollectionCard>>(HttpMethod.Get,
            parent is { } id ? $"api/collections/cards?parent={id.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "api/collections/cards", null, ct);

    /// <summary>A collection's page, or null when it has gone.</summary>
    public async Task<CollectionPage?> CollectionAsync(long id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(Collection(id), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<CollectionPage>(ct);
    }

    public Task<CollectionCreated> CreateCollectionAsync(CollectionName name, CancellationToken ct = default) =>
        SendAsync<CollectionCreated>(HttpMethod.Post, "api/collections", name, ct);

    public Task<ActionResult> RenameCollectionAsync(long id, CollectionName name, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, Collection(id), name, ct);

    public Task<ActionResult> MoveCollectionAsync(long id, CollectionMove move, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, $"{Collection(id)}/parent", move, ct);

    public Task<ActionResult> DeleteCollectionAsync(long id, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Delete, Collection(id), null, ct);

    public Task<ActionResult> AddToCollectionAsync(long id, CollectionBooks books, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Collection(id)}/books", books, ct);

    public Task<ActionResult> RemoveFromCollectionAsync(long id, CollectionBooks books, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Collection(id)}/books/remove", books, ct);

    async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, ct);
    }

    async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = body switch { null => null, HttpContent content => content, _ => JsonContent.Create(body, body.GetType()) },
        };
        var response = await http.SendAsync(request, ct);
        try
        {
            await EnsureAsync(response, ct);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>A refused call becomes an <see cref="ApiProblemException"/> carrying the server's own words, when it gave any.</summary>
    static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemText>(ct);
            detail = problem?.Detail ?? problem?.Title;
        }
        throw new ApiProblemException(response.StatusCode, detail ?? "Bibliotaph couldn't do that just now. Try again in a moment.");
    }

    sealed record ProblemText(string? Title, string? Detail);

    public async Task<SignedOut> SignOutAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("auth/signout", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SignedOut>(ct))!;
    }
}

/// <summary>A call the server refused, with a message that can be shown as it is.</summary>
public sealed class ApiProblemException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
