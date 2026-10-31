using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>The AI: a model service, scanning whole books and Ask.</summary>
    public sealed class AiTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
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
            var processingBefore = (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))!;
            using var notYet = await client.PostAsync("/api/processing/run-again/ai", null, ct);
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
            var offered = (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))!.RunAgain?.SingleOrDefault(o => o.Step == "ai");
            using var readAgain = await client.PostAsync("/api/processing/run-again/ai", null, ct);
            var readingAgain = (await readAgain.Content.ReadFromJsonAsync<RunAgainDone>(ct))!;
            var readTwice = await WaitAsync(async () => _models.Requests.Count >= 2 && (await client.GetFromJsonAsync<AiPage>("/api/ai", ct))!.Progress is var p
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
            Assert.DoesNotContain(processingBefore.RunAgain ?? [], o => o.Step == "ai");
            Assert.Equal(HttpStatusCode.NotFound, notYet.StatusCode);
            Assert.Equal("Read 1 again", offered?.Confirm);
            Assert.Equal("The books go back to the AI one at a time, in the background.", readingAgain.Note);
            Assert.NotEmpty(readTwice);
            Assert.Equal(2, _models.Requests.Count);
            Assert.All(_models.Requests, r => Assert.Equal(("https://api.example.com", FakeModelService.Key), (r.Endpoint, r.Key)));
            Assert.DoesNotContain(FakeModelService.Key, sealedKeys, StringComparison.Ordinal);
            // The one book is too short to be a sample, so the test says so.
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
    }
}
