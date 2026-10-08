using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

public sealed class SourceScannerTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bibliotaph-scan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Touch(string relative, int bytes = 10)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Lists_indexable_files_by_format_and_counts_the_rest()
    {
        Touch("Core/Rules.pdf", 100);
        Touch("Core/Maps/Keep.JPG");
        Touch("Handouts/letter.png");
        Touch("Handouts/notes.docx");
        Touch("readme.txt");

        var result = SourceScanner.Scan(_root, [], TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(
            [Path.Combine("Core", "Maps", "Keep.JPG"), Path.Combine("Core", "Rules.pdf"), Path.Combine("Handouts", "letter.png")],
            result.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(100, result.Files.Single(f => f.RelativePath.EndsWith(".pdf", StringComparison.Ordinal)).SizeBytes);
        Assert.Equal(new Dictionary<string, int> { [SourceFormats.Pdf] = 1, [SourceFormats.Jpeg] = 1, [SourceFormats.Png] = 1 }, result.Summary.ByFormat);
        Assert.Equal(2, result.Summary.Unsupported);
        Assert.Equal(3, result.Summary.Indexable);
        Assert.Equal(0, result.Summary.OnlineOnly);
    }

    [Fact]
    public void A_folder_that_is_its_own_library_root_is_left_to_its_own_scan()
    {
        Touch("Rules.pdf");
        Touch("Adventures/One.pdf");
        Touch("Adventures/Two.pdf");

        var result = SourceScanner.Scan(_root, [Path.Combine(_root, "Adventures") + Path.DirectorySeparatorChar], TestContext.Current.CancellationToken);

        Assert.Equal(["Rules.pdf"], result!.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void Linked_folders_are_not_followed()
    {
        Touch("Real/Book.pdf");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "Loop"), _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Creating a symbolic link needs Developer Mode or elevation here: {ex.Message}");
        }

        var result = SourceScanner.Scan(_root, [], TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine("Real", "Book.pdf")], result!.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_unreachable_rather_than_empty() =>
        Assert.Null(SourceScanner.Scan(Path.Combine(_root, "Unplugged drive"), [], TestContext.Current.CancellationToken));
}
