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
        public async Task Needs_review_asks_whether_a_record_whose_title_isnt_printed_is_the_books_and_same_book_fills_it_in_as_the_catalogues()
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
                string hash;
                await using (var db = lease.Library.Catalog.CreateContext())
                    hash = db.EntrySources.Where(s => s.EntryId == book.EntryId).Select(s => s.Document.ContentHash).First();
                var record = new BookRecord("RPGGeek", "4021", "9781234567897", "The Owlbear Mill", ["Ada Quill"], ["Gull Rock Games"], "2019");
                await lease.Library.Get<LookupRecordStore>().SaveAsync(hash, [record], _ => false, ct);
            }

            var list = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
            var card = list.Offers.Single(o => o.Kind == "records");
            var same = await PostAsync<ActionResult>("/api/review/offers/answer", new ReviewOfferAnswer(card.Id, card.Stamp, "same"));
            var filled = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.Single();
            var afterSame = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
            await PostAsync<ActionResult>($"/api/library/held/{same.Undo}", null);
            var undone = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books.Single();
            var afterUndo = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;

            Assert.Contains(list.Show, s => s.Key == "records" && s.Name == "Same book?");
            Assert.Equal((book.Title, "Same book?", book.EntryId), (card.Title, card.Chip, card.Book?.EntryId));
            Assert.Equal("RPGGeek's record for its ISBN is of The Owlbear Mill, which isn't printed in it. Is it this book?", card.Heading);
            Assert.Equal("The Owlbear Mill · Ada Quill · Gull Rock Games · 2019", card.Evidence);
            Assert.Equal(["Same book", "Not this book"], card.Choices.Select(c => c.Label));
            Assert.Equal($"Filled in {book.Title} from RPGGeek's record.", same.Message);
            Assert.Equal("The Owlbear Mill", filled.Title);
            Assert.DoesNotContain(afterSame.Offers, o => o.Kind == "records");
            Assert.Equal(book.Title, undone.Title);
            Assert.Contains(afterUndo.Offers, o => o.Id == card.Id);

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
            // Read to the end first, so its own proposals and title can't replace the ones saved below while the card is answered.
            await WaitAsync(async () => (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))! is { Files: 1, ToRead: 0, Queued: 1, Indexed: 1 } done ? done : null, ct);
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

        [Fact]
        public async Task A_vocabulary_can_take_the_AIs_new_terms_at_once_which_adds_the_ones_waiting_in_Needs_review()
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

            // A campaign setting the AI named on both books, which the vocabulary doesn't know yet.
            await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
            {
                var library = lease.Library;
                var (reach, _) = await library.Get<VocabularyStore>().ProposeTermAsync("setting", "Thornwick Reach", ct);
                foreach (var book in books)
                    await library.Get<MetadataStore>().AddSuggestionsAsync(new EntryId(book.EntryId), null,
                        [new(MetadataFields.Settings, reach.Key, AssertionOrigin.Ai, "set in Thornwick Reach")], ct);
                await library.Get<MetadataProjector>().ProjectAllAsync(ct);
            }

            var asking = await client.GetFromJsonAsync<AiTermChoice>("/api/vocabulary/setting/ai-terms", ct);
            var waiting = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
            using var chosen = await client.PutAsJsonAsync("/api/vocabulary/setting/ai-terms", new AiTermChoice(true), ct);
            var change = await chosen.Content.ReadFromJsonAsync<AiTermChange>(ct);
            var adding = await client.GetFromJsonAsync<AiTermChoice>("/api/vocabulary/setting/ai-terms", ct);
            var terms = await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/setting", ct);
            var after = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
            using var editions = await client.GetAsync("/api/vocabulary/edition/ai-terms", ct);

            Assert.False(asking!.Add);
            Assert.Equal("Thornwick Reach", Assert.Single(waiting.Terms).Label);
            Assert.Equal(new AiTermChange(true, 1), change);
            Assert.True(adding!.Add);
            Assert.Contains(terms!, t => t.Label == "Thornwick Reach" && !t.Yours);
            Assert.Empty(after.Terms);
            Assert.Equal(HttpStatusCode.NotFound, editions.StatusCode);
        }

        [Fact]
        public async Task Needs_review_offers_overlapping_terms_to_combine_with_undo_or_keep_apart_and_settings_counts_them()
        {
            var ct = TestContext.Current.CancellationToken;
            var client = Client(await AddUserAsync(admin: false));
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using var twin = await client.PostAsJsonAsync("/api/vocabulary/setting", new NewTerm("The Forgotten Realms"), ct);
            var twinId = (await twin.Content.ReadFromJsonAsync<VocabularyChange>(ct))!.TermId!.Value;
            using var typo = await client.PostAsJsonAsync("/api/vocabulary/setting", new NewTerm("Grayhawk"), ct);
            var typoId = (await typo.Content.ReadFromJsonAsync<VocabularyChange>(ct))!.TermId!.Value;
            var terms = (await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/setting", ct))!;
            var realmsId = terms.Single(t => t.Label == "Forgotten Realms").Id;
            var greyhawkId = terms.Single(t => t.Label == "Greyhawk").Id;

            var settings = await PostAsync<TermOverlapPage>("/api/vocabulary/setting/overlaps", null);
            var list = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
            var realms = list.Overlaps.Single(o => o.Terms.Any(t => t.Id == realmsId));
            var hawks = list.Overlaps.Single(o => o.Terms.Any(t => t.Id == typoId));
            var combined = await PostAsync<ActionResult>($"/api/review/overlaps/{realms.Id}/combine", new ReviewOverlapCombine(realms.Stamp, realmsId));
            var after = (await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/setting", ct))!;
            var afterCombine = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
            using var stale = await client.PostAsJsonAsync($"/api/review/overlaps/{realms.Id}/combine", new ReviewOverlapCombine(realms.Stamp, realmsId), ct);
            await PostAsync<ActionResult>($"/api/library/held/{combined.Undo}", null);
            var undone = (await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/setting", ct))!;
            var apart = await PostAsync<ActionResult>($"/api/review/overlaps/{hawks.Id}/keep-apart", new ReviewOverlapKeepApart(hawks.Stamp));
            var kept = (await client.GetFromJsonAsync<TermOverlapPage>("/api/vocabulary/setting/overlaps", ct))!;
            var afterApart = (await client.GetFromJsonAsync<ReviewSuggestions>("/api/review", ct))!;
            using (await client.DeleteAsync($"/api/vocabulary/setting/kept-apart/{kept.KeptApart.Single().Id}", ct)) { }
            var forgotten = (await client.GetFromJsonAsync<TermOverlapPage>("/api/vocabulary/setting/overlaps", ct))!;
            using var unknown = await client.GetAsync("/api/vocabulary/nonsense/overlaps", ct);

            Assert.Equal((2, false), (settings.Waiting, settings.AsksAi));
            Assert.Contains(list.Show, k => k.Key == "overlaps" && k.Name == "Terms that may be the same");
            Assert.Equal(("Forgotten Realms · The Forgotten Realms", "Same setting?", realmsId), (realms.Label, realms.Chip, realms.KeepId));
            Assert.Equal("The same name but for small words, brackets or a plural.", realms.Detail);
            Assert.Equal(("Greyhawk · Grayhawk", greyhawkId), (hawks.Label, hawks.KeepId));
            Assert.Equal("Combined 2 terms into Forgotten Realms", combined.Message);
            Assert.NotNull(combined.Undo);
            Assert.DoesNotContain(after, t => t.Id == twinId);
            Assert.Contains(after.Single(t => t.Id == realmsId).Aliases, a => a.Text == "The Forgotten Realms");
            Assert.Equal([hawks.Id], afterCombine.Overlaps.Select(o => o.Id));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("The Forgotten Realms", undone.Single(t => t.Id == twinId).Label);
            Assert.DoesNotContain(undone.Single(t => t.Id == realmsId).Aliases, a => a.Text == "The Forgotten Realms");
            Assert.Equal("Kept Greyhawk and Grayhawk apart.", apart.Message);
            Assert.NotNull(apart.Undo);
            Assert.Equal(("Grayhawk", "Greyhawk"), (kept.KeptApart.Single().First, kept.KeptApart.Single().Second));
            Assert.Equal(1, kept.Waiting);
            Assert.Equal([realms.Id], afterApart.Overlaps.Select(o => o.Id));
            Assert.Equal(2, forgotten.Waiting);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            async Task<T> PostAsync<T>(string url, object? body)
            {
                using var response = body is null ? await client.PostAsync(url, null, ct) : await client.PostAsJsonAsync(url, body, ct);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>(ct))!;
            }
        }
    }
}
