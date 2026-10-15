using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>What importing a book from an online library will do.</summary>
public enum OnlineOutcome
{
    /// <summary>A new book owned elsewhere.</summary>
    New,

    /// <summary>A card with its title is already in the library: it gets the place and the link instead of a second card.</summary>
    AddToCard,

    /// <summary>Imported before (the same link): its empty fields are filled, and nothing the user set changes.</summary>
    Update,

    /// <summary>Imported before, and there is nothing to fill.</summary>
    Unchanged,
}

/// <summary>
/// One book in an import's preview: what it becomes, the card it meets (with that card's title) when there is one, and
/// for an update the fields it fills.
/// </summary>
public sealed record OnlineImportRow(OnlineBook Book, OnlineOutcome Outcome, EntryId? EntryId = null, string? CardTitle = null)
{
    public IReadOnlyList<MetadataField> Fills { get; init; } = [];
}

/// <summary>A book imported before from this service that the library page doesn't list any more.</summary>
public sealed record MissingBook(EntryId EntryId, string Title, string Link);

/// <summary>
/// An import's preview: every book read and what it becomes, how many books shared with the user were left out, and
/// the books from earlier imports that are missing, listed only when the page is the whole library.
/// </summary>
public sealed record OnlineImportPlan(IOnlineLibraryReader Reader, IReadOnlyList<OnlineImportRow> Rows, int Shared, IReadOnlyList<MissingBook> Missing)
{
    /// <summary>Where a saved page's images are, to find covers in; null for a page read in the in-app browser.</summary>
    public string? SavedImages { get; init; }
}

/// <summary>What an import did: how many books it added and how many cards it changed, and what Undo needs.</summary>
public sealed record OnlineImportResult(int Added, int Changed, ImportReceipt Undo);

/// <summary>A saved library page read by one of the readers, with the folder its images were saved in.</summary>
public sealed record SavedLibraryPage(IOnlineLibraryReader Reader, OnlinePage Page, string Images);

/// <summary>
/// Importing the books the user owns on an online service as books owned elsewhere. A reader turns a library page
/// into books; the preview says what each becomes; the import writes them all in one transaction, and Undo takes the
/// whole import back. Every value is the user's own, with the import as its evidence, and the link goes in Also own,
/// where Open finds it and the next import knows the book by it, or for a store whose books are files to download,
/// in Get it from.
/// </summary>
public sealed class OnlineImports(MetadataStore store, MetadataService metadata, EntryStore entries,
    MetadataProjector projector, IndexQueries queries, ISourceFileReader files, IImageCodec images, CoverCache covers, TimeProvider? clock = null,
    ILogger<OnlineImports>? log = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<OnlineImports>.Instance;

    /// <summary>A cover bigger than this isn't one.</summary>
    const int MaxCoverBytes = 8 * 1024 * 1024;

    /// <summary>The services a library page can be read from, in the order the Online libraries page lists them.</summary>
    public static IReadOnlyList<IOnlineLibraryReader> Services { get; } =
        [new DndBeyondReader(), new DemiplaneReader(), new HumbleBundleReader(), new DriveThruRpgReader()];

    /// <inheritdoc cref="Services"/>
#pragma warning disable CA1822 // The app reaches the services through the imports it is given.
    public IReadOnlyList<IOnlineLibraryReader> Readers => Services;
#pragma warning restore CA1822

    /// <summary>
    /// A library page saved with Save as, Webpage complete, read by whichever reader recognises it, or null when none
    /// does. Its images are in the folder the browser saved beside it, "… _files".
    /// </summary>
    public async Task<SavedLibraryPage?> ReadSavedPageAsync(string path, CancellationToken ct = default)
    {
        string html;
        await using (var stream = files.OpenRead(path))
        using (var reader = new StreamReader(stream))
            html = await reader.ReadToEndAsync(ct);
        var images = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "_files");
        foreach (var candidate in Readers)
            if (candidate.Read(html) is { } page) return new SavedLibraryPage(candidate, page, images);
        return null;
    }

    /// <summary>
    /// The preview: each book on the page and what importing it does. A book whose link is already on a card was
    /// imported before; else a card with its title, and no publisher that says otherwise, gets it; else it is new.
    /// </summary>
    public async Task<OnlineImportPlan> PlanAsync(IOnlineLibraryReader reader, OnlinePage page, CancellationToken ct = default)
    {
        var linked = (await queries.GetLinkedCardsAsync(reader.LinkField.Key, reader.Key, ct))
            .GroupBy(c => LinkKey(c.Link)).ToDictionary(g => g.Key, g => g.First());
        var byTitle = (await queries.GetBookCardsAsync(ct)).ToLookup(c => TitleKey(c.Title));
        var known = await metadata.GetManyAsync([.. linked.Values.Select(c => c.EntryId)], ct);

        var rows = new List<OnlineImportRow>();
        var read = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in page.Books)
        {
            if (!read.Add(LinkKey(book.Link))) continue;
            if (linked.TryGetValue(LinkKey(book.Link), out var card))
            {
                var fills = Fillable(book, known.Metadata[card.EntryId], known.Vocabulary).Select(f => f.Field).Distinct().ToList();
                rows.Add(new OnlineImportRow(book, fills.Count > 0 ? OnlineOutcome.Update : OnlineOutcome.Unchanged, card.EntryId, card.Title) { Fills = fills });
                continue;
            }
            var match = byTitle[TitleKey(book.Title)]
                .FirstOrDefault(c => c.Publisher is null || book.Publisher is null || MetadataText.Normalize(c.Publisher) == MetadataText.Normalize(book.Publisher));
            rows.Add(match.Title is null ? new OnlineImportRow(book, OnlineOutcome.New) : new OnlineImportRow(book, OnlineOutcome.AddToCard, match.EntryId, match.Title));
        }

        // Only a page that is the whole library can say a book has gone, and only a book owned elsewhere is offered for
        // removal: a file's card stays whatever the service says.
        IReadOnlyList<MissingBook> missing = page.Complete
            ? [.. linked.Values.Where(c => c.Elsewhere && !read.Contains(LinkKey(c.Link)) && OnSite(c.Link, reader.LibraryPage))
                .Select(c => new MissingBook(c.EntryId, c.Title, c.Link))]
            : [];
        return new OnlineImportPlan(reader, rows, page.Shared, missing);
    }

    /// <summary>
    /// Imports the chosen rows of a preview, in one transaction. New books get every value the page gave, and covers
    /// from <paramref name="cover"/> (the page's own images, never fetched); cards already here get only the link, and
    /// books imported before only their empty fields.
    /// </summary>
    public async Task<OnlineImportResult> ImportAsync(OnlineImportPlan plan, IReadOnlyCollection<OnlineImportRow> chosen,
        Func<string, CancellationToken, Task<byte[]?>>? cover = null, CancellationToken ct = default)
    {
        var reader = plan.Reader;
        var rows = chosen.Where(r => r.Outcome != OnlineOutcome.Unchanged).ToList();
        var known = await metadata.GetManyAsync([.. rows.Where(r => r.EntryId is not null).Select(r => r.EntryId!.Value)], ct);
        var vocabulary = known.Vocabulary;
        var books = new List<ImportedBook>();
        var newRows = new List<OnlineImportRow>();
        var addedTerms = false;
        foreach (var row in rows)
        {
            IEnumerable<(MetadataField Field, string Typed)> values = row.Outcome switch
            {
                OnlineOutcome.New => [(MetadataFields.Title, row.Book.Title), .. Values(row.Book, vocabulary), (reader.LinkField, $"{reader.Name} {row.Book.Link}")],
                OnlineOutcome.AddToCard => [(reader.LinkField, $"{reader.Name} {row.Book.Link}")],
                _ => Fillable(row.Book, known.Metadata[row.EntryId!.Value], vocabulary).Where(v => row.Fills.Contains(v.Field)),
            };
            var changes = new List<BulkChange>();
            foreach (var (field, typed) in values)
            {
                var (stored, problem, added) = await metadata.StoredAsync(field, typed, ct);
                if (problem is not null)
                {
                    _log.LogInformation("Left {Field} out of a book imported from {Service}: {Problem}", field.Key, reader.Name, problem.Message);
                    continue;
                }
                addedTerms |= added;
                changes.AddRange(stored.Select(v => new BulkChange(field, field.Multiple ? BulkAction.Add : BulkAction.Set, v)));
            }
            // A card that says it is owned there, with no link, gets the link in place of the bare place.
            if (row.EntryId is { } entryId && known.Metadata.TryGetValue(entryId, out var current))
                foreach (var linked in changes.Where(c => c.Field == reader.LinkField).Select(c => StoreLink.Parse(c.Value!)).ToList())
                    if (current[reader.LinkField].Values.Any(v => v.Value == linked.Store))
                        changes.Add(new BulkChange(reader.LinkField, BulkAction.Remove, linked.Store));
            if (changes.Count == 0) continue;
            books.Add(new ImportedBook(row.EntryId, changes));
            if (row.EntryId is null) newRows.Add(row);
        }

        var quote = string.Create(CultureInfo.CurrentCulture, $"Imported from your {reader.Name} library on {_clock.GetLocalNow():d MMM yyyy}");
        var receipt = await store.ImportAsync(books, quote, ct);
        _log.LogInformation("Imported {Added} books from {Service} and changed {Changed} fields on cards already there", receipt.Added.Count, reader.Name,
            receipt.Changed.Count);

        // Covers: the new books', and those of books imported before that have none yet, as after a cache was cleared.
        var wanted = receipt.Added.Zip(newRows, (a, r) => (a.SyncId, r.Book.CoverUrl)).ToList();
        var again = rows.Where(r => r.Outcome == OnlineOutcome.Update && r.Book.CoverUrl is not null).ToDictionary(r => r.EntryId!.Value, r => r.Book.CoverUrl);
        if (again.Count > 0)
            wanted.AddRange((await entries.GetCurrentAsync([.. again.Keys], ct)).Where(e => e.SyncId is { } id && covers.FindElsewhere(id) is null)
                .Select(e => (e.SyncId!.Value, again[e.EntryId])));
        if (cover is not null)
            foreach (var (syncId, url) in wanted)
                if (url is not null) await KeepCoverAsync(syncId, url, cover, ct);

        if (addedTerms) await projector.ProjectVocabularyAsync(ct);
        await projector.ProjectAsync([.. receipt.Added.Select(a => a.EntryId), .. receipt.Changed.Select(c => c.EntryId).Distinct()], ct);
        return new OnlineImportResult(receipt.Added.Count, receipt.Changed.Select(c => c.EntryId).Distinct().Count(), receipt);
    }

    /// <summary>Undoes an import: the books it added go, with their covers, and the cards it changed are as they were.</summary>
    public async Task UndoAsync(ImportReceipt receipt, CancellationToken ct = default)
    {
        foreach (var (entryId, syncId) in receipt.Added)
        {
            await entries.RemoveElsewhereAsync(entryId, ct);
            covers.Delete(CoverCache.ElsewhereKey(syncId));
        }
        await store.RestoreAsync(receipt.Changed, ct);
        await projector.ProjectAsync([.. receipt.Added.Select(a => a.EntryId), .. receipt.Changed.Select(c => c.EntryId).Distinct()], ct);
    }

    /// <summary>
    /// The covers a saved page's images hold: each cover is looked up by its file name in the folder the browser saved
    /// the page's images in, whether the page gives it as a web address or as the saved file ("./… _files/cover.jpg").
    /// Nothing is fetched.
    /// </summary>
    public Func<string, CancellationToken, Task<byte[]?>> SavedCovers(string folder) => async (url, ct) =>
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsolutePath : url.Split('?', '#')[0];
        var name = Uri.UnescapeDataString(path[(path.LastIndexOfAny(['/', '\\']) + 1)..]);
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var file = Path.Combine(folder, name);
        if (!File.Exists(file) || new FileInfo(file).Length > MaxCoverBytes) return null;
        await using var stream = files.OpenRead(file);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    };

    async Task KeepCoverAsync(Guid syncId, string url, Func<string, CancellationToken, Task<byte[]?>> cover, CancellationToken ct)
    {
        try
        {
            if (await cover(url, ct) is not { Length: > 0 and <= MaxCoverBytes } bytes) return;
            using var stream = new MemoryStream(bytes, writable: false);
            if (images.Thumbnail(stream, CoverCache.Width) is { } jpeg) covers.Write(CoverCache.ElsewhereKey(syncId), jpeg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation("A cover from an online library couldn't be kept: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The values the page gives for a book, as typed: its publisher, type, game system and edition, and year. A type or
    /// edition the vocabulary doesn't know is left out rather than made the user's own term: the service's word for it
    /// may not be one the user would choose.
    /// </summary>
    static IEnumerable<(MetadataField Field, string Typed)> Values(OnlineBook book, Vocabulary vocabulary)
    {
        if (book.Publisher is { } publisher) yield return (MetadataFields.Publisher, publisher);
        if (book.System is { } system && vocabulary.Resolve(MetadataFields.System.Vocabulary!, system) is not null) yield return (MetadataFields.System, system);
        if (book.Edition is { } edition && vocabulary.Resolve(MetadataFields.Edition.Vocabulary!, edition) is not null) yield return (MetadataFields.Edition, edition);
        if (book.Type is { } type && vocabulary.Resolve(MetadataFields.Types.Vocabulary!, type) is not null) yield return (MetadataFields.Types, type);
        if (book.Year is { } year) yield return (MetadataFields.Year, year.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The values a book imported before can still give its card: those of fields that have none.</summary>
    static IEnumerable<(MetadataField Field, string Typed)> Fillable(OnlineBook book, EffectiveMetadata current, Vocabulary vocabulary) =>
        Values(book, vocabulary).Where(v => current[v.Field].Values.Count == 0);

    /// <summary>A title as titles are matched: letters and digits only, so "Player's Handbook" meets "Players Handbook".</summary>
    internal static string TitleKey(string title) => MetadataText.Normalize(title).Replace(" ", "", StringComparison.Ordinal);

    /// <summary>
    /// A link as links are matched: its site without "www.", its path and any fragment, ignoring case, a query and a last
    /// slash. The fragment counts, as it is all that tells one Humble Bundle book's link from another's.
    /// </summary>
    internal static string LinkKey(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return link.Trim().ToLowerInvariant();
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return (host + uri.AbsolutePath.TrimEnd('/') + (uri.Fragment.Length > 1 ? uri.Fragment : "")).ToLowerInvariant();
    }

    static bool OnSite(string link, Uri site) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && LinkKey(uri.GetLeftPart(UriPartial.Authority)) == LinkKey(site.GetLeftPart(UriPartial.Authority));
}
