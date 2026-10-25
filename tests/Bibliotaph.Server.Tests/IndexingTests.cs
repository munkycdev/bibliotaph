using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Catalog;
using Bibliotaph.Server.Indexing;
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
