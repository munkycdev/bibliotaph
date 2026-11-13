using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bibliotaph.Core.Metadata;

/// <summary>
/// A book's record in an online catalogue, found by one of its ISBNs, or by a product code it prints
/// (<paramref name="Isbn"/> is then that code), or by its title alone (<paramref name="Isbn"/> is then empty; see
/// <see cref="BookRecords.FoundBy"/>): <paramref name="Source"/> names the catalogue ("Open Library"),
/// <paramref name="Key"/> is the record's own id there, and <paramref name="Published"/> is the date as the catalogue
/// writes it ("1985", "March 2019"). <paramref name="Values"/> are what the catalogue files it under, in its own words,
/// for a catalogue that knows games (RPGGeek's systems, categories, genres, settings and series, AdventureLookup's
/// editions, environments and levels), and who drew it and the mechanics it uses. <paramref name="Pages"/> is its page
/// count, where the catalogue gives one, to tell a record found by title alone is this book's. <paramref name="Families"/>
/// are the catalogue's own ids of the games it belongs to (RPGGeek's), by which the books beside it can be found.
/// </summary>
public sealed record BookRecord(
    string Source, string Key, string Isbn, string Title, IReadOnlyList<string> Authors, IReadOnlyList<string> Publishers, string? Published,
    IReadOnlyList<CatalogueValue>? Values = null, int? Pages = null, IReadOnlyList<string>? Families = null);

/// <summary>
/// Something a catalogue's search found that may be the book, by its id there and what the catalogue calls it: not a
/// record yet, since only the book's own pages can say whether the name is its own (<see cref="BookRecords.Pick"/>).
/// </summary>
public sealed record CatalogueItem(string Source, string Key, string Name);

/// <summary>How well a catalogue's name for an item fits a book (<see cref="BookRecords.Fit"/>).</summary>
public enum NameFit
{
    /// <summary>The book doesn't print the name.</summary>
    None,

    /// <summary>The book prints the name's words, or prints the name somewhere past its cover: worth asking about.</summary>
    Partly,

    /// <summary>The book goes by the name: its title or its cover prints every part of it.</summary>
    Named,
}

/// <summary>
/// Something a catalogue files a book under, in its own words ("Fantasy (High Fantasy)"), by the key of the field it
/// would fill: system, type, theme, setting or series, or artists and mechanics.
/// </summary>
public sealed record CatalogueValue(string Field, string Value);

/// <summary>
/// A term a catalogue's record files the book under: one of the vocabulary's (<paramref name="Term"/>), or, when none
/// of them is named, <paramref name="Label"/> to propose as a new one. <paramref name="Quote"/> names the record.
/// </summary>
public sealed record CatalogueTerm(MetadataField Field, Term? Term, string Label, string Quote);

/// <summary>Turning a catalogue's record into suggestions, once it is known to be this book's.</summary>
public static partial class BookRecords
{
    [GeneratedRegex(@"(?<!\d)(1[89]\d\d|20\d\d)(?!\d)")]
    private static partial Regex YearIn();

    /// <summary>
    /// Whether every part of the record's title, each side of a colon, is printed in <paramref name="places"/>: how a
    /// record found by its title alone must name the book, since a line's name before the colon ("Line: Game Master's
    /// Screen") is printed in every book of the line.
    /// </summary>
    public static bool IsNamedWholeIn(BookRecord record, IEnumerable<string?> places)
    {
        var parts = record.Title.Split(':').Select(Simplify).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return false;
        var printed = $" {string.Join(" ", places.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Simplify(p!)))} ";
        return parts.All(p => printed.Contains($" {p} ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The parts of a catalogue's name for an item that are the book's own, each side of a colon, as compared: without
    /// the parts that only name one of <paramref name="lines"/> ("Gull Rock Saga" in "Gull Rock Saga: Book 2. Spells"),
    /// which every book of the line prints. None for an item named as its line is.
    /// </summary>
    public static IReadOnlyList<string> OwnParts(string name, IEnumerable<string> lines)
    {
        var known = lines.Select(Simplify).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        return [.. name.Split(':').Select(Simplify).Where(p => p.Length > 0 && !known.Contains(p))];
    }

    /// <summary>Words that say nothing of which book a name is.</summary>
    static readonly HashSet<string> Small = new(StringComparer.Ordinal) { "the", "of", "and", "a", "an", "to", "in", "for", "on", "at", "by", "with" };

    /// <summary>
    /// How well <paramref name="name"/>, a catalogue's for an item of one of the book's <paramref name="lines"/>, fits
    /// the book: <see cref="NameFit.Named"/> when each of its own parts (<see cref="OwnParts"/>) is printed whole in
    /// one of <paramref name="titles"/> (its card's, its file's) or on its <paramref name="cover"/>;
    /// <see cref="NameFit.Partly"/> when they're printed whole only further into its <paramref name="front"/> pages,
    /// where a book also names the others of its line, or when the titles and cover print every word of them but not
    /// together ("SPELLS" over "BOOK 2").
    /// </summary>
    public static NameFit Fit(string name, IEnumerable<string> lines, IEnumerable<string?> titles, string? cover, IEnumerable<string?> front)
    {
        var parts = OwnParts(name, lines);
        if (parts.Count == 0) return NameFit.None;
        var named = $" {string.Join(" | ", titles.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Simplify(p!)))} ";
        // A cover's lines apart, so words from two of them don't make a name; a name of one word must be a line of its
        // own there, since a blurb holds many a word.
        var coverLines = (cover ?? "").Split('\n').Select(Simplify).Where(l => l.Length > 0).ToList();
        var near = $"{named}| {string.Join(" | ", coverLines)} ";
        bool OnCover(string part) => part.Contains(' ', StringComparison.Ordinal) ? coverLines.Any(l => $" {l} ".Contains($" {part} ", StringComparison.Ordinal)) : coverLines.Contains(part);
        if (parts.All(p => named.Contains($" {p} ", StringComparison.Ordinal) || OnCover(p))) return NameFit.Named;
        var far = $" {string.Join(" | ", front.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Simplify(p!)))} ";
        var words = parts.SelectMany(p => p.Split(' ')).Where(w => !Small.Contains(w)).Distinct(StringComparer.Ordinal).ToList();
        // One word is too little to go on past the cover: "Bestiary" is in many a book's contents.
        if (words.Count < 2 || !words.Any(w => w.Length >= MinNameLength && w.Any(char.IsLetter))) return NameFit.None;
        if (parts.All(p => far.Contains($" {p} ", StringComparison.Ordinal))) return NameFit.Partly;
        var printed = near.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return words.All(printed.Contains) ? NameFit.Partly : NameFit.None;
    }

    /// <summary>The most items asked about for one book: more than that fit and the name says too little to ask.</summary>
    public const int MostAsked = 3;

    /// <summary>
    /// Which of <paramref name="items"/>, what a catalogue's searches of the book's lines found, are worth reading as
    /// its record. <c>Sure</c> is the one item the book goes by the name of; <c>Ask</c>, with none, are the two or
    /// three it might be, for Needs review to ask about: several it goes by the name of, or else those it prints only
    /// in part. An item whose own name is held whole in a longer one's that fits as well gives way to it ("Bestiary"
    /// to "Bestiary 2"). More than <see cref="MostAsked"/> and none is asked about.
    /// </summary>
    public static (CatalogueItem? Sure, IReadOnlyList<CatalogueItem> Ask) Pick(IEnumerable<CatalogueItem> items, IReadOnlyList<string> lines,
        IReadOnlyList<string?> titles, string? cover, IReadOnlyList<string?> front)
    {
        var fits = items.DistinctBy(i => (i.Source, i.Key))
            .Select(i => (Item: i, Fit: Fit(i.Name, lines, titles, cover, front), Own: string.Join(" ", OwnParts(i.Name, lines))))
            .Where(i => i.Fit != NameFit.None).ToList();
        foreach (var fit in new[] { NameFit.Named, NameFit.Partly })
        {
            var same = fits.Where(i => i.Fit == fit).ToList();
            List<CatalogueItem> best = [.. same.Where(i => !same.Any(o => o.Own.Length > i.Own.Length && $" {o.Own} ".Contains($" {i.Own} ", StringComparison.Ordinal)))
                .Select(i => i.Item)];
            if (best.Count == 0) continue;
            if (best.Count == 1 && fit == NameFit.Named) return (best[0], []);
            return (null, best.Count <= MostAsked ? best : []);
        }
        return (null, []);
    }

    /// <summary>
    /// Whether the record's title is printed in the book: in one of <paramref name="places"/>, the text of its front
    /// pages and the title its file gives, ignoring case, punctuation and spacing. Only the part before a colon has to
    /// be there, since a catalogue often adds a subtitle the cover doesn't print. An ISBN printed wrongly, or reused for
    /// another book, names a record whose title isn't in the book, so its record isn't used.
    /// </summary>
    public static bool IsNamedIn(BookRecord record, IEnumerable<string?> places)
    {
        var title = Simplify(record.Title.Split(':', 2)[0]);
        if (title.Length == 0) return false;
        var wanted = $" {title} ";
        return places.Any(p => !string.IsNullOrWhiteSpace(p) && $" {Simplify(p)} ".Contains(wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// What the records suggest: a title, authors, first publisher, year, series, levels, artists, mechanics, monsters and items, each field from the first of
    /// <paramref name="records"/> that gives it, so the catalogue the user prefers wins field by field and the next fills
    /// what it lacks. A library catalogue's subjects are a librarian's shelving, not a game's system or type, so they
    /// aren't used; a game catalogue's terms are, through <see cref="Terms"/>.
    /// </summary>
    public static IReadOnlyList<MetadataProposal> Proposals(IReadOnlyList<BookRecord> records)
    {
        var proposals = new List<MetadataProposal>();
        void Add(MetadataField field, Func<BookRecord, IEnumerable<string?>> values)
        {
            foreach (var record in records)
            {
                var given = values(record).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
                if (given.Count == 0) continue;
                proposals.AddRange(given.Select(v => new MetadataProposal(field, v, AssertionOrigin.Lookup, Quote(record))));
                return;
            }
        }

        Add(MetadataFields.Title, r => [r.Title]);
        Add(MetadataFields.Authors, r => r.Authors);
        Add(MetadataFields.Publisher, r => [r.Publishers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))]);
        Add(MetadataFields.Year, r => [Year(r.Published)]);
        Add(MetadataFields.Series, r => [ValuesOf(r, MetadataFields.Series).FirstOrDefault(v => Simplify(v) is not ("misc" or "miscellaneous"))]);
        Add(MetadataFields.Levels, r => [ValuesOf(r, MetadataFields.Levels).FirstOrDefault()]);
        Add(MetadataFields.Artists, r => ValuesOf(r, MetadataFields.Artists));
        Add(MetadataFields.Mechanics, r => ValuesOf(r, MetadataFields.Mechanics));
        Add(MetadataFields.Monsters, r => ValuesOf(r, MetadataFields.Monsters));
        Add(MetadataFields.Items, r => ValuesOf(r, MetadataFields.Items));
        return proposals;
    }

    /// <summary>
    /// What the record was found by, as a reason or card says it: "ISBN", "product code" for one found by a code it
    /// prints, or "title" for one found by its title alone.
    /// </summary>
    public static string FoundBy(BookRecord record) =>
        ByTitle(record) ? "title" : Isbn.Normalize(record.Isbn) is not null ? "ISBN" : "product code";

    /// <summary>Whether the record was found by the book's title alone, by a catalogue that knows no ISBNs.</summary>
    public static bool ByTitle(BookRecord record) => string.IsNullOrEmpty(record.Isbn);

    /// <summary>
    /// Whether something besides the title tells a record found by title alone is this book's: one of its authors or
    /// publishers printed on <paramref name="front"/>, the book's front pages, or its page count within
    /// <see cref="PagesApart"/> of the file's <paramref name="pages"/>. A short title is shared by many books, so the
    /// title alone isn't enough.
    /// </summary>
    public static bool Agrees(BookRecord record, IEnumerable<string?> front, int? pages)
    {
        if (record.Pages is { } given && pages is { } counted && Math.Abs(given - counted) <= PagesApart) return true;
        var printed = string.Join(" ", front.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Simplify(p!)));
        if (printed.Length == 0) return false;
        printed = $" {printed} ";
        return record.Authors.Concat(record.Publishers).Select(n => Simplify(n)).Where(n => n.Length >= MinNameLength)
            .Any(n => printed.Contains($" {n} ", StringComparison.Ordinal));
    }

    /// <summary>How far a record's page count may be from the file's: a cover or an ad page more or less.</summary>
    public const int PagesApart = 2;

    /// <summary>Names shorter than this ("TSR" is fine, "Al" isn't) are too likely printed by chance.</summary>
    const int MinNameLength = 3;

    /// <summary>Where a suggestion came from, as a card shows it: the record by the title it gives, which says more than its ISBN.</summary>
    static string Quote(BookRecord record) => $"{record.Source}'s record of {record.Title}";

    /// <summary>
    /// Whether <paramref name="field"/> shows anything from <paramref name="source"/>'s records, a value or another
    /// suggestion: the details credit RPGGeek where its data is shown, as BoardGameGeek's terms ask.
    /// </summary>
    public static bool Shows(EffectiveField field, string source) =>
        field.Values.Concat(field.Alternatives).SelectMany(v => v.Support)
            .Any(c => c.Origin == AssertionOrigin.Lookup && c.Quote?.StartsWith($"{source}'s record of ", StringComparison.Ordinal) == true);

    static IEnumerable<string> ValuesOf(BookRecord record, MetadataField field) =>
        (record.Values ?? []).Where(v => v.Field == field.Key && !string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Value.Trim());

    /// <summary>Genres so wide that nearly every book has them, so as a theme they'd say nothing.</summary>
    static readonly HashSet<string> TooWide = [.. new[] { "Fantasy", "Generic / Universal", "Action / Adventure" }.Select(MetadataText.Normalize)];

    /// <summary>
    /// The terms the records file the book under: its system and edition, types, themes, settings, environments and what it includes, each field from
    /// the first record that gives it. A catalogue's words ("Dungeons &amp; Dragons (3.5 Edition)") are matched to the
    /// vocabulary as a whole, then by the part before the bracket; a system also by any system or edition named in it,
    /// and its edition by the bracket read with the system's name ("Pathfinder" and "2nd Edition"). A name none of the
    /// vocabulary's terms stands for comes back to propose as a new term, by the part before its bracket, except for a
    /// type, since the types are a fixed list, and a genre too wide to tell books apart.
    /// </summary>
    public static IReadOnlyList<CatalogueTerm> Terms(IReadOnlyList<BookRecord> records, Vocabulary vocabulary)
    {
        var terms = new List<CatalogueTerm>();
        foreach (var field in new[] { MetadataFields.System, MetadataFields.Types, MetadataFields.Themes, MetadataFields.Settings, MetadataFields.Environments, MetadataFields.Includes })
        {
            if (records.FirstOrDefault(r => ValuesOf(r, field).Any()) is not { } record) continue;
            var quote = Quote(record);
            foreach (var value in ValuesOf(record, field))
                terms.AddRange(field == MetadataFields.System ? SystemTerms(value, vocabulary, quote) : FieldTerms(field, value, vocabulary, quote));
        }
        return [.. terms.DistinctBy(t => (t.Field.Key, t.Term?.Key, t.Term is null ? MetadataText.Normalize(t.Label) : null))];
    }

    static IEnumerable<CatalogueTerm> FieldTerms(MetadataField field, string value, Vocabulary vocabulary, string quote)
    {
        var vocabularyName = field.Vocabulary!;
        var (head, _) = Split(value);
        var found = new[] { vocabulary.Resolve(vocabularyName, value), vocabulary.Resolve(vocabularyName, head) }.OfType<Term>().Distinct().ToList();
        if (found.Count > 0) return found.Select(t => new CatalogueTerm(field, t, t.Label, quote));
        if (field == MetadataFields.Types || field == MetadataFields.Includes || head.Length == 0 || TooWide.Contains(MetadataText.Normalize(head))) return [];
        return [new CatalogueTerm(field, null, head, quote)];
    }

    static IEnumerable<CatalogueTerm> SystemTerms(string value, Vocabulary vocabulary, string quote)
    {
        var (head, brackets) = Split(value);
        var edition = vocabulary.Resolve("edition", value);
        var system = vocabulary.Resolve("system", value) ?? vocabulary.Resolve("system", head);
        if (system is null && edition is null)
        {
            var named = vocabulary.Match(value).Select(m => m.Term).Where(t => t.Vocabulary is "system" or "edition").ToList();
            system = named.FirstOrDefault(t => t.Vocabulary == "system");
            edition = named.FirstOrDefault(t => t.Vocabulary == "edition" && (system is null || t.ParentKey == system.Key));
        }
        if (edition?.ParentKey is { } parent && system is null) system = vocabulary.Find("system", parent);
        if (system is null)
            return head.Length == 0 || TooWide.Contains(MetadataText.Normalize(head)) ? [] : [new CatalogueTerm(MetadataFields.System, null, head, quote)];

        if (edition is null || edition.ParentKey != system.Key)
            edition = brackets.SelectMany(b => new[] { system.Label, system.Brief }.Select(name => vocabulary.Resolve("edition", $"{name} {b}")))
                .FirstOrDefault(t => t?.ParentKey == system.Key);
        return edition is null
            ? [new CatalogueTerm(MetadataFields.System, system, system.Label, quote)]
            : [new CatalogueTerm(MetadataFields.System, system, system.Label, quote), new CatalogueTerm(MetadataFields.Edition, edition, edition.Label, quote)];
    }

    /// <summary>"Call of Cthulhu (7th Edition)": the name before the brackets, and what each bracket holds.</summary>
    static (string Head, IReadOnlyList<string> Brackets) Split(string value)
    {
        var at = value.IndexOf('(', StringComparison.Ordinal);
        return at < 0 ? (value.Trim(), []) : (value[..at].Trim(), [.. Bracketed().Matches(value).Select(m => m.Groups[1].Value.Trim()).Where(b => b.Length > 0)]);
    }

    [GeneratedRegex(@"\(([^()]*)\)")]
    private static partial Regex Bracketed();

    /// <summary>The year in a catalogue's date ("March 2019", "c1985"), or null when it has none.</summary>
    public static string? Year(string? published) =>
        published is not null && YearIn().Match(published) is { Success: true } year ? year.Value : null;

    /// <summary>
    /// The first <paramref name="most"/> of <paramref name="titles"/> worth searching for, once each however it's cased
    /// or punctuated: "The Owlbear Mill" and "the owlbear mill." are one search.
    /// </summary>
    public static IEnumerable<string> Searches(IEnumerable<string> titles, int most) =>
        titles.Select(t => (Title: t.Trim(), Simple: Simplify(t))).Where(t => t.Simple.Length > 0).DistinctBy(t => t.Simple, StringComparer.Ordinal)
            .Take(most).Select(t => t.Title);

    /// <summary>Lower case letters and digits, each run of anything else one space: titles compared as a reader would.</summary>
    public static string Simplify(string text)
    {
        var simple = new StringBuilder(text.Length);
        foreach (var c in MetadataText.UnicodeForm(text, NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) simple.Append(char.ToLowerInvariant(c));
            else if (simple.Length > 0 && simple[^1] != ' ') simple.Append(' ');
        }
        return simple.ToString().Trim();
    }
}
