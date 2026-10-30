using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bibliotaph.Core.Metadata;

/// <summary>
/// A book's record in an online catalogue, found by one of its ISBNs, or by a product code it prints
/// (<paramref name="Isbn"/> is then that code; see <see cref="BookRecords.FoundBy"/>): <paramref name="Source"/> names
/// the catalogue ("Open Library"), <paramref name="Key"/> is the record's own id there, and <paramref name="Published"/>
/// is the date as the catalogue writes it ("1985", "March 2019"). <paramref name="Values"/> are what the catalogue files
/// it under, in its own words, for a catalogue that knows games (RPGGeek's systems, categories, genres, settings and
/// series), and who drew it and the mechanics it uses.
/// </summary>
public sealed record BookRecord(
    string Source, string Key, string Isbn, string Title, IReadOnlyList<string> Authors, IReadOnlyList<string> Publishers, string? Published,
    IReadOnlyList<CatalogueValue>? Values = null);

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
    /// What the records suggest: a title, authors, first publisher, year, series, artists and mechanics, each field from the first of
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
        Add(MetadataFields.Artists, r => ValuesOf(r, MetadataFields.Artists));
        Add(MetadataFields.Mechanics, r => ValuesOf(r, MetadataFields.Mechanics));
        return proposals;
    }

    /// <summary>What the record was found by, as a reason or card says it: "ISBN", or "product code" for one found by a code it prints.</summary>
    public static string FoundBy(BookRecord record) => Isbn.Normalize(record.Isbn) is not null ? "ISBN" : "product code";

    /// <summary>Where a suggestion came from, as a card shows it: the record by the title it gives, which says more than its ISBN.</summary>
    static string Quote(BookRecord record) => $"{record.Source}'s record of {record.Title}";

    static IEnumerable<string> ValuesOf(BookRecord record, MetadataField field) =>
        (record.Values ?? []).Where(v => v.Field == field.Key && !string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Value.Trim());

    /// <summary>Genres so wide that nearly every book has them, so as a theme they'd say nothing.</summary>
    static readonly HashSet<string> TooWide = [.. new[] { "Fantasy", "Generic / Universal", "Action / Adventure" }.Select(MetadataText.Normalize)];

    /// <summary>
    /// The terms the records file the book under: its system and edition, types, themes and settings, each field from
    /// the first record that gives it. A catalogue's words ("Dungeons &amp; Dragons (3.5 Edition)") are matched to the
    /// vocabulary as a whole, then by the part before the bracket; a system also by any system or edition named in it,
    /// and its edition by the bracket read with the system's name ("Pathfinder" and "2nd Edition"). A name none of the
    /// vocabulary's terms stands for comes back to propose as a new term, by the part before its bracket, except for a
    /// type, since the types are a fixed list, and a genre too wide to tell books apart.
    /// </summary>
    public static IReadOnlyList<CatalogueTerm> Terms(IReadOnlyList<BookRecord> records, Vocabulary vocabulary)
    {
        var terms = new List<CatalogueTerm>();
        foreach (var field in new[] { MetadataFields.System, MetadataFields.Types, MetadataFields.Themes, MetadataFields.Settings })
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
        if (field == MetadataFields.Types || head.Length == 0 || TooWide.Contains(MetadataText.Normalize(head))) return [];
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
