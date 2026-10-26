using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.OneDrive;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Api;

/// <summary>
/// The reader: a book to open, as the Windows reader opens it, and a short-lived link to its file in the user's
/// storage, which the browser reads byte ranges from itself. A book's bytes never pass through the server.
/// </summary>
public static partial class ReaderApi
{
    /// <summary>Find stops counting here, as the Windows reader's does.</summary>
    const int MaxMatches = 2000;

    public static void MapReaderApi(this RouteGroupBuilder api)
    {
        // An ordinary open goes back to the page the book was left at; a search hit, a page asked for or an adventure
        // opens where it says and keeps nothing. A reader popped out goes on keeping its place (keep).
        api.MapGet("/library/books/{entryId:long}/reader", async (long entryId, long? doc, int? page, string? q, bool? keep,
            ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await OpenAsync(lease.Library, new EntryId(entryId), doc, page, q, keep == true, ct) is { } book ? Results.Ok(book) : Results.NotFound();
        });

        var document = api.MapGroup("/library/documents/{documentId:long}");

        // Asked for when the book opens and again whenever the link has expired, which takes about an hour.
        document.MapGet("/link", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await LinkAsync(lease.Library, documentId, ct);
        });

        document.MapGet("/find", async (long documentId, string q, int? from, int? to, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var texts = await lease.Library.Get<IndexQueries>().GetPageTextsAsync(documentId, ct);
            return new ReaderFind(Find(texts.Select(t => (t.PdfPage, t.Text)), q, from, to));
        });

        // An adventure's cover is its first page, so only a whole PDF can have another.
        document.MapPut("/cover-page", async (long documentId, ReaderCover cover, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await UseCoverAsync(lease.Library, documentId, cover.Page, ct) is { } used ? Results.Ok(used) : Results.NotFound();
        });

        // Mark as adventure and Index as its own adventure…: pages typed as the book numbers them.
        document.MapPost("/parts", async (long documentId, PartEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Results.Ok(await MarkAsync(lease.Library, documentId, edit, ct));
        });

        document.MapGet("/bookmark", async (long documentId, int page, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return new ReaderBookmark(await lease.Library.Get<SessionsService>().SuggestLabelAsync(documentId, page, ct));
        });

        document.MapPut("/position", async (long documentId, ReaderPosition position, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<ReadingService>().SavePositionAsync(documentId, Math.Max(0, position.Page), ct);
            return Results.NoContent();
        });
    }

    internal static async Task<ReaderBook?> OpenAsync(UserLibrary library, EntryId entryId, long? asked, int? page, string? q, bool keep,
        CancellationToken ct)
    {
        var queries = library.Get<LibraryQueries>();
        if (await queries.GetDetailsAsync(entryId, ct) is not { } details) return null;
        var entry = details.Entry;
        var favorite = await library.Get<FavoritesService>().IsFavoriteAsync(entryId, ct);
        var marks = q is { Length: > 0 } ? SearchQuery.Parse(q).HighlightTerms() : [];

        if (entry.IsElsewhere)
            return Unreadable(entry, favorite, new ReaderProblem("Nothing to open here.", "This book is owned elsewhere, so Bibliotaph has no file of it to show."));

        // A pack opens at the file asked for, or its first; any other card at the file asked for, or its own.
        var pack = entry.IsPack ? await queries.GetPackImagesAsync(entryId, ct) : [];
        var documentId = asked is { } id && id > 0 ? id
            : entry.IsPack ? pack.Select(i => i.DocumentId).FirstOrDefault()
            : entry.DocumentId;
        if (documentId == 0 || await library.Get<LibraryStore>().GetSourceAsync(documentId, ct) is not { } source) return null;

        await using var db = library.Catalog.CreateContext();
        var size = await db.FileLocations.Where(l => l.DocumentId == documentId).Select(l => (long?)l.SizeBytes).FirstOrDefaultAsync(ct) ?? 0;
        var index = library.Get<IndexQueries>();
        var labels = await index.GetPageLabelsAsync(documentId, ct);
        var pageCount = await index.GetPageCountAsync(documentId, ct) ?? 0;
        var part = entry is { IsPart: true, Pages: { } pages } && entry.DocumentId == documentId ? new ReaderPart(pages.First, pages.Last) : null;
        var title = part is not null ? $"{entry.Title} · in {entry.ParentTitle ?? "its file"}" : entry.Title;
        var format = source.Format;
        var readerKind = SourceFormats.IsImage(format) ? ReaderKinds.Image : SourceFormats.IsEbook(format) ? ReaderKinds.Ebook : ReaderKinds.Pdf;
        // As the Windows reader's line: "PDF · 320 pages", "EPUB · 87 pages", "JPG image", and the pack it's in.
        var kind = readerKind == ReaderKinds.Image
            ? $"{CardText.FormatName(format)} image"
            : $"{CardText.FormatName(format)} · {pageCount.ToString("N0", CultureInfo.CurrentCulture)} {(pageCount == 1 ? "page" : "pages")}";
        if (entry.IsPack) kind = $"{kind} in {entry.PackTitle ?? entry.Title}";
        var subtitle = string.Join(" · ", new[] { kind, source.FolderHint }.Where(s => !string.IsNullOrEmpty(s)));

        var reading = library.Get<ReadingService>();
        var ordinary = page is null && string.IsNullOrWhiteSpace(q) && part is null;
        var kept = ordinary ? await reading.GetPositionAsync(documentId, ct) : 0;
        var start = Math.Clamp(page ?? part?.FirstPage ?? kept, 0, Math.Max(0, pageCount - 1));
        var notice = kept > 0 && start == kept ? $"Back at page {ReaderPageNumbers.Display(start, labels)}, where you left off." : null;
        // Home's Recently opened, as an open in the Windows app records it.
        await reading.RecordOpenAsync(documentId, ct);

        var problem = source.InArchive
            ? new ReaderProblem("This file is inside a ZIP.", "Bibliotaph on the web can't open a file inside a ZIP yet. The Windows app can.")
            : null;
        return new ReaderBook(entryId.Value, documentId, title, subtitle, format, source.ContentHash, size, labels, pageCount, start, part, marks,
            favorite, problem, notice)
        {
            Outline = await OutlineAsync(library, documentId, pageCount, labels, part, ct),
            KeepsPlace = ordinary || keep,
            Kind = readerKind,
            Pack = [.. pack.Select(p => new ReaderPackFile(p.DocumentId, p.Name, SourceFormats.IsImage(p.Format), p.MemberEntryId.Value))],
            CoverPage = readerKind == ReaderKinds.Pdf ? await library.Get<CoverPages>().GetAsync(documentId, ct) : 0,
        };
    }

    internal static async Task<ReaderCoverUsed?> UseCoverAsync(UserLibrary library, long documentId, int page, CancellationToken ct)
    {
        if (await library.Get<LibraryStore>().GetSourceAsync(documentId, ct) is not { Format: SourceFormats.Pdf }) return null;
        var pageCount = await library.Get<IndexQueries>().GetPageCountAsync(documentId, ct) ?? 0;
        page = page > 0 && page < pageCount ? page : 0;
        var drawn = await library.Get<CoverPages>().UseAsync(documentId, page, ct);
        return new ReaderCoverUsed(page, !drawn ? "The cover will change when the file is next read."
            : page == 0 ? "The first page is the cover again." : "This page is the cover now.");
    }

    internal static async Task<EditResult> MarkAsync(UserLibrary library, long documentId, PartEdit edit, CancellationToken ct)
    {
        var title = edit.Title?.Trim() ?? "";
        if (title.Length == 0) return new EditResult(null, "Give the adventure a title.");
        var index = library.Get<IndexQueries>();
        var labels = await index.GetPageLabelsAsync(documentId, ct);
        var pageCount = await index.GetPageCountAsync(documentId, ct) ?? 0;
        if (ReaderPageNumbers.Find(edit.From, labels, pageCount) is not { } first
            || (edit.To.Trim().Length == 0 ? first : ReaderPageNumbers.Find(edit.To, labels, pageCount)) is not { } last)
            return new EditResult(null, PagesHint);
        var added = await library.Get<PartsService>().AddAsync(documentId, new PartPages(Math.Min(first, last), Math.Max(first, last)), title, ct);
        return added.Problem is { } problem ? new EditResult(null, problem) : new EditResult(null, Message: $"“{title}” has a card of its own in the Library now.");
    }

    /// <summary>As the reader's page boxes say it.</summary>
    internal const string PagesHint = "Type pages as the book numbers them, or PDF page numbers.";

    /// <summary>
    /// The file's bookmarks, with what the AI found in each chapter under the bookmark the chapter starts at. A file with
    /// no bookmarks lists the scanned chapters themselves, as the Windows reader does.
    /// </summary>
    static async Task<IReadOnlyList<ReaderOutline>> OutlineAsync(UserLibrary library, long documentId, int pageCount, IReadOnlyList<string?> labels,
        ReaderPart? part, CancellationToken ct)
    {
        var chapters = await library.Get<ChapterQueries>().GetChaptersAsync(documentId, ct);
        var found = chapters.Where(c => c.Tags.Count > 0)
            .GroupBy(c => c.FirstPage)
            .ToDictionary(g => g.Key, g => "Found inside: " + string.Join(" · ", ChapterFields.Describe(g.First().Tags)));
        // Index as its own adventure… runs a bookmark's pages to the next at its depth or above, as the Windows reader
        // does, over every bookmark the file has, shown or not.
        var bookmarks = await library.Get<IndexQueries>().GetOutlineAsync(documentId, ct);
        var starts = bookmarks.Select(o => (o.PdfPage, Math.Min(o.Depth, 5))).ToList();
        var outline = bookmarks
            .Select((o, i) => (o, i))
            .Where(p => p.o.PdfPage >= 0 && p.o.PdfPage < pageCount && !string.IsNullOrWhiteSpace(p.o.Title))
            .Select(p => new ReaderOutline(p.o.Title.Trim(), p.o.PdfPage, Math.Min(p.o.Depth, 5), ReaderPageNumbers.Display(p.o.PdfPage, labels),
                part?.Contains(p.o.PdfPage) == true) { LastPage = PartRanges.FromBookmark(starts, p.i, pageCount).Last })
            .ToList();
        if (outline.Count == 0)
            return [.. chapters.Where(c => c.FirstPage < pageCount).Select(c => new ReaderOutline(c.Title, c.FirstPage, 0,
                ReaderPageNumbers.Display(c.FirstPage, labels), part?.Contains(c.FirstPage) == true, found.GetValueOrDefault(c.FirstPage))
                { LastPage = Math.Clamp(c.LastPage, c.FirstPage, pageCount - 1) })];
        // The shallowest bookmark at a chapter's first page is the chapter's own.
        var chapterStarts = outline.Select((o, i) => (o, i)).Where(p => found.ContainsKey(p.o.Page))
            .GroupBy(p => p.o.Page).Select(g => g.MinBy(p => p.o.Depth).i).ToHashSet();
        return [.. outline.Select((o, i) => chapterStarts.Contains(i) ? o with { Found = found[o.Page] } : o)];
    }

    static ReaderBook Unreadable(LibraryEntry entry, bool favorite, ReaderProblem problem) =>
        new(entry.EntryId.Value, 0, entry.Title, "", "", "", 0, [], 0, 0, null, [], favorite, problem);

    internal static async Task<ReaderLink> LinkAsync(UserLibrary library, long documentId, CancellationToken ct)
    {
        if (await library.Get<LibraryStore>().GetSourceAsync(documentId, ct) is not { } source)
            return new ReaderLink(null, new ReaderProblem("This book isn't in your library any more.", "It may have been removed since the page opened."));
        foreach (var path in new[] { source.FullPath }.Concat(source.AllPaths).Distinct())
        {
            if (!DrivePath.TryParse(path, out var driveId, out var inDrive) || library.Drives.Find(driveId) is not { } client) continue;
            try
            {
                if (await client.DownloadLinkAsync(driveId, inDrive, ct) is { } url) return new ReaderLink(url);
            }
            catch (OneDriveException)
            {
                // Another copy may still be there.
            }
        }
        return new ReaderLink(null, new ReaderProblem("The file couldn't be found.",
            "It isn't where Bibliotaph last saw it in your storage, or the drive needs connecting again in Settings."));
    }

    /// <summary>
    /// Every page with a match of what was typed, ignoring case and how the text breaks across lines, in the pages
    /// given (an adventure's), up to <see cref="MaxMatches"/> in all.
    /// </summary>
    internal static IReadOnlyList<ReaderFindPage> Find(IEnumerable<(int Page, string Text)> pages, string query, int? from, int? to)
    {
        var words = Spaces().Split(query.Trim()).Where(w => w.Length > 0).Select(Regex.Escape).ToList();
        if (words.Count == 0) return [];
        var pattern = new Regex(string.Join(@"\s+", words), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        var found = new List<ReaderFindPage>();
        var total = 0;
        foreach (var (page, text) in pages.OrderBy(p => p.Page))
        {
            if (page < (from ?? 0) || page > (to ?? int.MaxValue)) continue;
            var count = Math.Min(pattern.Count(text), MaxMatches - total);
            if (count == 0) continue;
            found.Add(new ReaderFindPage(page, count));
            total += count;
            if (total >= MaxMatches) break;
        }
        return found;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
