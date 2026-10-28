using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// Looking books up by their ISBN: the catalogue asked, and the words the Look up stage records a miss in. Always on:
/// each catalogue is sent a book's ISBNs and nothing else, and the Processing section says where they go.
/// </summary>
public sealed class BookLookups(IBookCatalogue catalogue)
{
    /// <summary>The catalogue books are looked up in.</summary>
    public IBookCatalogue Catalogue => catalogue;

    const string NoRecordBefore = "No record of its ISBN in ";

    /// <summary>Why a book none of <paramref name="catalogues"/> ("Open Library or Google Books") knows wasn't looked up.</summary>
    public static string NoRecordIn(string catalogues) => $"{NoRecordBefore}{catalogues}.";

    /// <summary>
    /// Whether a book was found in none of fewer catalogues than <paramref name="catalogues"/> names, as one asked only of
    /// Open Library is once Google Books can be asked too: it's worth asking again. One asked of more isn't, so a PC
    /// that can't reach its web library for a while doesn't ask the books it asked there all over again.
    /// </summary>
    public static bool AskedOfFewer(string? reason, string catalogues)
    {
        if (reason is null || !reason.StartsWith(NoRecordBefore, StringComparison.Ordinal) || !reason.EndsWith('.')) return false;
        var asked = reason[NoRecordBefore.Length..^1].Split(" or ");
        return catalogues.Split(" or ").Except(asked, StringComparer.Ordinal).Any();
    }
}

/// <summary>
/// Look up: asks an online catalogue about the ISBNs a book prints (<see cref="IdentifyStage"/>), and suggests the
/// title, authors, publisher and year its record gives, above the AI's (<see cref="AssertionOrigin.Lookup"/>). A record
/// is used only when its title is printed in the book, so a misprinted or reused ISBN can't rename it. A book the record
/// renames is looked at again for another file with its new title and publisher. Runs in its own lane, and waits with
/// a notice while the catalogue can't be reached. A book no catalogue knew is asked again once there are more
/// catalogues to ask.
/// </summary>
public sealed class LookUpStage(BookLookups lookups, IndexQueries queries, EntryStore entries, MetadataStore metadata, MetadataProjector projector,
    VersionFinder versions) : IRevisitedStage
{
    public Stage Stage => Stage.LookUp;

    public Lane Lane => Lane.Online;

    public Stage? After => Stage.Identify;

    /// <summary>Books identified before it existed get it too.</summary>
    public bool Backfill => true;

    public StageNames Names => new("Looking it up online", "Looking it up online", "Looking up");

    /// <summary>Asked again when the catalogues change, as when a PC connects to its web library.</summary>
    public event EventHandler? RunAgainChanged
    {
        add => lookups.Catalogue.NameChanged += value;
        remove => lookups.Catalogue.NameChanged -= value;
    }

    public async Task<Func<string?, bool>> RunAgainAsync(CancellationToken ct)
    {
        await lookups.Catalogue.RefreshAsync(ct);
        var catalogues = lookups.Catalogue.Name;
        return reason => BookLookups.AskedOfFewer(reason, catalogues);
    }

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var isbns = await queries.GetIsbnsAsync(job.DocumentId, ct);
        if (isbns.Count == 0) return new StageOutcome.Done(StageStatus.Skipped, [], "It prints no ISBN to look up.");

        var catalogue = lookups.Catalogue;
        BookRecord? record;
        try
        {
            record = await catalogue.FindAsync(isbns, ct);
        }
        catch (CatalogueUnavailableException ex)
        {
            return new StageOutcome.Unavailable(ex.Message);
        }
        if (record is null) return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NoRecordIn(catalogue.Name));

        if (!BookRecords.IsNamedIn(record, await PlacesAsync(job.DocumentId, ct)))
            return new StageOutcome.Done(StageStatus.Skipped, [],
                $"{record.Source}'s record for its ISBN is of “{record.Title}”, which isn't printed in it, so it wasn't used.");

        if (await entries.GetEntryAsync(job.DocumentId, ct) is not { } entry) return StageOutcome.Complete();
        var source = record.Source.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (await metadata.ApplyRunAsync(entry.EntryId, job.ContentHash, $"{source}:{record.Key}", AssertionOrigin.Lookup, BookRecords.Proposals(record), ct))
        {
            await projector.ProjectAsync([entry.EntryId], ct);
            // Match looked for a book of the same title and publisher before the record named this one properly.
            await versions.ProposeByTitleAndPublisherAsync(job.DocumentId, ct);
        }
        return StageOutcome.Complete();
    }

    /// <summary>Where a book names itself: its front pages, its file's name and the title its file gives.</summary>
    async Task<List<string?>> PlacesAsync(long documentId, CancellationToken ct)
    {
        var front = (await queries.GetAllPageTextsAsync(documentId, ct)).Where(t => !string.IsNullOrWhiteSpace(t)).Take(BookIdentifiers.FrontPages);
        return [.. front, await queries.GetTitleAsync(documentId, ct), (await queries.GetEmbeddedInfoAsync(documentId, ct))?.Title];
    }
}
