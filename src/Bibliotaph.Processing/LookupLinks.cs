using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>A book's record on RPGGeek as its details show it: what RPGGeek calls the book, and its page there.</summary>
public sealed record LinkedRecord(string Key, string Title, string Url);

/// <summary>
/// Giving a book its RPGGeek record by hand, from its details: the user pastes the address of the book's page there
/// and the record is read by the item's id, for a book that prints nothing a search finds it by. It is the book's
/// record from then on whatever a lookup finds (<see cref="LookupRecordStore.LinkAsync"/>), fills its fields at once as
/// a looked-up record does, above the AI's suggestions and below the user's own values, and goes to the user's other
/// PCs as an answer to Same book? does. Only where RPGGeek can be asked, which is through the web library, and only for
/// a book with a file of its own or one owned elsewhere: a record is kept by its file, or by the book's own name.
/// </summary>
public sealed class LookupLinks(BookLookups lookups, LookupRecordStore records, MetadataProjector projector, VersionFinder versions, IndexQueries queries)
{
    /// <summary>What a book that can't be given a record is answered with, as when the PC has just lost its web library.</summary>
    public const string NotHere = "RPGGeek can't be asked from here just now.";

    /// <summary>The label of the action that opens the box.</summary>
    public const string Offer = "Look up from an RPGGeek link…";

    /// <summary>The same, for a book that has a record there already.</summary>
    public const string OfferAnother = "Use another RPGGeek link…";

    /// <summary>What the box says under where the address goes.</summary>
    public const string Hint = "Paste the address of the book's page on RPGGeek, like https://rpggeek.com/rpgitem/12345/its-name.";

    /// <summary>What the details say of the record the book uses.</summary>
    public static string Shown(LinkedRecord record) => $"On RPGGeek as {record.Title}";

    static bool HasOwnFile(LibraryEntry entry) => !entry.IsPack && !entry.IsElsewhere && !entry.IsPart && !entry.IsPackFile;

    /// <summary>A book owned elsewhere keeps records too, under its own name in a file's place.</summary>
    static bool KeepsRecords(LibraryEntry entry) => entry.IsElsewhere || HasOwnFile(entry);

    /// <summary>Whether a record can be given for the book: one with a file of its own, while RPGGeek can be asked.</summary>
    public bool CanLink(LibraryEntry entry) => KeepsRecords(entry) && lookups.Asks(RpgGeekCatalogue.Source);

    /// <summary>
    /// What the book's last lookup searched for and what came of it, a labelled line each, for its details: none for
    /// a book not looked up yet, or with no file of its own.
    /// </summary>
    public async Task<IReadOnlyList<(string Label, string Value)>> SearchedAsync(LibraryEntry entry, CancellationToken ct = default) =>
        HasOwnFile(entry) && LookupSearch.Read(await queries.GetLookupSearchAsync(entry.DocumentId, ct)) is { } search ? search.Facts() : [];

    /// <summary>The RPGGeek record the book uses, found by a lookup or given by hand, or null.</summary>
    public async Task<LinkedRecord?> GetAsync(LibraryEntry entry, CancellationToken ct = default) =>
        KeepsRecords(entry) && await records.GetUsedAsync(entry.EntryId, RpgGeekCatalogue.Source, ct) is { } record
            ? new LinkedRecord(record.Key, record.Title, RpgGeekCatalogue.PageOf(record.Key))
            : null;

    /// <summary>
    /// Reads the item whose page <paramref name="link"/> is and makes it the book's record. Returns what is wrong with
    /// the link, or that RPGGeek couldn't be asked just now, in words for the box; null once the book has the record.
    /// </summary>
    public async Task<string?> LinkAsync(LibraryEntry entry, string? link, CancellationToken ct = default)
    {
        var (item, problem) = RpgGeekCatalogue.ItemIn(link);
        if (item is null) return problem;
        if (!CanLink(entry)) return NotHere;
        BookRecord? record;
        try
        {
            record = await lookups.Catalogue.ReadAsync(RpgGeekCatalogue.Source, item, ct);
        }
        catch (CatalogueUnavailableException ex)
        {
            return Later(ex.Message);
        }
        if (record is null) return RpgGeekCatalogue.NoSuchBook;
        var files = await records.LinkAsync(entry.EntryId, record, ct);
        if (files.Count == 0) return NotHere;
        // Match looked for a book of the same title and publisher before the record named this one properly.
        if (await ApplyAsync(entry.EntryId, files, ct) && !entry.IsElsewhere) await versions.ProposeByTitleAndPublisherAsync(entry.DocumentId, ct);
        return null;
    }

    /// <summary>
    /// Not this book, on the record the details show: it's never used for the book again unless its page is given.
    /// Returns whether the book had one.
    /// </summary>
    public async Task<bool> RefuseAsync(LibraryEntry entry, CancellationToken ct = default)
    {
        if (await GetAsync(entry, ct) is not { } record) return false;
        await ApplyAsync(entry.EntryId, await records.RefuseAsync(entry.EntryId, RpgGeekCatalogue.Source, record.Key, ct), ct);
        return true;
    }

    /// <summary>The book's suggestions from its files' records as they are now. Returns whether any changed.</summary>
    async Task<bool> ApplyAsync(EntryId entryId, IReadOnlyList<string> files, CancellationToken ct)
    {
        var (changed, grew) = (false, false);
        foreach (var file in files)
        {
            var applied = await records.ApplyAsync(entryId, file, ct);
            changed |= applied.Changed;
            grew |= applied.VocabularyChanged;
        }
        if (grew) await projector.ProjectVocabularyAsync(ct);
        // Projected even when no value changed: Found online follows which catalogues' records the book uses.
        await projector.ProjectAsync([entryId], ct);
        return changed;
    }

    /// <summary>
    /// Why RPGGeek couldn't be asked, for someone waiting at the box: a lookup's own words promise the book is looked
    /// up later, which nothing does for an address typed here.
    /// </summary>
    static string Later(string why)
    {
        var at = why.IndexOf(". ", StringComparison.Ordinal);
        return $"{(at < 0 ? why.TrimEnd('.') : why[..at])}. Try again in a little while.";
    }
}
