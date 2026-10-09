using System.IO.Compression;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Dapper;

namespace Bibliotaph.Processing.Tests;

/// <summary>ZIPs (F3): the PDFs and images inside a ZIP in a library folder are read in place, never unpacked beside it.</summary>
public sealed partial class PipelineTests
{
    void Zip(string relative, DateTime modifiedUtc, params (string Entry, byte[] Bytes)[] members)
    {
        var target = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            foreach (var (entry, bytes) in members)
            {
                using var stream = zip.CreateEntry(entry).Open();
                stream.Write(bytes);
            }
        File.SetLastWriteTimeUtc(target, modifiedUtc);
    }

    static readonly DateTime Saved = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Books_and_maps_inside_a_ZIP_are_read_without_unpacking_it()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        Zip("Bundles/Goblin Bundle.zip", Saved,
            ("Goblin Bundle/Known Text.pdf", File.ReadAllBytes(pdfs.KnownText)),
            ("Goblin Bundle/Maps/Harbor Map.png", FakeCodec.Png(800, 600)),
            ("Goblin Bundle/Extras.zip", [1, 2, 3]),
            ("Goblin Bundle/readme.txt", "not indexed"u8.ToArray()));
        Zip("Scans.zip", Saved, ("Goblin Scan.pdf", File.ReadAllBytes(pdfs.Scanned)));
        var before = Directory.GetFiles(_library, "*", SearchOption.AllDirectories).Order().ToArray();
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        var progress = await SettleAsync();

        // The book loose and zipped is one document; the map and the scan are the others.
        Assert.Equal(3, progress.Documents);
        await using var c = _index.OpenRead();
        var ids = (await c.QueryAsync<(string Title, long Id)>("SELECT display_title, document_id FROM doc")).ToDictionary(r => r.Title, r => r.Id);
        Assert.Equal(["Goblin Scan", "Harbor Map", "Known Text"], ids.Keys.Order());

        // The loose file is read in preference; the map is read from its ZIP, its path reading as Explorer shows it.
        Assert.False((await _libraryStore.GetSourceAsync(ids["Known Text"], Ct))!.InArchive);
        var map = (await _libraryStore.GetSourceAsync(ids["Harbor Map"], Ct))!;
        Assert.Equal(Path.Combine(_library, "Bundles", "Goblin Bundle.zip"), map.ArchivePath);
        Assert.Equal("Goblin Bundle/Maps/Harbor Map.png", map.EntryPath);
        Assert.Equal(Path.Combine(_library, "Bundles", "Goblin Bundle.zip", "Goblin Bundle", "Maps", "Harbor Map.png"), map.FullPath);
        Assert.Equal("Bundles / Goblin Bundle / Goblin Bundle / Maps", map.FolderHint);
        Assert.Equal((800L, 600L), c.QuerySingle<(long, long)>("SELECT width_px, height_px FROM doc WHERE display_title = 'Harbor Map'"));
        Assert.Equal("Complete", StatusOf(c, "Harbor Map", Stage.Covers));

        // The scan went through the PDF engine from the extract cache.
        Assert.Equal("Complete", StatusOf(c, "Goblin Scan", Stage.Text));
        Assert.Equal("Complete", StatusOf(c, "Goblin Scan", Stage.Covers));
        Assert.NotEmpty(Directory.GetFiles(_paths.Extract, "*.pdf"));

        // The ZIP inside the ZIP is listed with the reason; nothing was written beside the originals.
        var problem = Assert.Single(await _libraryStore.GetProblemsAsync(Ct));
        Assert.Equal(ArchiveReader.Nested, problem.Problem);
        Assert.EndsWith("Extras.zip", problem.FullPath, StringComparison.Ordinal);
        Assert.Equal(1, (await _libraryStore.GetCountsAsync(Ct)).Problems);
        Assert.Equal(before, Directory.GetFiles(_library, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task A_ZIP_saved_again_reads_only_what_changed_and_a_replaced_map_is_a_new_version()
    {
        byte[] mapA = FakeCodec.Png(100, 100), mapB = FakeCodec.Png(200, 200);
        Zip("Bundle.zip", Saved, ("Map A.png", mapA), ("Map B.png", mapB));
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var cards = (await search.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.Title);
        Assert.Equal(["Map A", "Map B"], cards.Keys.Order());

        // Saved again: Map B replaced, Map C added.
        File.Delete(Path.Combine(_library, "Bundle.zip"));
        Zip("Bundle.zip", Saved.AddDays(1), ("Map A.png", mapA), ("Map B.png", FakeCodec.Png(300, 300)), ("Map C.png", FakeCodec.Png(400, 400)));
        _service.RequestScan();
        await SettleUntilAsync(async () => (await _queries.GetProgressAsync(Ct)).Documents == 4);

        var after = (await search.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.Title);
        Assert.Equal(["Map A", "Map B", "Map C"], after.Keys.Order());
        Assert.Equal(cards["Map A"].DocumentId, after["Map A"].DocumentId);
        // The new Map B is its card's current version, with the old one as an earlier version.
        Assert.Equal(cards["Map B"].EntryId, after["Map B"].EntryId);
        Assert.NotEqual(cards["Map B"].DocumentId, after["Map B"].DocumentId);
        Assert.Equal(2, after["Map B"].Copies);
    }
}
