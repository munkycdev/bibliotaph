using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>What a folder holds, for the Add folder preview and the processing summary.</summary>
public sealed record ScanSummary
{
    /// <summary>Indexable files by format (pdf, epub, jpg, png, webp).</summary>
    public required IReadOnlyDictionary<string, int> ByFormat { get; init; }
    public int Unsupported { get; init; }

    /// <summary>ZIPs, whose PDFs and images are read from inside them. What they hold isn't known until then.</summary>
    public int Archives { get; init; }

    public int OnlineOnly { get; init; }

    /// <summary>Bytes that will download as online-only files are indexed.</summary>
    public long OnlineOnlyBytes { get; init; }

    /// <summary>Folders that could not be read, relative to the root.</summary>
    public IReadOnlyList<string> Inaccessible { get; init; } = [];

    public int Indexable => ByFormat.Values.Sum();
}

public sealed record ScanResult(IReadOnlyList<ScannedFile> Files, ScanSummary Summary);

/// <summary>
/// Which files in a library folder Bibliotaph reads, from an <see cref="ISourceLister"/>'s listing of it: indexable
/// files and ZIPs are kept, anything else is only counted.
/// </summary>
public static class SourceScanner
{
    /// <returns>What the folder holds, or null when the root itself can't be reached (offline drive, unplugged disk).</returns>
    public static async Task<ScanResult?> ScanAsync(ISourceLister lister, string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct = default) =>
        await lister.ListAsync(root, otherRoots, ct) is { } listing ? Classify(listing) : null;

    public static ScanResult Classify(SourceListing listing)
    {
        var files = new List<ScannedFile>();
        var byFormat = new Dictionary<string, int>();
        int unsupported = 0, archives = 0, onlineOnly = 0;
        long onlineOnlyBytes = 0;
        foreach (var file in listing.Files)
        {
            var name = Path.GetFileName(file.RelativePath);
            var format = SourceFormats.FromFileName(name);
            if (format is null && !SourceFormats.IsArchive(name))
            {
                unsupported++;
                continue;
            }
            files.Add(file);
            if (format is null) archives++;
            else byFormat[format] = byFormat.GetValueOrDefault(format) + 1;
            if (file.OnlineOnly)
            {
                onlineOnly++;
                onlineOnlyBytes += file.SizeBytes;
            }
        }

        return new ScanResult(files, new ScanSummary
        {
            ByFormat = byFormat,
            Unsupported = unsupported,
            Archives = archives,
            OnlineOnly = onlineOnly,
            OnlineOnlyBytes = onlineOnlyBytes,
            Inaccessible = listing.Inaccessible,
        });
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or a folder or file inside it, as Windows compares paths.</summary>
    internal static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
}
