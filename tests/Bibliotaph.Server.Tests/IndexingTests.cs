using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Api;
using Bibliotaph.Server.Indexing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Storage.OneDrive;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Bibliotaph.Server.Tests;

/// <summary>Books on a drive read by the server as the Windows app reads them: the real PDF worker, Skia for images.</summary>
public sealed class IndexingTests(SyntheticPdfs pdfs) : IClassFixture<SyntheticPdfs>, IDisposable
{
    readonly TempFolder _folder = new();
    LibraryFixture? _fixture;

    LibraryFixture Fixture => _fixture ??= new LibraryFixture(_folder.Path, TimeProvider.System);

    public void Dispose()
    {
        _fixture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _folder.Dispose();
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pdf_on_a_drive_is_read_for_its_text_and_cover_and_its_copy_deleted()
    {
        Fixture.Graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(pdfs.KnownText, Ct));
        Fixture.Graph.Add("RPG/map.png", Png(1200, 800));
        var (userId, _) = await Fixture.UserWithDriveAsync();
        await using var lease = await Fixture.Libraries.OpenAsync(userId, Ct);
        var library = lease.Library;
        var root = await library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        library.Indexing.RequestScan(root.Id);

        var queries = library.Get<IndexQueries>();
        var documents = await WaitAsync(async () =>
        {
            await using var db = library.Catalog.CreateContext();
            var ids = await db.Documents.Select(d => d.Id).ToListAsync(Ct);
            return ids.Count == 2 && (await Task.WhenAll(ids.Select(id => queries.GetStageStatusAsync(id, Stage.Covers, Ct)))).All(s => s == StageStatus.Complete)
                ? ids : null;
        });

        var texts = await Task.WhenAll(documents.Select(id => queries.GetAllPageTextsAsync(id, Ct)));
        Assert.Contains(texts, pages => pages.Any(p => p.Contains(SyntheticPdfs.KnownPhrase, StringComparison.Ordinal)));
        foreach (var id in documents) Assert.NotNull(await queries.GetCoverAsync(id, Ct));
        // Downloaded to hash, then once more to read: never again for each stage.
        Assert.Equal(4, Fixture.Graph.Downloads);
        await WaitAsync(() => Task.FromResult(Directory.Exists(library.Paths.Extract) && Directory.EnumerateFiles(library.Paths.Extract).Any() ? null : ""));
    }

    [Fact]
    public async Task The_library_page_shows_the_books_read_and_finds_their_pages()
    {
        Fixture.Graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(pdfs.KnownText, Ct));
        Fixture.Graph.Add("RPG/map.png", Png(1200, 800));
        var (userId, _) = await Fixture.UserWithDriveAsync();
        await using var lease = await Fixture.Libraries.OpenAsync(userId, Ct);
        var library = lease.Library;
        var root = await library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        library.Indexing.RequestScan(root.Id);

        var all = await WaitAsync(async () =>
        {
            var results = await LibraryApi.ResultsAsync(library, new LibraryQuery(), Ct);
            return results.Books.Count == 2 && results.Books.All(b => b.Cover is not null && !b.Meta.Contains("still being read", StringComparison.Ordinal))
                ? results : null;
        });
        Assert.Equal("2", all.CountText);
        Assert.Equal("documents", all.CountLabel);
        Assert.Contains(all.Books, b => b.Meta == "PNG image" && b.Size == "PNG");
        foreach (var book in all.Books) Assert.True(File.Exists(library.Get<CoverCache>().PathFor(book.Cover!)));
        Assert.Equal("RPG", all.Menus.Folders[1].Label);

        var images = await LibraryApi.ResultsAsync(library, new LibraryQuery { Kind = "Images" }, Ct);
        Assert.Equal("PNG image", Assert.Single(images.Books).Meta);
        var none = await LibraryApi.ResultsAsync(library, new LibraryQuery { Kind = "Packs" }, Ct);
        Assert.Equal("clear-filters", none.Empty?.Action);

        var word = SyntheticPdfs.KnownPhrase.Split(' ')[0];
        var pages = await LibraryApi.ResultsAsync(library, new LibraryQuery { Q = word, Tab = "pages" }, Ct);
        var match = Assert.Single(pages.Matches);
        Assert.Contains(LibraryChoices.HitStart, match.Pages[0].Snippet);
        Assert.StartsWith("PDF page ", match.Pages[0].Where, StringComparison.Ordinal);
        Assert.StartsWith("Inside documents · ", pages.PagesTab, StringComparison.Ordinal);
        Assert.Contains($"for “{word}”", pages.CountLabel, StringComparison.Ordinal);
        var nothing = await LibraryApi.ResultsAsync(library, new LibraryQuery { Q = "zzyzxnotaword" }, Ct);
        Assert.Equal("clear-search", nothing.Empty?.Action);

        // Home: the books added last; none opened yet.
        var home = await LibraryApi.HomeAsync(library, Ct);
        Assert.Equal(2, home.Added.Count);
        Assert.Empty(home.Opened);

        // A book's details: its fields with their sources, what is known of its file, where it is, and its processing.
        var pdf = all.Books.Single(b => b.Format == "PDF");
        var details = await DetailsApi.DetailsAsync(library, new EntryId(pdf.EntryId), Ct);
        Assert.NotNull(details);
        Assert.Equal("PDF", details.Eyebrow);
        Assert.Contains(details.Fields, f => f.Key == MetadataFields.Title.Key && f.Primary);
        Assert.Contains(details.Facts, f => f.Label == "Search" && f.Value.StartsWith("Text searchable", StringComparison.Ordinal));
        Assert.Equal("ABOUT THE FILE", details.FactsHeading);
        var location = Assert.Single(details.Locations);
        Assert.EndsWith("RPG › known.pdf", location.Path, StringComparison.Ordinal);
        Assert.Contains(details.Stages, s => s.Value == "Done");
        Assert.Empty(details.Copies);
        Assert.Null(await DetailsApi.DetailsAsync(library, new EntryId(long.MaxValue), Ct));

        // Covers outlive the server's disk: each is kept as indexing draws it, and one the disk lost comes back.
        var cover = all.Books[0].Cover!;
        await WaitAsync(() => Task.FromResult(all.Books.All(b => Fixture.Covers.Names.Contains($"{userId}/{b.Cover}")) ? "" : null));
        var path = library.Get<CoverCache>().PathFor(cover);
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        File.Delete(path);
        Assert.True(await library.Get<CoverMirror>().RestoreAsync(cover, Ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(await library.Get<CoverMirror>().RestoreAsync("nothere.jpg", Ct));
    }

    [Fact]
    public async Task A_book_opens_in_the_reader_at_its_place_with_a_link_to_its_file_in_the_drive()
    {
        var bytes = await File.ReadAllBytesAsync(pdfs.KnownText, Ct);
        Fixture.Graph.Add("RPG/known.pdf", bytes);
        var (userId, _) = await Fixture.UserWithDriveAsync();
        await using var lease = await Fixture.Libraries.OpenAsync(userId, Ct);
        var library = lease.Library;
        var root = await library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        library.Indexing.RequestScan(root.Id);
        var card = (await WaitAsync(async () =>
        {
            var results = await LibraryApi.ResultsAsync(library, new LibraryQuery(), Ct);
            return results.Books is [{ Cover: not null } only] ? only : null;
        }));
        var entry = new EntryId(card.EntryId);

        var opened = (await ReaderApi.OpenAsync(library, entry, null, null, null, false, Ct))!;
        Assert.Equal((card.DocumentId, "pdf", 3, 0, (long)bytes.Length), (opened.DocumentId, opened.Format, opened.PageCount, opened.StartPage, opened.Size));
        Assert.StartsWith("PDF · 3 pages", opened.Subtitle, StringComparison.Ordinal);
        Assert.Equal(64, opened.ContentHash.Length);
        Assert.True(opened.KeepsPlace);
        Assert.Null(opened.Problem);
        Assert.Null(opened.Notice);
        Assert.NotEmpty(opened.Outline);
        Assert.All(opened.Outline, o => Assert.InRange(o.Page, 0, 2));
        // Home's Recently opened shows it, as an open in the Windows app records it.
        Assert.Equal([card.EntryId], (await LibraryApi.HomeAsync(library, Ct)).Opened.Select(b => b.EntryId));

        // The browser reads the file through a link of the drive's own; the server never passes the bytes on.
        var downloads = Fixture.Graph.Downloads;
        var link = await ReaderApi.LinkAsync(library, opened.DocumentId, Ct);
        Assert.Equal("https://public.files.1drv.com/download/RPG%2Fknown.pdf", link.Url);
        Assert.Equal(downloads, Fixture.Graph.Downloads);

        // Find ignores case and how the text breaks, and keeps to the pages given.
        var texts = (await library.Get<IndexQueries>().GetPageTextsAsync(opened.DocumentId, Ct)).Select(t => (t.PdfPage, t.Text)).ToList();
        var shouted = SyntheticPdfs.KnownPhrase.ToUpperInvariant().Replace(" ", "  ", StringComparison.Ordinal);
        Assert.Equal([new ReaderFindPage(SyntheticPdfs.KnownPhrasePage, 1)], ReaderApi.Find(texts, shouted, null, null));
        Assert.Empty(ReaderApi.Find(texts, SyntheticPdfs.KnownPhrase, SyntheticPdfs.KnownPhrasePage + 1, null));
        Assert.Empty(ReaderApi.Find(texts, "   ", null, null));

        // An ordinary open goes back to where the book was left; a search hit opens at its page and keeps nothing.
        await library.Get<ReadingService>().SavePositionAsync(opened.DocumentId, 2, Ct);
        var again = (await ReaderApi.OpenAsync(library, entry, null, null, null, false, Ct))!;
        Assert.Equal(2, again.StartPage);
        Assert.Equal($"Back at page {ReaderPageNumbers.Display(2, again.Labels)}, where you left off.", again.Notice);
        var word = SyntheticPdfs.KnownPhrase.Split(' ')[0];
        var hit = (await ReaderApi.OpenAsync(library, entry, opened.DocumentId, SyntheticPdfs.KnownPhrasePage, word, false, Ct))!;
        Assert.Equal((SyntheticPdfs.KnownPhrasePage, false, null), (hit.StartPage, hit.KeepsPlace, hit.Notice));
        Assert.Equal([word], hit.Marks);
        // Popped out, the reader goes on keeping its place.
        Assert.True((await ReaderApi.OpenAsync(library, entry, opened.DocumentId, 1, null, true, Ct))!.KeepsPlace);

        // A file that has gone from the drive says so, in place of the pages.
        Fixture.Graph.Remove("RPG/known.pdf");
        var gone = await ReaderApi.LinkAsync(library, opened.DocumentId, Ct);
        Assert.Null(gone.Url);
        Assert.Equal("The file couldn't be found.", gone.Problem?.Title);
        Assert.Null(await ReaderApi.OpenAsync(library, new EntryId(long.MaxValue), null, null, null, false, Ct));
    }

    [Fact]
    public async Task Notes_on_a_books_pages_are_added_changed_and_deleted_with_Undo_in_the_reader()
    {
        Fixture.Graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(pdfs.KnownText, Ct));
        var (userId, _) = await Fixture.UserWithDriveAsync();
        await using var lease = await Fixture.Libraries.OpenAsync(userId, Ct);
        var library = lease.Library;
        var root = await library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        library.Indexing.RequestScan(root.Id);
        var card = await WaitAsync(async () =>
            (await LibraryApi.ResultsAsync(library, new LibraryQuery(), Ct)).Books is [{ Cover: not null } only] ? only : null);
        var document = card.DocumentId;
        var none = (await PageNotesApi.ListAsync(library, document, Ct))!;
        Assert.Equal(("Notes", 0), (none.Label, none.Notes.Count));
        Assert.Null(await PageNotesApi.ListAsync(library, long.MaxValue, Ct));

        var added = (await PageNotesApi.AddAsync(library, document, new PageNoteEdit(1, 2, " Ambush "), Ct))!;
        var note = Assert.Single(added.Notes.Notes);
        Assert.Equal(("Ambush", 1, 2, false, null), (note.Text, note.First, note.Last, note.OtherCopy, note.Reason));
        Assert.Equal($"Saved the note on {note.Pages}.", added.Done.Message);
        Assert.Equal("Notes (1)", added.Notes.Label);
        // The Notes tab of the details lists it too.
        Assert.Equal("Ambush", Assert.Single((await library.Get<NotesService>().GetPageNotesAsync(new EntryId(card.EntryId), Ct))).Text);

        var changed = (await PageNotesApi.UpdateAsync(library, document, note.Id, new PageNoteEdit(1, 2, "Ambush at the ford"), Ct))!;
        Assert.Equal(("Ambush at the ford", 1, 2), (changed.Notes.Notes[0].Text, changed.Notes.Notes[0].First, changed.Notes.Notes[0].Last));
        Assert.Equal("", changed.Done.Message);
        var moved = (await PageNotesApi.UpdateAsync(library, document, note.Id, new PageNoteEdit(0, 0, "Ambush at the ford"), Ct))!;
        Assert.Equal((0, 0), (moved.Notes.Notes[0].First, moved.Notes.Notes[0].Last));

        var deleted = (await PageNotesApi.DeleteAsync(library, document, note.Id, Ct))!;
        Assert.Empty(deleted.Notes.Notes);
        Assert.Equal($"Deleted the note on {moved.Notes.Notes[0].Pages}.", deleted.Done.Message);
        Assert.Equal("That note isn't there any more.", (await PageNotesApi.DeleteAsync(library, document, note.Id, Ct))!.Done.Message);
        var undo = library.Get<HeldActions>().Take(deleted.Done.Undo!)!;
        await undo(Ct);
        Assert.Equal("Ambush at the ford", Assert.Single((await PageNotesApi.ListAsync(library, document, Ct))!.Notes).Text);
    }

    [Fact]
    public async Task The_reader_changes_the_cover_marks_an_adventure_and_adds_pages_to_a_binder()
    {
        Fixture.Graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(pdfs.KnownText, Ct));
        var (userId, _) = await Fixture.UserWithDriveAsync();
        await using var lease = await Fixture.Libraries.OpenAsync(userId, Ct);
        var library = lease.Library;
        var root = await library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        library.Indexing.RequestScan(root.Id);
        var card = await WaitAsync(async () =>
            (await LibraryApi.ResultsAsync(library, new LibraryQuery(), Ct)).Books is [{ Cover: not null } only] ? only : null);
        var document = card.DocumentId;
        var opened = (await ReaderApi.OpenAsync(library, new EntryId(card.EntryId), null, null, null, false, Ct))!;
        Assert.Equal(0, opened.CoverPage);
        Assert.All(opened.Outline, o => Assert.InRange(o.LastPage, o.Page, opened.PageCount - 1));
        var printed = await ReaderApi.PrintedPagesAsync(library, document, Ct);
        Assert.All(printed.Pages, p => Assert.InRange(p, -1, opened.PageCount - 1));
        Assert.All(printed.Listed, p => Assert.InRange(p, 0, opened.PageCount - 1));

        var used = (await ReaderApi.UseCoverAsync(library, document, 1, Ct))!;
        Assert.Equal((1, "This page is the cover now."), (used.CoverPage, used.Message));
        Assert.Equal(1, (await ReaderApi.OpenAsync(library, new EntryId(card.EntryId), null, null, null, false, Ct))!.CoverPage);
        Assert.Equal((0, "The first page is the cover again."), ((await ReaderApi.UseCoverAsync(library, document, 0, Ct))!) is var back ? (back.CoverPage, back.Message) : default);
        Assert.Null(await ReaderApi.UseCoverAsync(library, long.MaxValue, 1, Ct));

        Assert.Equal("Give the adventure a title.", (await ReaderApi.MarkAsync(library, document, new PartEdit(" ", "1", ""), Ct)).Problem);
        Assert.Equal(ReaderApi.PagesHint, (await ReaderApi.MarkAsync(library, document, new PartEdit("Ambush", "999", ""), Ct)).Problem);
        var marked = await ReaderApi.MarkAsync(library, document, new PartEdit("Ambush", "3", "2"), Ct);
        Assert.Equal((null, "“Ambush” has a card of its own in the Library now."), (marked.Problem, marked.Message));
        Assert.Contains((await DetailsApi.DetailsAsync(library, new EntryId(card.EntryId), Ct))!.Parts, p => p.Title == "Ambush");

        var sessions = library.Get<SessionsService>();
        var binder = await sessions.CreateAsync("Harbor night", ct: Ct);
        var maps = await sessions.AddSectionAsync(binder.Id, "Maps", Ct);
        Assert.Equal("Added page 1 to Harbor night.", (await BindersApi.AddAsync(library, binder.Id, new BinderAdd([], DocumentId: document, Page: 0), Ct))!.Message);
        var pages = (await BindersApi.AddAsync(library, binder.Id,
            new BinderAdd([], DocumentId: document, Page: 2, LastPage: 1, Label: " Warehouse ambush ", SectionId: maps.Id), Ct))!;
        Assert.Equal("Added Warehouse ambush to Harbor night.", pages.Message);
        var item = (await sessions.GetAsync(binder.Id, Ct))!.Items.Single(i => i.Label == "Warehouse ambush");
        Assert.Equal((maps.Id, 1, 2), (item.SectionId, item.Range!.FirstPdfPage, item.Range.LastPdfPage));
        Assert.Equal("That couldn't be added.", (await BindersApi.AddAsync(library, binder.Id, new BinderAdd([], DocumentId: document, Page: 0, LastPage: 999), Ct))!.Message);
        Assert.Null(await BindersApi.AddAsync(library, long.MaxValue, new BinderAdd([], DocumentId: document, Page: 0), Ct));
        await library.Get<HeldActions>().Take(pages.Undo!)!(Ct);
        Assert.DoesNotContain((await sessions.GetAsync(binder.Id, Ct))!.Items, i => i.Label == "Warehouse ambush");
    }

    [Fact]
    public async Task A_binders_reader_opens_its_items_at_their_pages_adds_after_the_one_in_hand_and_uses_a_page()
    {
        Fixture.Graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(pdfs.KnownText, Ct));
        var (userId, _) = await Fixture.UserWithDriveAsync();
        await using var lease = await Fixture.Libraries.OpenAsync(userId, Ct);
        var library = lease.Library;
        var root = await library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        library.Indexing.RequestScan(root.Id);
        var card = await WaitAsync(async () =>
            (await LibraryApi.ResultsAsync(library, new LibraryQuery(), Ct)).Books is [{ Cover: not null } only] ? only : null);
        var document = card.DocumentId;
        var sessions = library.Get<SessionsService>();
        var binder = await sessions.CreateAsync("Harbor night", ct: Ct);
        await BindersApi.AddAsync(library, binder.Id, new BinderAdd([], DocumentId: document, Page: 1, LastPage: 2, Label: "Warehouse ambush"), Ct);
        await BindersApi.AddAsync(library, binder.Id, new BinderAdd([card.EntryId], card.Title), Ct);
        var (pages, whole) = (await sessions.GetAsync(binder.Id, Ct))!.Items is [var a, var b] ? (a, b) : default;

        var opened = (await ReaderApi.OpenAsync(library, new EntryId(card.EntryId), null, null, null, false, Ct, pages.Id))!;
        Assert.Equal(("Warehouse ambush", 1, false), (opened.Title, opened.StartPage, opened.KeepsPlace));
        Assert.Equal(new ReaderItem(pages.Id, "Harbor night", 1, 2, false), opened.Item);
        Assert.Equal((0, true), (await ReaderApi.OpenAsync(library, new EntryId(card.EntryId), null, null, null, false, Ct, whole.Id))! is var book
            ? (book.StartPage, book.Item!.WholeBook) : default);
        Assert.Null(await ReaderApi.OpenAsync(library, new EntryId(card.EntryId + 1000), null, null, null, false, Ct, pages.Id));

        var added = (await BindersApi.AddAfterAsync(library, binder.Id, new BinderPageAdd(document, 0, After: pages.Id), Ct))!.Value;
        Assert.Equal([pages.Id, added, whole.Id], (await sessions.GetAsync(binder.Id, Ct))!.Items.Select(i => i.Id));
        Assert.Null(await BindersApi.AddAfterAsync(library, binder.Id, new BinderPageAdd(document, 999), Ct));

        var used = (await BindersApi.UsePageAsync(library, pages.Id, new ReaderUsePage(document, 0), Ct))!;
        Assert.Equal((0, 1), (used.FirstPage, used.LastPage));
        Assert.StartsWith("Saved. In Harbor night, this item now opens at page ", used.Message, StringComparison.Ordinal);
        Assert.Equal((0, 1), (await ReaderApi.OpenAsync(library, new EntryId(card.EntryId), null, null, null, false, Ct, pages.Id))!.Item is { } moved
            ? (moved.FirstPage, moved.LastPage) : default);
        Assert.Null(await BindersApi.UsePageAsync(library, pages.Id, new ReaderUsePage(document, 999), Ct));
    }

    [Fact]
    public async Task A_picture_opens_in_the_picture_reader_and_a_pack_steps_through_its_files()
    {
        Fixture.Graph.Add("RPG/known.pdf", await File.ReadAllBytesAsync(pdfs.KnownText, Ct));
        Fixture.Graph.Add("RPG/map.png", Png(1200, 800));
        var (userId, _) = await Fixture.UserWithDriveAsync();
        await using var lease = await Fixture.Libraries.OpenAsync(userId, Ct);
        var library = lease.Library;
        var root = await library.Get<SourceRootStore>().AddAsync(DrivePath.Of(FakeGraph.DriveId, "RPG"), Ct);
        library.Indexing.RequestScan(root.Id);
        var books = await WaitAsync(async () =>
        {
            var results = await LibraryApi.ResultsAsync(library, new LibraryQuery(), Ct);
            return results.Books.Count == 2 && results.Books.All(b => b.Cover is not null) ? results.Books : null;
        });
        var map = books.Single(b => b.Meta == "PNG image");
        var sheet = books.Single(b => b != map);

        var picture = (await ReaderApi.OpenAsync(library, new EntryId(map.EntryId), null, null, null, false, Ct))!;
        Assert.Equal((ReaderKinds.Image, "png"), (picture.Kind, picture.Format));
        Assert.StartsWith("PNG image", picture.Subtitle, StringComparison.Ordinal);
        Assert.Empty(picture.Pack);
        Assert.Equal(ReaderKinds.Pdf, (await ReaderApi.OpenAsync(library, new EntryId(sheet.EntryId), null, null, null, false, Ct))!.Kind);

        Assert.NotNull(await library.Get<PackService>().PackTogetherAsync([new EntryId(map.EntryId), new EntryId(sheet.EntryId)], "Harbor", Ct));
        var pack = (await LibraryApi.ResultsAsync(library, new LibraryQuery(), Ct)).Books.Single();
        var first = (await ReaderApi.OpenAsync(library, new EntryId(pack.EntryId), null, null, null, false, Ct))!;
        Assert.Equal(2, first.Pack.Count);
        Assert.Equal(first.Pack[0].DocumentId, first.DocumentId);
        Assert.Equal([true, false], first.Pack.Select(f => f.IsImage).OrderDescending());
        Assert.Equal(new[] { map.EntryId, sheet.EntryId }.Order(), first.Pack.Select(f => f.MemberEntryId!.Value).Order());
        // The next file opens from the same card, in the same pack, in its own reader.
        var next = first.Pack.Single(f => f.DocumentId != first.DocumentId);
        var stepped = (await ReaderApi.OpenAsync(library, new EntryId(pack.EntryId), next.DocumentId, null, null, false, Ct))!;
        Assert.Equal((next.DocumentId, next.IsImage ? ReaderKinds.Image : ReaderKinds.Pdf, 2), (stepped.DocumentId, stepped.Kind, stepped.Pack.Count));
        Assert.Contains(" in Harbor", stepped.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chosen_cover_is_back_on_disk_before_the_library_opens_and_forgotten_once_removed()
    {
        var (userId, _) = await Fixture.UserWithDriveAsync();
        string kept, path;
        await using (var lease = await Fixture.Libraries.OpenAsync(userId, Ct))
        {
            var library = lease.Library;
            var (entry, _) = await library.Get<ElsewhereService>().AddAsync(ElsewhereText.Draft("Tomb of the Test", null, null, null, [], ""), Ct);
            var chosen = library.Get<ChosenCovers>();
            await chosen.KeepAsync(entry!.Value, chosen.Read(new MemoryStream(Png(60, 80))).Jpeg!, Ct);
            await WaitAsync(() => Task.FromResult(Chosen().Count == 1 ? "" : null));
            var first = Chosen()[0];
            // A cover chosen again replaces the one before, in the store too.
            await chosen.KeepAsync(entry.Value, chosen.Read(new MemoryStream(Png(80, 60))).Jpeg!, Ct);
            kept = await WaitAsync(() => Task.FromResult(Chosen() is [var only] && only != first ? only : null));
            path = library.Get<CoverCache>().PathFor(kept);
        }
        Assert.Equal(1, await Fixture.Libraries.CloseIdleAsync(TimeSpan.Zero));

        // A restart empties the disk; the databases come back, and the chosen cover with them, before anything reads it.
        File.Delete(path);
        await using (var lease = await Fixture.Libraries.OpenAsync(userId, Ct))
        {
            var library = lease.Library;
            var entry = (await library.Get<LibraryQueries>().ListAsync(new LibraryFilter(), ct: Ct)).Single().EntryId;
            Assert.True(File.Exists(path));
            Assert.True(await library.Get<ChosenCovers>().HasChosenAsync(entry, Ct));

            await library.Get<ChosenCovers>().RemoveAsync(entry, Ct);
            await WaitAsync(() => Task.FromResult(Chosen().Count == 0 ? "" : null));
        }

        List<string> Chosen() => [.. Fixture.Covers.Names.Where(n => n.StartsWith(userId + "/", StringComparison.Ordinal))
            .Select(n => n[(userId.Length + 1)..]).Where(CoverCache.IsChosen)];
    }

    [Fact]
    public void Skia_makes_a_jpeg_thumbnail_no_wider_than_asked()
    {
        var codec = new SkiaImageCodec();
        using var png = new MemoryStream(Png(1200, 800));

        Assert.Equal((1200, 800), codec.ReadSize(png));
        png.Position = 0;
        var jpeg = codec.Thumbnail(png, 360);

        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal((360, 240), (decoded.Width, decoded.Height));
        Assert.Null(codec.ReadSize(new MemoryStream([1, 2, 3])));
    }

    static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SeaGreen);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static async Task<T> WaitAsync<T>(Func<Task<T?>> done) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (await done() is { } result) return result;
            await Task.Delay(100, timeout.Token);
        }
    }
}
