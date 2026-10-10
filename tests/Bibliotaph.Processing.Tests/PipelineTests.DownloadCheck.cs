using System.IO.Compression;

namespace Bibliotaph.Processing.Tests;

/// <summary>Check a download (F5b): a folder and its ZIPs checked against the library, which it leaves as it was.</summary>
public sealed partial class PipelineTests
{
    [Fact]
    public async Task Check_a_download_says_which_files_the_library_already_has()
    {
        Copy(pdfs.WatermarkedForAna, "Purchases/Drowned Abbey.pdf");
        var map = FakeCodec.Png(120, 80);
        Directory.CreateDirectory(Path.Combine(_library, "Maps"));
        File.WriteAllBytes(Path.Combine(_library, "Maps", "Harbor.png"), map);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        Assert.Null((await _elsewhere.AddAsync(new ElsewhereDraft("Goblin Warrens", Publisher: "Kobold Works", AlsoOwn: ["Print"]), Ct)).Problem);
        var documents = (await _libraryStore.GetCountsAsync(Ct)).Documents;

        var download = Path.Combine(_dir, "Downloads", "Bundle");
        Directory.CreateDirectory(Path.Combine(download, "Extras"));
        File.Copy(pdfs.WatermarkedForAna, Path.Combine(download, "Drowned Abbey.pdf"));
        File.Copy(pdfs.WatermarkedForDale, Path.Combine(download, "Abbey for Dale.pdf"));
        File.Copy(pdfs.RevisedForAna, Path.Combine(download, "Extras", "Abbey second printing.pdf"));
        File.Copy(pdfs.Injected, Path.Combine(download, "Goblin Warrens.pdf"));
        File.WriteAllBytes(Path.Combine(download, "Harbor copy.png"), map);
        File.WriteAllBytes(Path.Combine(download, "Island.png"), FakeCodec.Png(64, 64));
        File.WriteAllText(Path.Combine(download, "readme.txt"), "Not a book.");
        using (var zip = ZipFile.Open(Path.Combine(download, "Extras.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(pdfs.WatermarkedForDale, "Books/Abbey again.pdf");
            zip.CreateEntryFromFile(pdfs.Scanned, "Books/Fresh Scans.pdf");
            zip.CreateEntry("Inner.zip").Open().Dispose();
        }

        var check = new DownloadCheck(_libraryStore, _entries, _queries, _workers, new ArchiveReader(new SourceFileReader()), new SourceFileReader(),
            _vocabulary, _paths, new DiskSpace());
        var items = check.List(download, Ct);
        Assert.Equal(9, items.Count); // the text file is left out
        var results = new Dictionary<string, CheckedFile>();
        await using (var run = await check.StartAsync(download, Ct))
            foreach (var item in items) results[item.Name] = await run.CheckAsync(item, Ct);

        Assert.Equal(CheckVerdict.AlreadyOwned, results["Drowned Abbey.pdf"].Verdict);
        Assert.EndsWith(Path.Combine("Purchases", "Drowned Abbey.pdf"), results["Drowned Abbey.pdf"].MatchWhere, StringComparison.Ordinal);
        Assert.Equal(CheckVerdict.AlreadyOwned, results["Harbor copy.png"].Verdict);
        Assert.Equal(CheckVerdict.New, results["Island.png"].Verdict);

        var copy = results["Abbey for Dale.pdf"];
        Assert.Equal((CheckVerdict.AnotherCopy, "Drowned Abbey"), (copy.Verdict, copy.MatchTitle));
        Assert.NotNull(copy.MatchDocumentId);
        Assert.Equal(CheckVerdict.AnotherCopy, results["Abbey again.pdf"].Verdict);
        Assert.EndsWith(Path.Combine("Extras.zip", "Books", "Abbey again.pdf"), results["Abbey again.pdf"].Item.Location, StringComparison.Ordinal);
        Assert.Equal((CheckVerdict.MaybeNewVersion, "Drowned Abbey"), (results["Abbey second printing.pdf"].Verdict, results["Abbey second printing.pdf"].MatchTitle));

        var elsewhere = results["Goblin Warrens.pdf"];
        Assert.Equal((CheckVerdict.OwnedElsewhere, "Goblin Warrens", null), (elsewhere.Verdict, elsewhere.MatchTitle, elsewhere.MatchDocumentId));

        var scans = results["Fresh Scans.pdf"];
        Assert.Equal(CheckVerdict.New, scans.Verdict);
        Assert.Contains("too little text", scans.Note, StringComparison.Ordinal);
        Assert.Equal(CheckVerdict.Unreadable, results["Inner.zip"].Verdict);

        // Nothing was added, and the copies taken out of the ZIP are gone.
        Assert.Equal(documents, (await _libraryStore.GetCountsAsync(Ct)).Documents);
        Assert.Empty(Directory.EnumerateDirectories(_paths.Extract, "check-*"));
    }
}
