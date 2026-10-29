using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// Looking books up by their ISBN: the catalogue asked, and the words the Look up stage records a miss in. Always on:
/// each catalogue is sent a book's ISBNs, and RPGGeek, which is searched by name, its title too; the Processing
/// section says where they go.
/// </summary>
public sealed class BookLookups(IBookCatalogue catalogue)
{
    /// <summary>The catalogue books are looked up in.</summary>
    public IBookCatalogue Catalogue => catalogue;

    const string NoRecordBefore = "No record of its ISBN in ";
    const string AskedBefore = "Looked up in ";

    /// <summary>Why a book none of <paramref name="catalogues"/> ("Open Library or Google Books") knows wasn't looked up.</summary>
    public static string NoRecordIn(string catalogues) => $"{NoRecordBefore}{catalogues}.";

    /// <summary>Where a book was looked up, at the end of how its lookup ended, so it is asked again where more can be asked.</summary>
    public static string AskedOf(string catalogues) => $"{AskedBefore}{catalogues}.";

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
        else if (reason.LastIndexOf(AskedBefore, StringComparison.Ordinal) is var at and >= 0) list = reason[(at + AskedBefore.Length)..^1];
        else return false;
        return catalogues.Split(" or ").Except(list.Split(" or "), StringComparer.Ordinal).Any();
    }
}

/// <summary>
/// Look up: asks the online catalogues about the ISBNs a book prints (<see cref="IdentifyStage"/>), and suggests the
/// title, authors, publisher, year and series their records give, above the AI's (<see cref="AssertionOrigin.Lookup"/>),
/// each field from the catalogue the user prefers that has it, and the system, edition, types, themes and settings a
/// game catalogue files it under, as the vocabulary names them. A name the vocabulary doesn't have is proposed as a new
/// term the way the AI's are, so Settings › Vocabulary's choice for the AI's new names holds for it too. A record is
/// used only when its title is printed in the book,
/// so a misprinted or reused ISBN can't rename it. A book the record
/// renames is looked at again for another file with its new title and publisher. Runs in its own lane, and waits with
/// a notice while the catalogue can't be reached. A book is asked again once there are more catalogues to ask.
/// </summary>
public sealed class LookUpStage(BookLookups lookups, IndexQueries queries, EntryStore entries, MetadataStore metadata, MetadataProjector projector,
    VersionFinder versions, VocabularyStore vocabularies, SettingsStore settings) : IRevisitedStage
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

        // For a catalogue searched only by name, when the others' records give no title to search for.
        var title = await queries.GetTitleAsync(job.DocumentId, ct);
        var embedded = (await queries.GetEmbeddedInfoAsync(job.DocumentId, ct))?.Title;
        IReadOnlyList<string> titles = [.. new[] { title, embedded }.OfType<string>().Select(t => t.Trim()).Where(t => t.Length is > 0 and <= 300)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        CatalogueAnswer answer;
        try
        {
            answer = await lookups.Catalogue.AskAsync(isbns, titles, ct);
        }
        catch (CatalogueUnavailableException ex)
        {
            return new StageOutcome.Unavailable(ex.Message);
        }
        if (answer.Records.Count == 0) return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NoRecordIn(answer.AskedOf));

        var places = await PlacesAsync(job.DocumentId, ct);
        var named = answer.Records.Where(r => BookRecords.IsNamedIn(r, places)).ToList();
        if (named.Count == 0)
        {
            var record = answer.Records[0];
            return new StageOutcome.Done(StageStatus.Skipped, [],
                $"{record.Source}'s record for its ISBN is of “{record.Title}”, which isn't printed in it, so it wasn't used. {BookLookups.AskedOf(answer.AskedOf)}");
        }

        var asked = BookLookups.AskedOf(answer.AskedOf);
        if (await entries.GetEntryAsync(job.DocumentId, ct) is not { } entry) return new StageOutcome.Done(StageStatus.Complete, [], asked);
        var run = string.Join('+', named.Select(r => $"{r.Source.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)}:{r.Key}"));
        List<MetadataProposal> proposals = [.. BookRecords.Proposals(named), .. await TermProposalsAsync(named, ct)];
        if (await metadata.ApplyRunAsync(entry.EntryId, job.ContentHash, run, AssertionOrigin.Lookup, proposals, ct))
        {
            await projector.ProjectAsync([entry.EntryId], ct);
            // Match looked for a book of the same title and publisher before the record named this one properly.
            await versions.ProposeByTitleAndPublisherAsync(job.DocumentId, ct);
        }
        return new StageOutcome.Done(StageStatus.Complete, [], asked);
    }

    /// <summary>
    /// The terms the records file the book under, by their keys. A new name is proposed as the AI's are: it waits in Needs
    /// review, or joins the vocabulary at once where Settings › Vocabulary says so; one the user rejected is left out.
    /// </summary>
    async Task<List<MetadataProposal>> TermProposalsAsync(IReadOnlyList<BookRecord> records, CancellationToken ct)
    {
        var proposals = new List<MetadataProposal>();
        var grew = false;
        foreach (var term in BookRecords.Terms(records, await vocabularies.GetAsync(ct)))
        {
            var key = term.Term?.Key;
            if (key is null)
            {
                var vocabulary = term.Field.Vocabulary!;
                var (proposed, state, added) = await vocabularies.ProposeTermAsync(vocabulary, term.Label, await settings.AddsAiTermsAsync(vocabulary, ct), ct);
                if (state == TermState.Rejected) continue;
                grew |= added;
                key = proposed.Key;
            }
            proposals.Add(new MetadataProposal(term.Field, key, AssertionOrigin.Lookup, term.Quote));
        }
        if (grew) await projector.ProjectVocabularyAsync(ct);
        return proposals;
    }

    /// <summary>Where a book names itself: its front pages, its file's name and the title its file gives.</summary>
    async Task<List<string?>> PlacesAsync(long documentId, CancellationToken ct)
    {
        var front = (await queries.GetAllPageTextsAsync(documentId, ct)).Where(t => !string.IsNullOrWhiteSpace(t)).Take(BookIdentifiers.FrontPages);
        return [.. front, await queries.GetTitleAsync(documentId, ct), (await queries.GetEmbeddedInfoAsync(documentId, ct))?.Title];
    }
}
