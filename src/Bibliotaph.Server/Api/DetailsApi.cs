using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;

namespace Bibliotaph.Server.Api;

/// <summary>/api/library/books/{entryId}: a book's details, as the Windows app's details drawer shows them.</summary>
public static class DetailsApi
{
    public static void MapDetailsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/library/books/{entryId:long}", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await DetailsAsync(lease.Library, new EntryId(entryId), ct) is { } details ? Results.Ok(details) : Results.NotFound();
        });

        // Scan again: forgets what the AI found in the book and reads it again, while scanning whole books is on.
        api.MapPost("/library/books/{entryId:long}/scan-again", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryQueries>().GetDetailsAsync(new EntryId(entryId), ct) is not { } details) return Results.NotFound();
            var chapters = library.Get<ChapterScanService>();
            await chapters.RescanAsync(details.Entry.DocumentId, ct);
            return Results.Ok(new EditResult(await DetailsAsync(library, details.Entry.EntryId, ct), Message: DetailsText.ScanAgain(chapters.IsOn)));
        });
    }

    /// <summary>
    /// Everything the details show, read as the Windows app's inspector reads it: the card, its fields and their
    /// sources, its adventures or pack files, what is known of its file, where it is, its copies and its processing.
    /// Null for a book that isn't in the library, or whose file hasn't been looked at yet.
    /// </summary>
    public static async Task<BookDetails?> DetailsAsync(UserLibrary library, EntryId entryId, CancellationToken ct)
    {
        var queries = library.Get<LibraryQueries>();
        if (await queries.GetDetailsAsync(entryId, ct) is not { } details) return null;
        var entry = details.Entry;
        var away = (await library.Get<LibraryStore>().GetUnavailableAsync(ct)).GetValueOrDefault(entryId);

        var metadata = (await library.Get<MetadataStore>().GetAsync(entryId, ct)).Compute();
        var vocabulary = await library.Get<VocabularyStore>().GetAsync(ct);
        var adventure = DetailsText.IsAdventure(entry, metadata);
        // A book owned elsewhere says first where it is owned.
        var fields = entry.IsElsewhere ? metadata.Fields.OrderBy(f => f.Field != MetadataFields.AlsoOwn) : metadata.Fields;

        IReadOnlyList<PackFile> packFiles = [];
        IReadOnlyList<DetailLocation> locations = [];
        IReadOnlyList<DetailCopy> copies = [];
        IReadOnlyList<DetailPart> parts = [];
        IReadOnlyList<string?> labels = [];
        var drives = library.Get<UserDrives>();
        string Where(string path) => FolderText.Location(path, drives);
        if (entry.IsPack)
        {
            var packs = library.Get<PackService>();
            packFiles = [.. (await queries.GetPackImagesAsync(entryId, ct)).Select(i =>
                new PackFile(i.MemberEntryId.Value, i.Name, i.Cover, i.Format == SourceFormats.Pdf, i.MemberEntryId == entry.CoverMember))];
            var pdfs = entry.PdfMembers > 0 || packFiles.Any(f => f.IsPdf);
            var place = await packs.GetPlaceAsync(entryId, ct);
            var folders = await packs.GetFoldersAsync(entryId, ct);
            locations = [.. place is null ? [] : new[] { new DetailLocation(Where(place.FullPath), DetailsText.PackPlaceState(place), FolderText.Service(place.FullPath)) },
                .. folders.Where(f => place is null || !string.Equals(f.FullPath, place.FullPath, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new DetailLocation(Where(f.FullPath), DetailsText.PackFolderState(f, pdfs), FolderText.Service(f.FullPath)))];
        }
        else if (!entry.IsElsewhere)
        {
            locations = [.. (await library.Get<LibraryStore>().GetLocationsAsync(entry.DocumentId, ct))
                .Select(l => new DetailLocation(Where(l.FullPath), DetailsText.LocationState(l), FolderText.Service(l.FullPath)))];
            // An adventure inside a file is in the file's copies; its file's details list them.
            if (!entry.IsPart && await library.Get<CopiesService>().GetAsync(entryId, ct) is { Count: > 1 } files)
                copies = [.. files.Select(c =>
                {
                    var path = c.Locations.FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
                    return new DetailCopy(c.Copy.DocumentId, DetailsText.CopyHeading(c.Copy, path is not null), DetailsText.CopyDetail(c.Copy),
                        path is null ? null : Where(path), c.Copy.IsCurrent, path is null ? null : FolderText.Service(path));
                })];
            if (entry.Format == SourceFormats.Pdf)
            {
                // Pages are typed and shown as the file numbers them.
                var partsService = library.Get<PartsService>();
                labels = await partsService.GetPageLabelsAsync(entry.DocumentId, ct);
                if (!entry.IsPart)
                    parts = [.. (await partsService.GetAsync(entryId, ct)).Select(p => new DetailPart(p.EntryId.Value, p.Title,
                        DetailsText.PartPages(p.Pages, labels), From(p.Pages, labels), To(p.Pages, labels)))];
            }
        }

        var stages = library.Get<IStageCatalog>();
        var notes = library.Get<NotesService>();
        var noted = (string.IsNullOrWhiteSpace(await notes.GetEntryNoteAsync(entryId, ct)) ? 0 : 1) + (await notes.GetPageNotesAsync(entryId, ct)).Count;
        var hasPdfs = entry.PdfMembers > 0 || packFiles.Any(f => f.IsPdf);
        var collections = await CollectionsApi.AllAsync(library, ct);
        var chips = (await library.Get<CollectionsService>().GetForEntryAsync(entryId, ct))
            .Select(collections.GetValueOrDefault).OfType<CollectionInfo>()
            .Select(c => new CollectionChip(c.Id, c.Name, CollectionText.PathOf(collections, c.Id)))
            .OrderBy(c => c.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new BookDetails(
            LibraryApi.ToCard(entry, away),
            DetailsText.Eyebrow(entry, hasPdfs),
            entry.Publisher ?? "",
            CardText.Pages(entry),
            !entry.IsElsewhere,
            [.. fields.Select(f => ToField(f, vocabulary, DetailsText.IsPrimary(f.Field, entry.IsElsewhere, adventure)))],
            parts,
            DetailsText.PartsHeading(parts.Count),
            !entry.IsPack && !entry.IsElsewhere && !entry.IsPart && entry.Format == SourceFormats.Pdf,
            entry.IsPart ? entry.PartLine ?? "" : null,
            entry.IsPart ? entry.ParentEntryId?.Value : null,
            packFiles,
            DetailsText.PackFilesHeading(packFiles.Count, hasPdfs),
            DetailsText.FactsHeading(entry),
            [.. DetailsText.Facts(entry, details).Select(f => new DetailFact(f.Label, f.Value))],
            !entry.IsElsewhere,
            locations,
            copies,
            DetailsText.CopiesHeading(copies.Count),
            // A pack's processing is its images', each read like any image.
            entry.IsPack ? [] : [.. details.Stages.Select(s => new DetailFact(stages.Find(s.Stage)?.Names.Label ?? s.Stage.ToString(), DetailsText.Progress(s)))],
            DetailsText.IsReprocessing(entry, details),
            DetailsText.NotesLabel(noted),
            PageNumbers.Display(0, labels),
            entry.IsPart ? From(entry.Pages, labels) : "",
            entry.IsPart ? To(entry.Pages, labels) : "",
            DetailsText.SplitPackLabel(hasPdfs),
            DetailsText.SplitPackTip(hasPdfs),
            chips,
            await ChaptersAsync(library, entry, labels, ct),
            library.Get<AskService>().IsOn && !entry.IsElsewhere && !entry.IsPack && SourceFormats.HasPages(entry.Format)
                && entry.TextAccess == TextAccess.Readable,
            entry.IsElsewhere && await library.Get<ChosenCovers>().HasChosenAsync(entryId, ct),
            // Asked before every page is read by OCR, so a long book's wait is known; only for a file with pages.
            !entry.IsElsewhere && !entry.IsPack && entry.PageCount is int pages and > 0
                ? DetailsText.OcrEveryPageQuestion(entry.Title, pages) + " " + DetailsText.OcrEveryPageNote : null);
    }

    /// <summary>What the AI found reading the book chapter by chapter; an adventure inside a file shows only its own chapters.</summary>
    static async Task<IReadOnlyList<DetailChapter>> ChaptersAsync(UserLibrary library, LibraryEntry entry, IReadOnlyList<string?> labels,
        CancellationToken ct)
    {
        if (entry.IsElsewhere || entry.IsPack || !SourceFormats.HasPages(entry.Format)) return [];
        var chapters = await library.Get<ChapterScanService>().GetChaptersAsync(entry.DocumentId, entry.IsPart ? entry.Pages : null, ct);
        return [.. chapters.Select(c =>
        {
            var lines = ChapterFields.Describe(c.Tags);
            return new DetailChapter(c.Title, c.Pages.Describe(labels), lines, DetailsText.ChapterNote(c, lines.Count));
        })];
    }

    /// <summary>An adventure's first page as the editor starts with it.</summary>
    static string From(PartPages? pages, IReadOnlyList<string?> labels) => pages is { } p ? PageNumbers.Display(p.First, labels) : "";

    /// <summary>Its last page, or blank for one page.</summary>
    static string To(PartPages? pages, IReadOnlyList<string?> labels) => pages is { } p && p.Count > 1 ? PageNumbers.Display(p.Last, labels) : "";

    static DetailField ToField(EffectiveField field, Vocabulary vocabulary, bool primary)
    {
        DetailValue Value(EffectiveValue v)
        {
            var link = field.Field.Kind == FieldKind.Store ? StoreLink.Parse(v.Value) : (StoreLink?)null;
            return new DetailValue(FieldText.Display(field.Field, v.Value, vocabulary), v.Value, v.Normalized, v.Confirmed, link?.Url is { } url ? StoreLink.WebAddress(url) : null, link?.ShortUrl,
                [.. v.Support.Select(c => FieldText.Source(c)).Distinct()]);
        }

        var values = field.Values.Select(Value).ToList();
        var suggested = values.Any(v => !v.Confirmed);
        return new DetailField(field.Field.Key, field.Field.Label, primary, FieldText.Join([.. values.Select(v => v.Text)]), values.Count > 0, suggested,
            field.NeedsReview, FieldText.EvidenceLabel(field.NeedsReview, suggested), values.Any(v => v.Confirmed), values,
            [.. field.Alternatives.Select(Value)], field.Field.Multiple, FieldText.EditHint(field.Field), FieldText.ChoicesLabel(field.Field),
            FieldText.Choices(field.Field, vocabulary));
    }
}
