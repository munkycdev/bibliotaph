using System.Buffers.Binary;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Pdf.Host.Tests;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// A library folder of synthetic files through the real pipeline: scan, hash, Probe, Text, Covers and OCR, with
/// the real PDF worker, catalog.db and index.db on disk. Only image encoding is faked.
/// </summary>
public sealed class PipelineTests(SyntheticPdfs pdfs) : IAsyncLifetime
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-pipeline-").FullName;
    AppPaths _paths = null!;
    string _library = null!;
    CatalogDatabase _catalog = null!;
    IndexDatabase _index = null!;
    IndexWriter _writer = null!;
    PdfWorkerPool _workers = null!;
    IndexQueries _queries = null!;
    SourceRootStore _roots = null!;
    LibraryStore _libraryStore = null!;
    IndexingService _service = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _paths = new AppPaths(Path.Combine(_dir, "data"));
        foreach (var directory in _paths.Directories) Directory.CreateDirectory(directory);
        _library = Path.Combine(_dir, "RPG Library");

        _catalog = new CatalogDatabase(_paths.CatalogDatabase, _paths.Backups);
        await _catalog.MigrateAsync(Ct);
        var contexts = new Factory(_catalog);
        _index = IndexDatabase.ForFile(_paths.IndexDatabase);
        await _index.InitializeAsync(Ct);
        _writer = new IndexWriter(_index);
        await _writer.StartAsync(Ct);

        _workers = new PdfWorkerPool(new PdfWorkerPoolOptions());
        _queries = new IndexQueries(_index);
        _roots = new SourceRootStore(contexts);
        var library = _libraryStore = new LibraryStore(contexts);
        var queue = new JobBoard(_writer, _index);
        var reader = new SourceFileReader();
        var services = new StageServices(library, new IndexStore(_writer), _queries, _workers, reader, new FakeCodec(), new CoverCache(_paths), new NoPasswords());
        _service = new IndexingService(_roots, library, queue,
            [new ProbeStage(services), new TextStage(services), new CoversStage(services), new OcrStage(services)],
            new FileHasher(reader), new DiskSpace(), new IndexingOptions { WatchFolders = false, IdleRecheck = TimeSpan.FromSeconds(1) });
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        await _writer.StopAsync(CancellationToken.None);
        _writer.Dispose();
        await _workers.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    void Copy(string source, string relative)
    {
        var target = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }

    /// <summary>Waits until nothing is waiting to be scanned, hashed or processed.</summary>
    async Task<IndexProgress> SettleAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (true)
        {
            await Task.Delay(200, timeout.Token);
            var summary = await _queries.GetQueueSummaryAsync(timeout.Token);
            var progress = await _queries.GetProgressAsync(timeout.Token);
            var counts = await _libraryStore.GetCountsAsync(timeout.Token);
            if (!_service.IsScanning && counts.Files > 0 && counts.Unhashed == 0 && summary.IsIdle && progress.Processing == 0) return progress;
        }
    }

    static string? StatusOf(SqliteConnection c, string title, Stage stage) => c.ExecuteScalar<string?>(
        "SELECT s.status FROM stage_status s JOIN doc d ON d.document_id = s.document_id WHERE d.display_title = @title AND s.stage = @stage",
        new { title, stage = stage.ToString() });

    [Fact]
    public async Task A_new_library_folder_becomes_searchable()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        Copy(pdfs.KnownText, "Backup/known text copy.pdf");
        Copy(pdfs.Scanned, "Scans/Goblin Scan.pdf");
        Copy(pdfs.PasswordProtected, "Locked Book.pdf");
        Copy(pdfs.Truncated, "Damaged Download.pdf");
        File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
        File.WriteAllText(Path.Combine(_library, "notes.txt"), "not indexed");
        await _roots.AddAsync(_library, Ct);

        await _service.StartAsync(Ct);
        var progress = await SettleAsync();

        // Five distinct contents; the copy is the same document.
        Assert.Equal(5, (await _queries.GetProgressAsync(Ct)).Documents);
        await using var c = _index.OpenRead();

        // Text: the phrase is found on its page, and the title comes from the file name.
        var (title, page) = c.QuerySingle<(string Title, long Page)>(
            "SELECT d.display_title, p.pdf_page FROM page_fts JOIN page p ON p.id = page_fts.rowid JOIN doc d ON d.document_id = p.document_id WHERE page_fts MATCH @q",
            new { q = "\"owlbear waits\"" });
        Assert.Equal(SyntheticPdfs.KnownPhrasePage, page);
        Assert.Contains(title, new[] { "Known Text", "Known Text Copy" });
        Assert.Equal(2, c.ExecuteScalar<long>("SELECT count(*) FROM outline JOIN doc USING (document_id) WHERE display_title = @title", new { title }));

        // The image-only page of the known text is flagged for OCR along with the scan.
        Assert.Equal("Complete", StatusOf(c, title, Stage.Text));
        Assert.Equal("Complete", StatusOf(c, title, Stage.Covers));
        Assert.True(File.Exists(Path.Combine(_paths.Cache, "covers", c.ExecuteScalar<string>("SELECT cover FROM doc WHERE display_title = @t", new { t = title })!)));

        // OCR: read where Windows OCR has a language; blocked with a reason everywhere else.
        var ocr = StatusOf(c, "Goblin Scan", Stage.Ocr);
        if (ocr == "Complete")
            Assert.Contains("goblin", c.ExecuteScalar<string>("SELECT p.text FROM page p JOIN doc d USING (document_id) WHERE d.display_title = 'Goblin Scan'"), StringComparison.OrdinalIgnoreCase);
        else
            Assert.Equal("Blocked", ocr);

        // The image: probed for its size, no text stage, a cover from the image itself.
        Assert.Equal((640L, 480L), c.QuerySingle<(long, long)>("SELECT width_px, height_px FROM doc WHERE display_title = 'Handout Map'"));
        Assert.Equal("Skipped", StatusOf(c, "Handout Map", Stage.Text));
        Assert.Equal("Complete", StatusOf(c, "Handout Map", Stage.Covers));

        // Files needing attention: the password and the damaged file, each with a reason a person can act on.
        var attention = await _queries.GetAttentionAsync(Ct);
        var locked = attention.Single(a => a.Stage == Stage.Probe && a.Status == StageStatus.Blocked);
        Assert.Contains("Password", locked.Reason, StringComparison.Ordinal);
        Assert.Contains(attention, a => a.Stage == Stage.Probe && a.Status == StageStatus.Failed);
        Assert.Equal(progress.NeedAttention, attention.Select(a => a.DocumentId).Distinct().Count());
    }

    [Fact]
    public async Task A_paused_lane_does_nothing_until_it_resumes()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        await _roots.AddAsync(_library, Ct);
        _service.Pause(Lane.Index);

        await _service.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(0, (await _queries.GetProgressAsync(Ct)).Documents);

        _service.Resume(Lane.Index);
        var progress = await SettleAsync();

        Assert.Equal(1, progress.Searchable);
    }

    [Fact]
    public async Task A_document_finished_before_a_restart_is_not_processed_again()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        await _service.StopAsync(Ct);
        var before = await JobsAsync();

        await _service.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await SettleAsync();

        Assert.Equal(before, await JobsAsync());
    }

    /// <summary>How many jobs there are and how many times they have run.</summary>
    async Task<(long Jobs, long Attempts)> JobsAsync()
    {
        await using var c = _index.OpenRead();
        return await c.QuerySingleAsync<(long, long)>("SELECT count(*), coalesce(sum(attempts), 0) FROM job");
    }

    sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
    }

    /// <summary>Encodes nothing: a "JPEG" is a marker plus the size, and PNG sizes come from the header.</summary>
    sealed class FakeCodec : IImageCodec
    {
        public byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride) => [0xFF, 0xD8, (byte)width, (byte)height];

        public (int Width, int Height)? ReadSize(Stream image)
        {
            var header = new byte[24];
            if (image.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24 || header[1] != (byte)'P') return null;
            return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
        }

        public byte[]? Thumbnail(Stream image, int maxWidth) => ReadSize(image) is null ? null : [0xFF, 0xD8];

        /// <summary>A PNG signature and IHDR: enough for anything that only reads the size.</summary>
        public static byte[] Png(int width, int height)
        {
            var png = new byte[33];
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
            return png;
        }
    }
}
