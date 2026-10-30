using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// Looking books up by their ISBN, product code or title: the catalogue asked, and the words the Look up stage records
/// a miss in. Always on: each catalogue is sent a book's ISBNs and product codes, and RPGGeek and AdventureLookup, which
/// are searched by name, its title too; the Processing section says where they go.
/// </summary>
public sealed class BookLookups(IBookCatalogue catalogue)
{
    /// <summary>The catalogue books are looked up in.</summary>
    public IBookCatalogue Catalogue => catalogue;

    const string NoRecordBefore = "No record of its ISBN in ";
    const string NoCodeRecordBefore = "No record of its product code in ";
    const string NoTitleRecordBefore = "No record of its title in ";
    const string AskedBefore = "Looked up in ";

    /// <summary>
    /// Why a book printing no ISBN or product code isn't looked up where no catalogue can find a book by title alone:
    /// it's looked up by title once one can, as when a PC connects to its web library.
    /// </summary>
    public const string NothingToLookUp = "It prints no ISBN or product code to look up.";

    /// <summary>Why a book with no ISBN, product code or text to find its title in isn't looked up, as an image isn't.</summary>
    public const string NothingAtAll = "It prints no ISBN, product code or title to look up.";

    /// <summary>
    /// Why a book none of <paramref name="catalogues"/> ("Open Library or Google Books") knows wasn't looked up, by
    /// what it was looked up by: its ISBNs, else its product codes, else its title.
    /// </summary>
    public static string NoRecordIn(string catalogues, bool byCode = false, bool byTitle = false) =>
        $"{(byTitle ? NoTitleRecordBefore : byCode ? NoCodeRecordBefore : NoRecordBefore)}{catalogues}.";

    /// <summary>
    /// Whether a book was passed over for printing no ISBN, before product codes were looked up too: it's looked at
    /// again, and one printing neither is passed over again in words that say so.
    /// </summary>
    public static bool HadNoIsbn(string? reason) => reason == "It prints no ISBN to look up.";

    /// <summary>Where a book was looked up, at the end of how its lookup ended, so it is asked again where more can be asked.</summary>
    public static string AskedOf(string catalogues) => $"{AskedBefore}{catalogues}.";

    /// <summary>
    /// Whether a book's record was set aside without a word, as before Needs review asked about a record whose title
    /// isn't printed in the book: it's looked up once more, so the question is put.
    /// </summary>
    public static bool SetAside(string? reason) => reason?.Contains("which isn't printed in it, so it wasn't used.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether a book was looked up in fewer catalogues than <paramref name="catalogues"/> names, as one asked only of
    /// Open Library is once RPGGeek or Google Books can be asked too: it's worth asking again, whether it was found or
    /// not, since each field comes from the catalogue the user prefers. One asked of more isn't, so a PC that can't
    /// reach its web library for a while doesn't ask the books it asked there all over again.
    /// </summary>
    public static bool AskedOfFewer(string? reason, string catalogues)
    {
        if (reason is null || !reason.EndsWith('.')) return false;
        string list;
        if (reason.StartsWith(NoRecordBefore, StringComparison.Ordinal)) list = reason[NoRecordBefore.Length..^1];
        else if (reason.StartsWith(NoCodeRecordBefore, StringComparison.Ordinal)) list = reason[NoCodeRecordBefore.Length..^1];
        else if (reason.StartsWith(NoTitleRecordBefore, StringComparison.Ordinal)) list = reason[NoTitleRecordBefore.Length..^1];
        else if (reason.LastIndexOf(AskedBefore, StringComparison.Ordinal) is var at and >= 0) list = reason[(at + AskedBefore.Length)..^1];
        else return false;
        return catalogues.Split(" or ").Except(list.Split(" or "), StringComparer.Ordinal).Any();
    }
}

/// <summary>
/// Look up: asks the online catalogues about the ISBNs and product codes a book prints (<see cref="IdentifyStage"/>; a
/// code only RPGGeek's records list), and suggests the title, authors, publisher, year, series, artists and mechanics
/// their records give, above the AI's (<see cref="AssertionOrigin.Lookup"/>),
/// each field from the catalogue the user prefers that has it, and the system, edition, types, themes and settings a
/// game catalogue files it under, as the vocabulary names them. A name the vocabulary doesn't have is proposed as a new
/// term the way the AI's are, so Settings › Vocabulary's choice for the AI's new names holds for it too. A record is
/// used only when its title is printed in the book, so a misprinted or reused ISBN can't rename it; one whose title
/// isn't waits in Needs review as "Same book?" (<see cref="LookupRecordStore"/>). A book printing neither an ISBN nor a
/// code is looked up by its title where a catalogue finds books by title alone, as AdventureLookup does: a record it
/// gives is dropped unless the title is printed on the book's front pages, and used when its authors, publisher or page
/// count agree too (<see cref="BookRecords.Agrees"/>), or else asked about. A book the record renames is looked at
/// again for another file with its new title and publisher. Runs in its own lane, and waits with
/// a notice while the catalogue can't be reached. A book is asked again once there are more catalogues to ask.
/// </summary>
public sealed class LookUpStage(BookLookups lookups, IndexQueries queries, EntryStore entries, LookupRecordStore records, MetadataProjector projector,
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
        var byTitle = lookups.Catalogue.FindsByTitleAlone;
        return reason => BookLookups.AskedOfFewer(reason, catalogues) || BookLookups.SetAside(reason) || BookLookups.HadNoIsbn(reason)
            || (byTitle && reason == BookLookups.NothingToLookUp);
    }

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var isbns = await queries.GetIsbnsAsync(job.DocumentId, ct);
        var codes = await queries.GetProductCodesAsync(job.DocumentId, ct);
        var front = await FrontPagesAsync(job.DocumentId, ct);
        var byTitle = isbns.Count == 0 && codes.Count == 0;
        if (byTitle)
        {
            // A book found by title has to print it, and an image prints nothing.
            if (front.Count == 0) return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NothingAtAll);
            if (!lookups.Catalogue.FindsByTitleAlone) return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NothingToLookUp);
        }

        // For a catalogue searched only by name, when the others' records give no title to search for.
        var title = await queries.GetTitleAsync(job.DocumentId, ct);
        var embedded = (await queries.GetEmbeddedInfoAsync(job.DocumentId, ct))?.Title;
        IReadOnlyList<string> titles = [.. new[] { title, embedded }.OfType<string>().Select(t => t.Trim()).Where(t => t.Length is > 0 and <= 300)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        CatalogueAnswer answer;
        try
        {
            answer = await lookups.Catalogue.AskAsync(isbns, titles, codes, ct);
        }
        catch (CatalogueUnavailableException ex)
        {
            return new StageOutcome.Unavailable(ex.Message);
        }
        if (answer.Records.Count == 0)
        {
            // A record no catalogue gives any more isn't worth asking about.
            await records.SaveAsync(job.ContentHash, [], _ => false, ct);
            return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NoRecordIn(answer.AskedOf, byCode: isbns.Count == 0, byTitle: byTitle));
        }

        // A record found by an identifier must name the book where it names itself, and one found by title alone must
        // have its title printed in it, or it's dropped; it's used when something else agrees, and asked about if not.
        List<string?> places = [.. front, title, embedded];
        var pages = await queries.GetPageCountAsync(job.DocumentId, ct);
        List<BookRecord> found = [.. answer.Records.Where(r => !BookRecords.ByTitle(r) || BookRecords.IsNamedIn(r, front))];
        var saved = await records.SaveAsync(job.ContentHash, found,
            r => BookRecords.ByTitle(r) ? BookRecords.Agrees(r, front, pages) : BookRecords.IsNamedIn(r, places), ct);
        var asked = BookLookups.AskedOf(answer.AskedOf);
        if (await entries.GetEntryAsync(job.DocumentId, ct) is { } entry)
        {
            var (changed, grew) = await records.ApplyAsync(entry.EntryId, job.ContentHash, ct);
            if (grew) await projector.ProjectVocabularyAsync(ct);
            if (changed)
            {
                await projector.ProjectAsync([entry.EntryId], ct);
                // Match looked for a book of the same title and publisher before the record named this one properly.
                if (saved.Used.Count > 0) await versions.ProposeByTitleAndPublisherAsync(job.DocumentId, ct);
            }
        }
        if (saved.Used.Count > 0) return new StageOutcome.Done(StageStatus.Complete, [], asked);
        if (saved.Waiting.Count == 0 && saved.Refused.Count == 0)
            return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NoRecordIn(answer.AskedOf, byCode: isbns.Count == 0, byTitle: byTitle));
        if (saved.Waiting.Count > 0)
        {
            var record = saved.Waiting[0];
            return new StageOutcome.Done(StageStatus.Skipped, [], BookRecords.ByTitle(record)
                ? $"{record.Source}'s record of “{record.Title}” has the title it prints, but not its authors, publisher or page count, so Needs review asks whether it's this book. {asked}"
                : $"{record.Source}'s record for its {BookRecords.FoundBy(record)} is of “{record.Title}”, which isn't printed in it, so Needs review asks whether it's this book. {asked}");
        }
        var refused = saved.Refused[0];
        return new StageOutcome.Done(StageStatus.Skipped, [],
            $"You said {refused.Source}'s record for its {BookRecords.FoundBy(refused)}, of “{refused.Title}”, is of another book. {asked}");
    }

    /// <summary>The text of a book's front pages, where it prints its title, its authors and its publisher.</summary>
    async Task<List<string?>> FrontPagesAsync(long documentId, CancellationToken ct) =>
        [.. (await queries.GetAllPageTextsAsync(documentId, ct)).Where(t => !string.IsNullOrWhiteSpace(t)).Take(BookIdentifiers.FrontPages)];
}
