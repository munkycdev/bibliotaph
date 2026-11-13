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

    /// <summary>Whether <paramref name="source"/> ("RPGGeek") is among the catalogues asked now, as it is only through the web library.</summary>
    public bool Asks(string source) => catalogue.Name.Split(" or ").Contains(source, StringComparer.Ordinal);

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
    public const string NothingAtAll = LookupReasons.NothingAtAll;

    /// <summary>Why a map, a character sheet or a handout printing no ISBN or code isn't looked up: no catalogue of books lists it.</summary>
    public const string NotABook = LookupReasons.NotABook;

    /// <summary>The types that say a file isn't a book.</summary>
    static readonly HashSet<string> LooseTypes = new(StringComparer.Ordinal) { "map-pack", "handout", "character-sheet" };

    /// <summary>What a file's name calls something that isn't a book, as compared (<see cref="BookRecords.Simplify"/>).</summary>
    static readonly string[] LooseNames =
    [
        "character sheet", "character sheets", "char sheet", "pregens", "pregenerated characters", "handout", "handouts", "battle map", "battle maps",
        "battlemap", "battlemaps", "map pack", "poster map", "tokens", "pawns", "form fillable", "cheat sheet", "reference sheet",
    ];

    /// <summary>The most pages of a file too short to be a book, where nothing says what it is.</summary>
    const int LoosePages = 2;

    /// <summary>
    /// Whether a file printing no ISBN or product code is something no catalogue of books lists: a map, a character
    /// sheet, a handout. By its <paramref name="types"/> (their keys) where it has any, the user's or a suggestion,
    /// which then decide either way; else by a page or two (<paramref name="pages"/>), or by what its
    /// <paramref name="titles"/> call it.
    /// </summary>
    public static bool IsNotABook(IReadOnlyCollection<string> types, IEnumerable<string?> titles, int? pages)
    {
        if (types.Count > 0) return types.All(LooseTypes.Contains);
        if (pages is > 0 and <= LoosePages) return true;
        return titles.OfType<string>().Select(t => $" {BookRecords.Simplify(t)} ").Any(t => LooseNames.Any(n => t.Contains($" {n} ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Whether a book was looked for and no catalogue's record is used for it, however that came about: no record, one
    /// waiting in Needs review, one the user refused, or nothing a catalogue asked then could go by. Not one with
    /// nothing to find at all, nor one that isn't a book: asking again would find no more.
    /// </summary>
    public static bool NotFound(string? reason) =>
        reason is not null && reason != NothingAtAll && reason != NotABook && !reason.StartsWith(AskedBefore, StringComparison.Ordinal);

    /// <summary>
    /// Why a book none of <paramref name="catalogues"/> ("Open Library or Google Books") knows wasn't looked up, by
    /// what it was looked up by: its ISBNs, else its product codes, else its title.
    /// </summary>
    public static string NoRecordIn(string catalogues, bool byCode = false, bool byTitle = false) =>
        $"{(byTitle ? NoTitleRecordBefore : byCode ? NoCodeRecordBefore : NoRecordBefore)}{catalogues}.";

    /// <summary>Whether a book was looked up and no catalogue had a record of it, by its ISBN, product code or title.</summary>
    public static bool Missed(string? reason) =>
        reason is not null && (reason.StartsWith(NoRecordBefore, StringComparison.Ordinal) || reason.StartsWith(NoCodeRecordBefore, StringComparison.Ordinal)
            || reason.StartsWith(NoTitleRecordBefore, StringComparison.Ordinal));

    /// <summary>
    /// Whether a book was passed over for printing no ISBN, before product codes were looked up too: it's looked at
    /// again, and one printing neither is passed over again in words that say so.
    /// </summary>
    public static bool HadNoIsbn(string? reason) => reason == "It prints no ISBN to look up.";

    /// <summary>What a book's details say for a lookup no catalogue had a record for.</summary>
    public const string NotFoundOnline = "Unable to find in any online listing.";

    /// <summary>
    /// The words a lookup's reason is shown in. A miss is stored naming the catalogues asked, which <see cref="AskedOfFewer"/>
    /// needs, but the reader only needs to know nothing was found.
    /// </summary>
    public static string Shown(string reason) => Missed(reason) ? NotFoundOnline : reason;

    /// <summary>
    /// The lines a book belongs to, for a catalogue searched by name that takes records only by ISBN or code: the folders
    /// it's in (<paramref name="folderHint"/>, "Gull Rock Saga / Monsters"), nearest first, whose names its
    /// <paramref name="front"/> pages print, as a publisher's credits page names its line. A file's name often leaves
    /// the line out ("Monster Tome Vol1"), and a catalogue may name every book in it after the line. Only a name
    /// the book prints is sent, so a folder named for the user's own reasons isn't.
    /// </summary>
    public static IReadOnlyList<string> Lines(string? folderHint, IEnumerable<string?> front)
    {
        if (string.IsNullOrWhiteSpace(folderHint)) return [];
        var printed = $" {string.Join(" ", front.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => BookRecords.Simplify(p!)))} ";
        if (printed.Length <= 2) return [];
        bool Printed(string name) => BookRecords.Simplify(name) is { Length: >= MinLine } simple && printed.Contains($" {simple} ", StringComparison.Ordinal);
        // As a title would read it ("Pathfinder_Society" as "Pathfinder Society"), or as written where that splits a
        // name the book prints whole ("GullRock").
        return [.. folderHint.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse()
            .Select(folder => new[] { DisplayTitle.FromFolderName(folder), folder }.FirstOrDefault(Printed))
            .OfType<string>()
            .DistinctBy(BookRecords.Simplify, StringComparer.Ordinal)];
    }

    /// <summary>The shortest line name searched for: a shorter one finds too many books to read.</summary>
    const int MinLine = 4;

    /// <summary>
    /// The lines the records of the books beside one name (<paramref name="titles"/>, the records the other files in its
    /// folder use): the part of each title before a colon, as RPGGeek names a line's books "Line: Book 2. Grimoire", so a
    /// book whose file and folder don't name its line is searched for among its neighbours'.
    /// </summary>
    public static IReadOnlyList<string> LinesBeside(IEnumerable<string> titles) =>
        [.. titles.Where(t => t.Contains(':', StringComparison.Ordinal)).Select(t => t.Split(':', 2)[0].Trim())
            .Where(l => BookRecords.Simplify(l).Length >= MinLine).DistinctBy(BookRecords.Simplify, StringComparer.Ordinal)];

    /// <summary>Lines on a cover read longer than this are a blurb, not a title.</summary>
    const int LongestCoverLine = 80;

    /// <summary>
    /// What a book's cover (<paramref name="cover"/>, its first page's text) prints as its title: its first lines that
    /// read as words, not a scan's noise ("TALES OF THE GULL ROCK SAGA / MONSTER TOME"). <c>Title</c> is the first two
    /// together, for a search by title; <c>Lines</c> those of three words or more, for a search by line, since a cover
    /// often prints its line above the book's name. Nothing for a page that reads as none.
    /// </summary>
    public static (string? Title, IReadOnlyList<string> Lines) Cover(string? cover)
    {
        if (string.IsNullOrWhiteSpace(cover)) return (null, []);
        List<string> read = [.. cover.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(ReadsAsWords).Take(3)];
        if (read.Count == 0) return (null, []);
        var title = string.Join(" ", read.Take(2));
        return (title.Length <= 300 ? title : null,
            [.. read.Where(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3).DistinctBy(BookRecords.Simplify, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Whether a line reads as words: no stray symbols, no lone letters but "a", "A" and "I", and half its words or
    /// more of three letters, which a scan's noise ("ra | Meme lad im") seldom manages.
    /// </summary>
    static bool ReadsAsWords(string line)
    {
        if (line.Length is < MinLine or > LongestCoverLine) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = 0;
        foreach (var raw in words)
        {
            if (raw is "-" or "\u2013" or "\u2014" or "&") continue;
            var word = raw.Trim(',', '.', ':', ';', '!', '?', '\'', '"', '\u2018', '\u2019', '\u201C', '\u201D', '(', ')');
            if (word.Length == 0 || !(word.All(c => char.IsLetter(c) || c is '\'' or '\u2019' or '-') || word.All(char.IsDigit))) return false;
            if (word.Length == 1 && char.IsLetter(word[0]) && word is not ("a" or "A" or "I")) return false;
            if (word.Count(char.IsLetter) >= 3) letters++;
        }
        return letters > 0 && letters * 2 >= words.Length;
    }

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
/// again for another file with its new title and publisher. A record the user gives by its page's address
/// (<see cref="LookupLinks"/>) is the book's whatever a lookup finds, and one given on another PC is read here by its
/// id. Runs in its own lane, and waits with
/// a notice while the catalogue can't be reached. A book is asked again once there are more catalogues to ask.
/// </summary>
public sealed class LookUpStage(BookLookups lookups, IndexQueries queries, IndexStore index, EntryStore entries, LookupRecordStore records, MetadataStore metadata,
    MetadataProjector projector, VersionFinder versions, ElsewhereLookups? elsewhere = null) : IRevisitedStage
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

    /// <summary>A book no catalogue found is asked again once its card has a new title, the AI's or the user's.</summary>
    public event EventHandler<IReadOnlyCollection<long>>? RunAgainFor
    {
        add => projector.Retitled += value;
        remove => projector.Retitled -= value;
    }

    public bool RunsAgainFor(string? reason) => BookLookups.Missed(reason) || reason == BookLookups.NotABook;

    public async Task<Func<string?, bool>> RunAgainAsync(CancellationToken ct)
    {
        await lookups.Catalogue.RefreshAsync(ct);
        // The books with no file are asked alongside, at the start and whenever there is somewhere more to ask.
        elsewhere?.Start();
        var catalogues = lookups.Catalogue.Name;
        var byTitle = lookups.Catalogue.FindsByTitleAlone;
        return reason => BookLookups.AskedOfFewer(reason, catalogues) || BookLookups.SetAside(reason) || BookLookups.HadNoIsbn(reason)
            || (byTitle && reason == BookLookups.NothingToLookUp) || reason == BookLookups.NotABook;
    }

    /// <summary>
    /// A book the user has just said which record is of, by its page's address, is run again at once: a record given
    /// on another PC is read here by its id, and the lookup says what the book uses now.
    /// </summary>
    public event EventHandler<IReadOnlyCollection<string>>? RunAgainForFiles
    {
        add => records.Linked += value;
        remove => records.Linked -= value;
    }

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var isbns = await queries.GetIsbnsAsync(job.DocumentId, ct);
        var codes = await queries.GetProductCodesAsync(job.DocumentId, ct);
        var pages = await queries.GetAllPageTextsAsync(job.DocumentId, ct);
        List<string?> front = [.. pages.Where(t => !string.IsNullOrWhiteSpace(t)).Take(BookIdentifiers.FrontPages)];
        var byTitle = isbns.Count == 0 && codes.Count == 0;
        // A book found by title has to print it, and an image prints nothing.
        var nothing = !byTitle ? null : front.Count == 0 ? BookLookups.NothingAtAll : !lookups.Catalogue.FindsByTitleAlone ? BookLookups.NothingToLookUp : null;
        // A record the user said is this book's is its record whatever it prints, as one given by its page's address.
        var kept = await records.GetKeptAsync(job.ContentHash, ct);
        if (nothing is not null && kept.Count == 0) return new StageOutcome.Done(StageStatus.Skipped, [], nothing);

        var entry = await entries.GetEntryAsync(job.DocumentId, ct);
        var title = await queries.GetTitleAsync(job.DocumentId, ct);
        var embedded = (await queries.GetEmbeddedInfoAsync(job.DocumentId, ct))?.Title;
        var shown = await queries.GetBookTitleAsync(job.DocumentId, ct);
        var pageCount = await queries.GetPageCountAsync(job.DocumentId, ct);
        if (nothing is null && byTitle && kept.Count == 0)
        {
            // A map or a character sheet is in no catalogue of books: not asked about, and not counted as not found.
            List<string> types = entry is null ? [] : [.. (await metadata.GetAsync(entry.EntryId, ct)).Compute()[MetadataFields.Types].Values.Select(v => v.Value)];
            if (BookLookups.IsNotABook(types, [shown, title], pageCount)) return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NotABook);
        }

        var cover = BookLookups.Cover(pages.Count > 0 ? pages[0] : null);
        // Its card's title first, which may be the user's or the AI's and read better than the run-together words of a file name.
        IReadOnlyList<string> titles = [.. BookRecords.Searches(new[] { shown, title, embedded, cover.Title }.OfType<string>().Where(t => t.Trim().Length <= 300), 4)];
        var beside = await records.GetUsedAsync(await queries.GetHashesBesideAsync(job.DocumentId, MostBeside, ct), ct);
        // The folders it's in, then what the books beside it were found as, then what its cover prints above its name.
        string[] shared = [
            .. BookLookups.Lines(await queries.GetFolderHintAsync(job.DocumentId, ct), front),
            .. BookLookups.LinesBeside(beside.Select(r => r.Title).Distinct(StringComparer.Ordinal))];
        IReadOnlyList<string> lines = [.. BookRecords.Searches([.. shared, .. cover.Lines], 4)];
        // The games most of the books beside it belong to, whose other books it may be.
        IReadOnlyList<string> families = [.. beside.SelectMany(r => r.Families ?? []).GroupBy(f => f, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(MostFamilies)];

        List<BookRecord> given = [];
        CatalogueAnswer? answer = null;
        CatalogueItem? sure = null;
        IReadOnlyList<CatalogueItem> asked = [];
        List<BookRecord> picked = [];
        try
        {
            // One said to be this book's on another PC is read by its id: no lookup here may ever give it.
            foreach (var (source, key, _) in kept.Where(k => !k.Read))
                if (await lookups.Catalogue.ReadAsync(source, key, ct) is { } read) given.Add(read);
            if (nothing is null)
            {
                answer = await lookups.Catalogue.AskAsync(isbns, titles, codes, lines, families, ct);
                // What its searches found by name is the book's only if the book says so, which only its own pages can.
                var refused = await records.GetRefusedAsync(job.ContentHash, ct);
                var open = (answer.Candidates ?? []).Where(c => !kept.Any(k => k.Source == c.Source) && !answer.Records.Any(r => r.Source == c.Source)
                    && !refused.Contains((c.Source, c.Key)));
                // Only a folder's name or its neighbours' is known to be a line's: a line of its cover may be the book's own name.
                (sure, asked) = BookRecords.Pick(open, shared, [.. titles], front.FirstOrDefault(), front);
                foreach (var item in sure is null ? asked : [sure])
                    if (await lookups.Catalogue.ReadAsync(item.Source, item.Key, ct) is { } read) picked.Add(read);
            }
        }
        catch (CatalogueUnavailableException ex)
        {
            return new StageOutcome.Unavailable(ex.Message);
        }

        SavedLookup saved;
        if (answer is null) saved = await records.KeepAsync(job.ContentHash, given, ct);
        else
        {
            // A record found by an identifier must name the book where it names itself, and one found by title alone must
            // have its title printed in it, or it's dropped; it's used when something else agrees, and asked about if not.
            List<string?> places = [.. front, title, embedded];
            // Once the user has said which of a catalogue's records the book is, its others aren't the book's: they'd
            // fill the fields that record leaves empty from another book.
            bool Settled(BookRecord r) => kept.Any(k => k.Source == r.Source && k.Key != r.Key && (k.Read || given.Any(g => g.Source == k.Source && g.Key == k.Key)));
            bool Is(BookRecord r, CatalogueItem? item) => item is not null && item.Source == r.Source && item.Key == r.Key;
            // One named exactly as the book's card or file is, though its pages don't print the name, as a scan's may not: asked about.
            bool GoesBy(BookRecord r) => titles.Any(t => BookRecords.Simplify(t) == BookRecords.Simplify(r.Title));
            List<BookRecord> found = [.. given, .. answer.Records.Concat(picked).Where(r => !given.Any(g => g.Source == r.Source && g.Key == r.Key) && !Settled(r)
                && (!BookRecords.ByTitle(r) || picked.Contains(r) || BookRecords.IsNamedWholeIn(r, front) || GoesBy(r)))];
            // A record no catalogue gives any more goes with this, unless the user answered about it: it isn't worth asking about.
            saved = await records.SaveAsync(job.ContentHash, found,
                r => !BookRecords.ByTitle(r) ? BookRecords.IsNamedIn(r, places)
                    : (Is(r, sure) || (!picked.Contains(r) && BookRecords.IsNamedWholeIn(r, front))) && BookRecords.Agrees(r, front, pageCount), ct);
        }
        if (entry is not null)
        {
            var (changed, grew) = await records.ApplyAsync(entry.EntryId, job.ContentHash, ct);
            if (grew) await projector.ProjectVocabularyAsync(ct);
            // Projected even when no value changed: Found online follows which catalogues' records the book uses.
            await projector.ProjectAsync([entry.EntryId], ct);
            // Match looked for a book of the same title and publisher before the record named this one properly.
            if (changed && saved.Used.Count > 0) await versions.ProposeByTitleAndPublisherAsync(job.DocumentId, ct);
        }
        // Not looked up, so it's asked once it can be, but it has the record the user gave.
        if (answer is null) return new StageOutcome.Done(saved.Used.Count > 0 ? StageStatus.Complete : StageStatus.Skipped, [], nothing);
        await index.SetLookupSearchAsync(job.DocumentId, LookupSearch.Write(new LookupSearch(isbns, codes, titles, lines, families.Count, answer.AskedOf,
            [.. saved.Used.Select(Named)], answer.Candidates?.Count ?? 0, [.. saved.Waiting.Select(Named)], [.. saved.Refused.Select(Named)])), ct);
        var where = BookLookups.AskedOf(answer.AskedOf);
        if (saved.Used.Count > 0) return new StageOutcome.Done(StageStatus.Complete, [], where);
        if (saved.Waiting.Count == 0 && saved.Refused.Count == 0)
            return new StageOutcome.Done(StageStatus.Skipped, [], BookLookups.NoRecordIn(answer.AskedOf, byCode: isbns.Count == 0, byTitle: byTitle));
        if (saved.Waiting.Count > 1)
            return new StageOutcome.Done(StageStatus.Skipped, [],
                $"{saved.Waiting.Count} records might be this book's, so Needs review asks which it is. {where}");
        if (saved.Waiting.Count > 0)
        {
            var record = saved.Waiting[0];
            return new StageOutcome.Done(StageStatus.Skipped, [], BookRecords.ByTitle(record)
                ? $"{record.Source}'s record of “{record.Title}” goes by its name, but nothing else says it's this book, so Needs review asks whether it is. {where}"
                : $"{record.Source}'s record for its {BookRecords.FoundBy(record)} is of “{record.Title}”, which isn't printed in it, so Needs review asks whether it's this book. {where}");
        }
        var no = saved.Refused[0];
        return new StageOutcome.Done(StageStatus.Skipped, [],
            $"You said {no.Source}'s record for its {BookRecords.FoundBy(no)}, of “{no.Title}”, is of another book. {where}");
    }

    static string Named(BookRecord record) => $"{record.Source}: {record.Title}";

    /// <summary>The books beside one whose records are read for the line they name: enough for a folder of a line's books.</summary>
    const int MostBeside = 200;

    /// <summary>The games asked about for one book.</summary>
    const int MostFamilies = 2;
}

/// <summary>
/// What a book's last lookup searched for and what came of it, kept with the book for its details to show: the ISBNs
/// and product codes it prints, the titles and lines searched by name, how many games of the books beside it were
/// looked through (<see cref="Games"/>), where it was asked, the records it uses (<see cref="Found"/>, "RPGGeek: its
/// name"), how many items the searches found by name alone (<see cref="Candidates"/>), and the records Needs review
/// asks about (<see cref="Waiting"/>) or the user said are another book's (<see cref="Refused"/>).
/// </summary>
public sealed record LookupSearch(IReadOnlyList<string> Isbns, IReadOnlyList<string> Codes, IReadOnlyList<string> Titles, IReadOnlyList<string> Lines,
    int Games, string AskedOf, IReadOnlyList<string> Found, int Candidates, IReadOnlyList<string> Waiting, IReadOnlyList<string> Refused)
{
    static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Write(LookupSearch search) => System.Text.Json.JsonSerializer.Serialize(search, Json);

    public static LookupSearch? Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<LookupSearch>(json, Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The heading the details show it under.</summary>
    public const string Heading = "Online lookup";

    static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"“{v}”"));

    /// <summary>What the details say of it, a labelled line each, leaving out what there was none of.</summary>
    public IReadOnlyList<(string Label, string Value)> Facts()
    {
        var lines = new List<(string, string)>();
        var by = new List<string>();
        if (Isbns.Count > 0) by.Add($"ISBN {string.Join(", ", Isbns)}");
        if (Codes.Count > 0) by.Add($"product code {string.Join(", ", Codes)}");
        lines.Add(("Looked up by", by.Count > 0 ? string.Join("; ", by) : "Its title, since it prints no ISBN or product code"));
        if (Titles.Count > 0) lines.Add(("Titles searched", Quoted(Titles)));
        if (Lines.Count > 0) lines.Add(("Lines searched", Quoted(Lines)));
        if (Games > 0) lines.Add(("Also looked in", Games == 1 ? "The game the books beside it belong to" : "The games the books beside it belong to"));
        lines.Add(("Asked", AskedOf));
        if (Candidates > 0) lines.Add(("Found by name", $"{Candidates:N0} {(Candidates == 1 ? "item" : "items")}, checked against what the book prints"));
        if (Found.Count > 0) lines.Add(("Uses", string.Join("; ", Found)));
        if (Waiting.Count > 0) lines.Add(("Needs review asks about", string.Join("; ", Waiting)));
        if (Refused.Count > 0) lines.Add(("You said not this book", string.Join("; ", Refused)));
        if (Found.Count + Waiting.Count + Refused.Count == 0) lines.Add(("Result", BookLookups.NotFoundOnline));
        return lines;
    }
}
