using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bibliotaph.Core.Metadata;

/// <summary>
/// A book's record in an online catalogue, found by one of its ISBNs: <paramref name="Source"/> names the catalogue
/// ("Open Library"), <paramref name="Key"/> is the record's own id there, and <paramref name="Published"/> is the date
/// as the catalogue writes it ("1985", "March 2019").
/// </summary>
public sealed record BookRecord(
    string Source, string Key, string Isbn, string Title, IReadOnlyList<string> Authors, IReadOnlyList<string> Publishers, string? Published);

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
    /// What the record suggests: its title, authors, first publisher and year. Nothing else: a catalogue's subjects
    /// are a librarian's shelving, not a game's system or type, so those stay for the rules and the AI.
    /// </summary>
    public static IReadOnlyList<MetadataProposal> Proposals(BookRecord record)
    {
        var quote = $"{record.Source}'s record for ISBN {record.Isbn}";
        var proposals = new List<MetadataProposal>();
        void Add(MetadataField field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) proposals.Add(new MetadataProposal(field, value.Trim(), AssertionOrigin.Lookup, quote));
        }

        Add(MetadataFields.Title, record.Title);
        foreach (var author in record.Authors) Add(MetadataFields.Authors, author);
        Add(MetadataFields.Publisher, record.Publishers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)));
        Add(MetadataFields.Year, Year(record.Published));
        return proposals;
    }

    /// <summary>The year in a catalogue's date ("March 2019", "c1985"), or null when it has none.</summary>
    public static string? Year(string? published) =>
        published is not null && YearIn().Match(published) is { Success: true } year ? year.Value : null;

    /// <summary>Lower case letters and digits, each run of anything else one space.</summary>
    static string Simplify(string text)
    {
        var simple = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) simple.Append(char.ToLowerInvariant(c));
            else if (simple.Length > 0 && simple[^1] != ' ') simple.Append(' ');
        }
        return simple.ToString().Trim();
    }
}
