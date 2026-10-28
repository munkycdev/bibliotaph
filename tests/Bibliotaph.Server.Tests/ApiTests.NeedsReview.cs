using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Needs review's cards, as the Windows app decides them.</summary>
    public sealed class NeedsReviewTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
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
            Assert.Equal(("Sources disagree.", "Pathfinder", "Dungeons & Dragons", "Accept all 2", "Keep current"),
                (first.Reason, first.CurrentText, first.SuggestedText, first.AcceptAllLabel, first.RejectLabel));
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
            // Every file read to the end first, so a suggestion made meanwhile can't change the counts compared below.
            await WaitAsync(async () => (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))! is { Files: 2, ToRead: 0, Queued: 2, Indexed: 2 } done ? done : null, ct);
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
    }
}
