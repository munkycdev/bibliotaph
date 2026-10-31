using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>A book's details, its text and the checks on what the browser sends.</summary>
    public sealed class BooksTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
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
        public async Task A_locked_pdf_opens_to_unlock_and_its_password_makes_it_searchable_and_is_remembered_sealed()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/locked.pdf", await File.ReadAllBytesAsync(_pdfs.PasswordProtected, ct));
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }

            var locked = await WaitAsync(async () => (await client.GetFromJsonAsync<ReviewFiles>("/api/review/files", ct))!.Cards
                .SelectMany(c => c.Files).FirstOrDefault(f => f.CanUnlock), ct);
            var document = $"/api/library/documents/{locked.DocumentId}/password";
            var before = (await client.GetFromJsonAsync<ReaderPassword>(document, ct))!;
            using var kept = await client.PostAsJsonAsync(document, new ReaderPasswordUse(SyntheticPdfs.Password, Remember: true, Searchable: true), ct);
            var found = await WaitAsync(async () => (await client.GetFromJsonAsync<ReaderFind>(
                $"/api/library/documents/{locked.DocumentId}/find?q={Uri.EscapeDataString("owlbear")}", ct))!.Pages is { Count: > 0 } pages ? pages : null, ct);
            var remembered = (await client.GetFromJsonAsync<ReaderPassword>(document, ct))!;
            var listed = (await client.GetFromJsonAsync<List<RememberedPassword>>("/api/library/passwords", ct))!;
            string? sealedPasswords;
            await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
                sealedPasswords = await lease.Library.Get<SettingsStore>().GetAsync(SettingKeys.ServerPdfPasswords, ct);
            using var forgot = await client.DeleteAsync($"/api/library/passwords/{listed[0].ContentHash}", ct);
            var after = (await client.GetFromJsonAsync<ReaderPassword>(document, ct))!;

            Assert.NotNull(locked.EntryId);
            Assert.False(locked.CanRetry);
            Assert.Null(before.Password);
            Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
            Assert.NotEmpty(found);
            Assert.Equal(SyntheticPdfs.Password, remembered.Password);
            Assert.Single(listed);
            Assert.DoesNotContain(SyntheticPdfs.Password, sealedPasswords, StringComparison.Ordinal);
            Assert.Empty((await forgot.Content.ReadFromJsonAsync<List<RememberedPassword>>(ct))!);
            Assert.Null(after.Password);
        }

        [Fact]
        public async Task Ai_reading_says_why_no_book_is_being_read_and_lists_what_was_read()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var off = (await client.GetFromJsonAsync<AiReadingPage>("/api/ai/reading", ct))!;
            await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
            {
                var feed = lease.Library.Get<AiReadingFeed>();
                feed.Start(7, "The Sunken Lantern", "fake-mini");
                feed.Fail(7, "The model's answer was cut off");
                feed.Start(8, "Tides of the Lantern Coast", "fake-mini");
            }
            var reading = (await client.GetFromJsonAsync<AiReadingPage>("/api/ai/reading", ct))!;

            Assert.Equal(("AI is off, or not set up yet, so no book is being read.", null, 0), (off.Idle, off.Now, off.Recent.Count));
            Assert.Null(reading.Idle);
            Assert.Equal("Tides of the Lantern Coast", reading.Now!.Title);
            Assert.StartsWith("Waiting for fake-mini", reading.Now.Step, StringComparison.Ordinal);
            var failed = Assert.Single(reading.Recent);
            Assert.Equal(("The Sunken Lantern", "The model's answer was cut off"), (failed.Title, failed.Problem));
            Assert.StartsWith("Couldn't be read", failed.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Check_a_download_judges_what_the_browser_sends_without_the_file()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            var bytes = await File.ReadAllBytesAsync(_pdfs.WatermarkedForAna, ct);
            _graph.Add("RPG/known.pdf", bytes);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var book = await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books
                .SingleOrDefault(b => b.HasText), ct);
            // As the browser makes them from the pages it reads: here, the library's own, so the file sent is a copy.
            var fingerprints = await WaitAsync(async () =>
            {
                await using var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct);
                var stored = await lease.Library.Get<Bibliotaph.Index.IndexQueries>().GetFingerprintsAsync(book.DocumentId, ct);
                return stored.Count(f => f is not null) >= 3 ? stored : null;
            }, ct);
            async Task<CheckResult> CheckAsync(CheckFile file)
            {
                using var response = await client.PostAsJsonAsync("/api/library/check", file, ct);
                return (await response.Content.ReadFromJsonAsync<CheckResult>(ct))!;
            }
            var other = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([1, 2, 3]));

            var same = await CheckAsync(new CheckFile("known.pdf", "Bundle/known.pdf", "known.pdf",
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), null, null));
            var copy = await CheckAsync(new CheckFile("watermarked.pdf", "Bundle/watermarked.pdf", "watermarked.pdf", other, null,
                new CheckPages(fingerprints, fingerprints.Count(f => f is not null))));
            var locked = await CheckAsync(new CheckFile("locked.pdf", "Bundle/locked.pdf", "locked.pdf", other, null, new CheckPages([], 0, "password")));
            var nested = await CheckAsync(new CheckFile("inner.zip", "Bundle/outer.zip/inner.zip", "outer.zip/inner.zip", null, ArchiveProblems.Nested, null));

            Assert.True(same.Owned);
            Assert.Equal("Already owned", same.Label);
            Assert.Equal(book.EntryId, same.MatchEntryId);
            Assert.Equal("Another copy", copy.Label);
            Assert.False(copy.Owned);
            Assert.StartsWith("Another copy of ", copy.Detail, StringComparison.Ordinal);
            Assert.Equal(("New", "It needs a password, so it was judged by its name."), (locked.Label, locked.Detail));
            Assert.Equal(("Couldn't be read", ArchiveProblems.Nested), (nested.Label, nested.Detail));
            Assert.True(nested.Order > copy.Order && copy.Order > locked.Order);
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
        public async Task An_unknown_API_path_is_404_not_the_web_page()
        {
            var user = await AddUserAsync(admin: false);

            using var response = await Client(user).GetAsync("/api/nothing-here", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task The_health_check_needs_no_session()
        {
            using var response = await Client().GetAsync("/healthz", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
