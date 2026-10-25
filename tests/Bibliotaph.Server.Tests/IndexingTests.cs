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
