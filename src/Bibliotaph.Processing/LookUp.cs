using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// "Look up books online by their ISBN": off until the user turns it on, since it sends the ISBNs a library holds to
/// someone else's server. Held in memory once loaded, so the Look up stage's gate can be read at any time.
/// </summary>
public sealed class BookLookups(SettingsStore settings, IBookCatalogue catalogue)
{
    volatile bool _on;

    /// <summary>Raised when the setting changes, from any thread.</summary>
    public event EventHandler? Changed;

    public bool IsOn => _on;

    /// <summary>The catalogue books are looked up in.</summary>
    public IBookCatalogue Catalogue => catalogue;

    public async Task LoadAsync(CancellationToken ct = default) =>
        _on = await settings.GetAsync(SettingKeys.LookUpOnline, ct) == bool.TrueString;

    public async Task SetOnAsync(bool on, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.LookUpOnline, on.ToString(), ct);
        if (_on == on) return;
        _on = on;
        Changed?.Invoke(this, EventArgs.Empty);
    }

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
/// renames is looked at again for another file with its new title and publisher. Runs in its own lane, only while the
/// user has lookups on, and waits with a notice while the catalogue can't be reached. A book no catalogue knew is
/// asked again once there are more catalogues to ask.
/// </summary>
public sealed class LookUpStage(BookLookups lookups, IndexQueries queries, EntryStore entries, MetadataStore metadata, MetadataProjector projector,
    VersionFinder versions) : IGatedStage, IRevisitedStage
{
    public Stage Stage => Stage.LookUp;

    public Lane Lane => Lane.Online;

    public Stage? After => Stage.Identify;

    /// <summary>Books identified before it existed get it too; they wait in the queue until lookups are on.</summary>
    public bool Backfill => true;

    public StageNames Names => new("Looking it up online", "Looking it up online", "Looking up");

    public bool IsReady => lookups.IsOn;

    public event EventHandler? ReadyChanged
    {
        add => lookups.Changed += value;
        remove => lookups.Changed -= value;
    }

    /// <summary>Asked again when lookups are turned on, too, as the catalogues may have changed while they were off.</summary>
    public event EventHandler? RunAgainChanged
    {
        add
        {
            lookups.Changed += value;
            lookups.Catalogue.NameChanged += value;
        }
        remove
        {
            lookups.Changed -= value;
            lookups.Catalogue.NameChanged -= value;
        }
    }

    public async Task<Func<string?, bool>> RunAgainAsync(CancellationToken ct)
    {
        // Only while lookups are on: off, nothing is sent anywhere, not even to ask where it would be.
        if (lookups.IsOn) await lookups.Catalogue.RefreshAsync(ct);
        var catalogues = lookups.Catalogue.Name;
        return reason => BookLookups.AskedOfFewer(reason, catalogues);
    }

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (!lookups.IsOn) return new StageOutcome.Unavailable("Looking books up online is switched off.");
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
