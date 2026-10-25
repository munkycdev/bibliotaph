using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
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
            locations = [.. place is null ? [] : new[] { new DetailLocation(Where(place.FullPath), DetailsText.PackPlaceState(place)) },
                .. folders.Where(f => place is null || !string.Equals(f.FullPath, place.FullPath, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new DetailLocation(Where(f.FullPath), DetailsText.PackFolderState(f, pdfs)))];
        }
        else if (!entry.IsElsewhere)
        {
            locations = [.. (await library.Get<LibraryStore>().GetLocationsAsync(entry.DocumentId, ct))
                .Select(l => new DetailLocation(Where(l.FullPath), DetailsText.LocationState(l)))];
            // An adventure inside a file is in the file's copies; its file's details list them.
            if (!entry.IsPart && await library.Get<CopiesService>().GetAsync(entryId, ct) is { Count: > 1 } files)
                copies = [.. files.Select(c =>
                {
                    var path = c.Locations.FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
                    return new DetailCopy(c.Copy.DocumentId, DetailsText.CopyHeading(c.Copy, path is not null), DetailsText.CopyDetail(c.Copy),
                        path is null ? null : Where(path), c.Copy.IsCurrent);
                })];
            if (entry.Format == SourceFormats.Pdf && !entry.IsPart)
            {
                var partsService = library.Get<PartsService>();
                var labels = await partsService.GetPageLabelsAsync(entry.DocumentId, ct);
                parts = [.. (await partsService.GetAsync(entryId, ct)).Select(p => new DetailPart(p.EntryId.Value, p.Title, DetailsText.PartPages(p.Pages, labels)))];
            }
        }

        var stages = library.Get<IStageCatalog>();
        return new BookDetails(
            LibraryApi.ToCard(entry, away),
            DetailsText.Eyebrow(entry, entry.PdfMembers > 0 || packFiles.Any(f => f.IsPdf)),
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
            DetailsText.PackFilesHeading(packFiles.Count, entry.PdfMembers > 0 || packFiles.Any(f => f.IsPdf)),
            DetailsText.FactsHeading(entry),
            [.. DetailsText.Facts(entry, details).Select(f => new DetailFact(f.Label, f.Value))],
            !entry.IsElsewhere,
            locations,
            copies,
            DetailsText.CopiesHeading(copies.Count),
            // A pack's processing is its images', each read like any image.
            entry.IsPack ? [] : [.. details.Stages.Select(s => new DetailFact(stages.Find(s.Stage)?.Names.Label ?? s.Stage.ToString(), DetailsText.Progress(s)))]);
    }

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
            [.. field.Alternatives.Select(Value)]);
    }
}
