using System.Security.Cryptography;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;

namespace Bibliotaph.Processing;

/// <summary>What Check a download made of a file (F5 plan, choice 11), in the order the page groups them.</summary>
public enum CheckVerdict
{
    New,
    MaybeNewVersion,
    OwnedElsewhere,
    AnotherCopy,
    AlreadyOwned,
    Unreadable,
}

/// <summary>
/// A file to check: a PDF or image in the folder, or one inside a ZIP (<see cref="EntryPath"/> set, with
/// <see cref="FullPath"/> the ZIP's). <see cref="Problem"/> says why it can't be read, for a ZIP inside a ZIP and the like.
/// </summary>
public sealed record CheckItem(string FullPath, string? EntryPath = null, string? Problem = null)
{
    /// <summary>The file's name, without the folders or ZIP it is in.</summary>
    public string Name => Path.GetFileName(EntryPath ?? FullPath);

    /// <summary>Where it is, as File Explorer shows it: through the ZIP for a file inside one.</summary>
    public string Location => EntryPath is null ? FullPath : ArchivePaths.MemberPath(FullPath, EntryPath);
}

/// <summary>
/// One checked file. A match names the library card (<see cref="MatchEntryId"/>, <see cref="MatchTitle"/>) and the
/// document it opens, which a book owned elsewhere doesn't have; Already owned says where the owned file is.
/// </summary>
public sealed record CheckedFile(CheckItem Item, CheckVerdict Verdict, string? Note = null)
{
    public EntryId? MatchEntryId { get; init; }

    public string? MatchTitle { get; init; }

    public long? MatchDocumentId { get; init; }

    public string? MatchWhere { get; init; }
}

/// <summary>
/// Check a download (F5 plan, choices 9 to 13): which files in a folder or ZIP the library already has, before anything
/// is copied anywhere. It reads through the read-only reader and writes only its own temporary copies of PDFs inside a
/// ZIP, in the extract cache, which go when the check ends. Nothing goes into the catalog, the index or the job queue.
/// The rules are Match's: the same hash, every page alike, most pages alike, or the title and publisher.
/// </summary>
public sealed class DownloadCheck(LibraryStore library, EntryStore entries, IndexQueries queries, PdfWorkerPool workers, ArchiveReader archives,
    ISourceFileReader reader, VocabularyStore vocabularies, AppPaths paths, IDiskSpace disk)
{
    /// <summary>Free space a PDF taken out of a ZIP must leave on the drive.</summary>
    public const long ReserveBytes = 1L * 1024 * 1024 * 1024;

    const string TempPrefix = "check-";

    /// <summary>
    /// The PDFs and images a check reads: in a folder and its subfolders, with those in its ZIPs, or in one ZIP. Other
    /// files are left out. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the folder
    /// can't be listed.
    /// </summary>
    public IReadOnlyList<CheckItem> List(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return ListArchive(path);
        var items = new List<CheckItem>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var file in Directory.EnumerateFiles(path, "*", options).Order(NaturalOrder.Instance))
        {
            ct.ThrowIfCancellationRequested();
            if (SourceFormats.IsArchive(file)) items.AddRange(ListArchive(file));
            else if (SourceFormats.FromFileName(file) is not null) items.Add(new CheckItem(file));
        }
        return items;
    }

    IReadOnlyList<CheckItem> ListArchive(string zip)
    {
        try
        {
            return [.. archives.List(zip).Select(m => new CheckItem(zip, m.EntryPath, m.Problem))];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return [new CheckItem(zip, null, "This ZIP couldn't be read; it may be damaged or still downloading.")];
        }
    }

    /// <summary>Starts a check of the files under <paramref name="root"/>. Dispose it to remove its temporary files.</summary>
    public async Task<DownloadCheckRun> StartAsync(string root, CancellationToken ct = default)
    {
        RemoveLeftovers();
        return new DownloadCheckRun(this, root, Path.Combine(paths.Extract, TempPrefix + Guid.NewGuid().ToString("N")), await vocabularies.GetAsync(ct));
    }

    /// <summary>The temporary folders of a check that didn't end cleanly (the app closed during it).</summary>
    void RemoveLeftovers()
    {
        if (!Directory.Exists(paths.Extract)) return;
        foreach (var folder in Directory.EnumerateDirectories(paths.Extract, TempPrefix + "*")) DownloadCheckRun.TryDelete(folder);
    }

    internal async Task<CheckedFile> CheckAsync(CheckItem item, string root, string temp, Vocabulary vocabulary, CancellationToken ct)
    {
        if (item.Problem is not null) return new CheckedFile(item, CheckVerdict.Unreadable, item.Problem);
        var pdf = SourceFormats.FromFileName(item.Name) == SourceFormats.Pdf;
        LocalFile? file = null;
        try
        {
            ContentHash hash;
            try
            {
                (hash, file) = await HashAsync(item, pdf, temp, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new CheckedFile(item, CheckVerdict.Unreadable, $"It couldn't be read: {ex.Message}");
            }

            if (await library.FindDocumentAsync(hash, ct) is { } owned)
            {
                var where = (await library.GetLocationsAsync(owned, ct)).FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
                return await MatchAsync(item, CheckVerdict.AlreadyOwned, owned, ct) with { MatchWhere = where };
            }
            // Images have no text to compare (choice 13).
            if (!pdf || file is null) return new CheckedFile(item, CheckVerdict.New);
            return await CompareAsync(item, file, root, vocabulary, ct);
        }
        finally
        {
            if (file is not null) await file.DisposeAsync();
        }
    }

    /// <summary>
    /// The file's hash and, for a PDF, a file the PDF engine can open: itself, or for one inside a ZIP a temporary copy
    /// written as it is hashed, so the ZIP is read once.
    /// </summary>
    async Task<(ContentHash Hash, LocalFile? File)> HashAsync(CheckItem item, bool pdf, string temp, CancellationToken ct)
    {
        if (item.EntryPath is null)
        {
            await using var stream = reader.OpenRead(item.FullPath);
            return (await FileHasher.HashAsync(stream, ct), pdf ? new LocalFile(item.FullPath, null) : null);
        }

        using var zip = archives.Open(item.FullPath);
        await using var input = ArchiveReader.OpenMember(zip, item.EntryPath);
        if (!pdf) return (await FileHasher.HashAsync(input, ct), null);

        Directory.CreateDirectory(temp);
        if (disk.FreeBytes(temp) is { } free && free - input.Length < ReserveBytes)
            throw new IOException("there isn't enough free disk space to read it from its ZIP.");
        var target = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            return (ContentHash.FromBytes(hash.GetHashAndReset()), new LocalFile(target, () => DownloadCheckRun.TryDeleteFile(target)));
        }
        catch
        {
            DownloadCheckRun.TryDeleteFile(target);
            throw;
        }
    }

    /// <summary>A PDF that isn't an exact match: its pages against the library's, then its title and publisher.</summary>
    async Task<CheckedFile> CompareAsync(CheckItem item, LocalFile file, string root, Vocabulary vocabulary, CancellationToken ct)
    {
        var (texts, info, problem) = await ReadAsync(file, ct);
        if (texts is not null)
        {
            var fingerprints = PageFingerprints.Compute(texts);
            var mine = fingerprints.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (mine.Count >= PageFingerprints.MinimumMatchingPages)
            {
                var sharing = await queries.GetSharingPagesAsync(mine, ct);
                foreach (var (other, shared, _) in sharing)
                {
                    if (shared < PageFingerprints.MinimumMatchingPages) break;
                    if (PageFingerprints.IsSameBook(fingerprints, await queries.GetFingerprintsAsync(other, ct)))
                        return await MatchAsync(item, CheckVerdict.AnotherCopy, other, ct);
                }
                var version = sharing
                    .Select(s => (s.DocumentId, s.Shared, Compared: Math.Min(mine.Count, s.Fingerprinted)))
                    .Where(s => s.Compared > 0 && s.Shared >= PageFingerprints.MinimumMatchingPages && s.Shared >= VersionStore.MinimumSharedPages * s.Compared)
                    .OrderByDescending(s => (double)s.Shared / s.Compared).ThenByDescending(s => s.Shared)
                    .Select(s => (long?)s.DocumentId).FirstOrDefault();
                if (version is { } newer)
                    return await MatchAsync(item, CheckVerdict.MaybeNewVersion, newer, ct, "Most of its pages are in it.");
            }
        }

        var relative = Path.GetRelativePath(root, item.Location);
        var hints = RuleHints.Propose(new HintSource(relative, info?.Title, info?.Author, info?.Subject, info?.Keywords), vocabulary);
        var title = Best(hints, MetadataFields.Title);
        var publisher = Best(hints, MetadataFields.Publisher);
        if (title is not null)
        {
            var matches = await queries.GetTitleMatchesAsync(title, ct);
            if (matches.FirstOrDefault(m => m.Kind == EntryKind.Elsewhere && Agree(m.Publisher, publisher)) is { } elsewhere)
                return await TitledAsync(item, CheckVerdict.OwnedElsewhere, elsewhere, problem, ct);
            if (publisher is not null && matches.FirstOrDefault(m => m.DocumentId is not null && m.Publisher is not null && Agree(m.Publisher, publisher)) is { } same)
                return await TitledAsync(item, CheckVerdict.MaybeNewVersion, same, problem ?? "It has its title and publisher.", ct);
        }
        return new CheckedFile(item, CheckVerdict.New, problem);
    }

    static string? Best(IReadOnlyList<MetadataProposal> hints, MetadataField field) =>
        hints.Where(h => h.Field == field).MaxBy(h => EffectiveMetadata.Priority(h.Origin))?.Value;

    /// <summary>Publishers that don't say the books differ: the same, or one of them unknown.</summary>
    static bool Agree(string? a, string? b) => a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every page's text and the PDF's own information; or why its pages couldn't be read, to say beside the verdict.</summary>
    async Task<(IReadOnlyList<string?>? Texts, DocMetadata? Info, string? Problem)> ReadAsync(LocalFile file, CancellationToken ct)
    {
        await using var session = new PdfSession(workers.Index, file, password: null);
        Response? error;
        try
        {
            error = await session.OpenAsync(ct);
        }
        catch (WorkerException)
        {
            return (null, null, "The PDF engine couldn't open it, so it was judged by its name.");
        }
        if (error is not null)
            return (null, null, error.Error == ErrorKind.Password
                ? "It needs a password, so it was judged by its name."
                : "It isn't a readable PDF, so it was judged by its name.");

        var texts = new List<string?>(session.Doc.PageCount);
        for (var first = 0; first < session.Doc.PageCount; first += TextStage.RunLength)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(TextStage.RunLength, session.Doc.PageCount - first);
            var pages = await TextStage.ExtractRunAsync(session, first, count, ct) ?? await TextStage.ExtractOneByOneAsync(session, first, count, ct);
            texts.AddRange(pages.Select(p => p.Error is null ? p.Text : null));
        }
        // Scanned pages have no text until OCR, which a check doesn't run: such a book is judged by its name.
        var problem = texts.Count(t => !string.IsNullOrWhiteSpace(t)) < PageFingerprints.MinimumMatchingPages
            ? "It has too little text to compare, so it was judged by its name."
            : null;
        return (texts, session.Doc.Metadata, problem);
    }

    /// <summary>The verdict, naming the card that shows <paramref name="documentId"/>, and the document it opens.</summary>
    async Task<CheckedFile> MatchAsync(CheckItem item, CheckVerdict verdict, long documentId, CancellationToken ct, string? note = null)
    {
        if (await entries.GetShownByAsync(documentId, ct) is not [var card, ..]) return new CheckedFile(item, verdict, note) { MatchDocumentId = documentId, MatchTitle = await queries.GetTitleAsync(documentId, ct) };
        var titles = await queries.GetEntryTitlesAsync([card.EntryId], ct);
        return new CheckedFile(item, verdict, note)
        {
            MatchEntryId = card.EntryId,
            MatchTitle = titles.GetValueOrDefault(card.EntryId) ?? await queries.GetTitleAsync(documentId, ct),
            MatchDocumentId = card.DocumentId ?? documentId,
        };
    }

    async Task<CheckedFile> TitledAsync(CheckItem item, CheckVerdict verdict, TitleMatch match, string? note, CancellationToken ct)
    {
        var titles = await queries.GetEntryTitlesAsync([match.EntryId], ct);
        return new CheckedFile(item, verdict, note)
        {
            MatchEntryId = match.EntryId,
            MatchTitle = titles.GetValueOrDefault(match.EntryId),
            MatchDocumentId = match.DocumentId,
        };
    }
}

/// <summary>A check in progress: its files are checked one at a time, and its temporary files go when it is disposed.</summary>
public sealed class DownloadCheckRun : IAsyncDisposable
{
    readonly DownloadCheck _check;
    readonly string _temp;
    readonly Vocabulary _vocabulary;

    internal DownloadCheckRun(DownloadCheck check, string root, string temp, Vocabulary vocabulary)
    {
        _check = check;
        Root = Directory.Exists(root) ? root : Path.GetDirectoryName(root) ?? root;
        _temp = temp;
        _vocabulary = vocabulary;
    }

    /// <summary>The folder the files' folder names are read from: the folder checked, or the one the ZIP is in.</summary>
    public string Root { get; }

    public Task<CheckedFile> CheckAsync(CheckItem item, CancellationToken ct = default) => _check.CheckAsync(item, Root, _temp, _vocabulary, ct);

    public ValueTask DisposeAsync()
    {
        TryDelete(_temp);
        return ValueTask.CompletedTask;
    }

    internal static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere; the next check removes it.
        }
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed with its folder when the check ends.
        }
    }
}
