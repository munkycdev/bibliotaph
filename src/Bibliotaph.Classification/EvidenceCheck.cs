using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Classification;

/// <summary>
/// A claim that passed the evidence check, in stored form: a term's key, a level range, a year, tidy text. A term the
/// vocabulary doesn't know has <see cref="IsNewTerm"/> set and its proposed name as <see cref="Value"/>.
/// </summary>
public sealed record CheckedClaim(MetadataField Field, string Value, string Quote, int PdfPage, bool FromSampling, bool IsNewTerm = false);

/// <summary>A claim the check threw out, and why, for the log and the Test button.</summary>
public sealed record DroppedClaim(ClassifierClaim Claim, string Reason);

public sealed record EvidenceResult(IReadOnlyList<CheckedClaim> Accepted, IReadOnlyList<DroppedClaim> Dropped);

/// <summary>
/// The evidence check (architecture, "Evidence check"; choice 9). A claim stands only if its quote appears in the indexed
/// text of the page it cites, ignoring case, spacing, hyphenation, ligatures, accents, punctuation and quote style; the
/// quote shows the value wherever a value is printed rather than judged (a title, a name, a year, a game system); and
/// the value is the right type, in range and, for a closed field, in the vocabulary or a new term worth proposing.
/// Anything a model makes up, or that a book's text talked it into, has no quote on the page and is dropped (A16).
/// </summary>
public static partial class EvidenceCheck
{
    /// <summary>A quote needs at least this many letters and digits, so "the" or "5e" can't match anywhere.</summary>
    public const int MinQuoteCharacters = 8;

    public const int MaxQuoteLength = 400;

    /// <summary>How much of a quote is kept as evidence.</summary>
    public const int StoredQuoteLength = 300;

    /// <summary>A proposed new term longer than this is a sentence, not a name.</summary>
    const int MaxNewTermWords = 5;

    static readonly Dictionary<string, int> FieldOrder = MetadataFields.All.Select((f, i) => (f.Key, i)).ToDictionary(f => f.Key, f => f.i);

    static readonly HashSet<string> NoAnswer =
    [
        with(StringComparer.Ordinal),
        "unknown", "none", "not stated", "not specified", "unspecified", "not given", "not mentioned", "null", "various", "n a", "na", "tbd",
    ];

    /// <param name="claims">The model's claims, unchecked.</param>
    /// <param name="excerpt">What the model read, which says which pages were sampled.</param>
    /// <param name="pageText">The indexed text of a PDF page, or null when it has none.</param>
    /// <param name="vocabulary">The terms closed fields resolve through.</param>
    public static EvidenceResult Check(IReadOnlyList<ClassifierClaim> claims, Excerpt excerpt, Func<int, string?> pageText, Vocabulary vocabulary)
    {
        var accepted = new List<CheckedClaim>();
        var dropped = new List<DroppedClaim>();
        var comparable = new Dictionary<int, string?>();

        foreach (var claim in claims)
        {
            if (ClassifierPrompt.Find(claim.Field) is not { } field)
            {
                dropped.Add(new(claim, "Not a field the classifier fills."));
                continue;
            }
            var (value, isNew, problem) = ReadValue(field.Field, claim.Value, vocabulary);
            if (problem is not null)
            {
                dropped.Add(new(claim, problem));
                continue;
            }
            if (value is null) continue; // "unknown", said in words
            if (claim.Page is not { } marker)
            {
                dropped.Add(new(claim, "No page given."));
                continue;
            }
            var pdfPage = ClassifierPrompt.PdfPage(marker);
            if (!comparable.TryGetValue(pdfPage, out var page)) comparable[pdfPage] = page = pageText(pdfPage) is { } t ? Comparable(t) : null;
            if (page is null)
            {
                dropped.Add(new(claim, $"Page {marker} has no text."));
                continue;
            }
            var quote = Tidy(claim.Quote ?? "");
            if (quote.Length > MaxQuoteLength)
            {
                dropped.Add(new(claim, "The quote is too long to be a quote."));
                continue;
            }
            if (!Appears(quote, page, out var why))
            {
                dropped.Add(new(claim, why!));
                continue;
            }
            if (!Shows(field.Field, value, isNew, quote, vocabulary))
            {
                dropped.Add(new(claim, "The quote doesn't show that value."));
                continue;
            }
            var sampled = excerpt.Find(pdfPage)?.Part == ExcerptPart.Sampled;
            accepted.Add(new CheckedClaim(field.Field, value, Cut(quote), pdfPage, sampled, isNew));
        }

        return Settle(accepted, dropped, claims, vocabulary);
    }

    /// <summary>
    /// One value per single-value field, or none when the model gave two different ones; at most each field's limit of
    /// values; an edition's system added when the model left it out, and the edition dropped when it contradicts it.
    /// </summary>
    static EvidenceResult Settle(List<CheckedClaim> accepted, List<DroppedClaim> dropped, IReadOnlyList<ClassifierClaim> claims, Vocabulary vocabulary)
    {
        var settled = new List<CheckedClaim>();
        foreach (var group in accepted.GroupBy(c => c.Field))
        {
            var field = group.Key;
            var distinct = group.DistinctBy(c => MetadataValues.Normalize(field, c.Value)).ToList();
            if (!field.Multiple && distinct.Count > 1)
            {
                foreach (var claim in distinct) dropped.Add(new(Original(claim, claims), $"More than one {field.Label.ToLowerInvariant()} given."));
                continue;
            }
            settled.AddRange(distinct.Take(ClassifierPrompt.Find(field.Key)!.MaxValues));
        }

        var edition = settled.FirstOrDefault(c => c.Field == MetadataFields.Edition);
        var parent = edition is null ? null : vocabulary.Find("edition", edition.Value)?.ParentKey;
        if (edition is not null && parent is not null)
        {
            var system = settled.FirstOrDefault(c => c.Field == MetadataFields.System);
            if (system is null) settled.Add(edition with { Field = MetadataFields.System, Value = parent });
            else if (system.Value != parent)
            {
                settled.Remove(edition);
                dropped.Add(new(Original(edition, claims), "The edition belongs to a different game system."));
            }
        }
        return new EvidenceResult([.. settled.OrderBy(c => FieldOrder[c.Field.Key])], dropped);
    }

    static ClassifierClaim Original(CheckedClaim claim, IReadOnlyList<ClassifierClaim> claims) =>
        claims.FirstOrDefault(c => c.Field == claim.Field.Key && c.Page == ClassifierPrompt.Marker(claim.PdfPage))
        ?? new ClassifierClaim(claim.Field.Key, claim.Value, ClassifierPrompt.Marker(claim.PdfPage), claim.Quote);

    /// <summary>The stored form of a value, null for an answer that means unknown, or why it can't be one.</summary>
    internal static (string? Value, bool IsNewTerm, string? Problem) ReadValue(MetadataField field, string text, Vocabulary vocabulary)
    {
        var tidy = Tidy(text);
        var normalized = MetadataText.Normalize(tidy);
        if (normalized.Length == 0 || (NoAnswer.Contains(normalized) && field.Kind != FieldKind.Levels)) return (null, false, null);

        switch (field.Kind)
        {
            case FieldKind.Term:
                if (FindTerm(field, tidy, vocabulary) is { } term) return (term.Key, false, null);
                // A new edition is too easy to get wrong ("5th printing", "Revised"); a new name for anything else is a card.
                if (field == MetadataFields.Edition) return (null, false, "Not an edition in the vocabulary.");
                if (MetadataText.Words(normalized).Length > MaxNewTermWords || !tidy.Any(char.IsLetter))
                    return (null, false, $"“{Cut(tidy, 60)}” doesn't look like a name.");
                return (tidy, true, null);
            case FieldKind.Levels:
            case FieldKind.Year:
                var (parsed, problem) = MetadataValues.Parse(field, tidy);
                return (parsed, false, problem);
            default:
                var limit = field == MetadataFields.Title ? 200 : 120;
                if (tidy.Length > limit) return (null, false, $"Too long for {field.Label.ToLowerInvariant()}.");
                var (value, textProblem) = MetadataValues.Parse(field, tidy);
                if (value is not null && field.Vocabulary is { } names && vocabulary.Resolve(names, value) is { } known) value = known.Label;
                return (value, false, textProblem);
        }
    }

    /// <summary>
    /// The term a name stands for: an exact name or alias, else the one term of the field's vocabulary the text
    /// mentions ("the fifth edition of Dungeons &amp; Dragons" gives D&amp;D for a game system, through its edition).
    /// </summary>
    static Term? FindTerm(MetadataField field, string text, Vocabulary vocabulary)
    {
        var vocabularyName = field.Vocabulary!;
        if (vocabulary.Resolve(vocabularyName, text) is { } exact) return exact;
        var matches = vocabulary.Match(text);
        var same = matches.Where(m => m.Term.Vocabulary == vocabularyName).Select(m => m.Term).DistinctBy(t => t.Key).ToList();
        if (same.Count == 1) return same[0];
        if (same.Count == 0 && vocabularyName == "system")
        {
            var systems = matches.Where(m => m.Term.Vocabulary == "edition" && m.Term.ParentKey is not null)
                .Select(m => vocabulary.Find("system", m.Term.ParentKey!)).OfType<Term>().DistinctBy(t => t.Key).ToList();
            if (systems.Count == 1) return systems[0];
        }
        return null;
    }

    /// <summary>
    /// Whether the quote names the value, for the fields whose values are printed in the book. A true quote next to a
    /// made-up value ("Evil Corp", quoting the copyright line) fails here. Types, themes and environments are judged
    /// from what a passage describes, so for them the quote only has to be on its page.
    /// </summary>
    internal static bool Shows(MetadataField field, string value, bool isNewTerm, string quote, Vocabulary vocabulary)
    {
        if (field == MetadataFields.Types || field == MetadataFields.Themes || field == MetadataFields.Environments) return true;
        if (field == MetadataFields.Levels)
        {
            if (!value.Any(char.IsDigit)) return true; // "n/a": the book says levels don't apply, in its own words
            var numbers = Number().Matches(quote).Select(m => m.Value.TrimStart('0')).ToHashSet(StringComparer.Ordinal);
            return Number().Matches(value).All(m => numbers.Contains(m.Value.TrimStart('0')));
        }
        if (field == MetadataFields.Title) return Mentions(quote, value.Split(':')[0]);
        if (field.Kind != FieldKind.Term || isNewTerm)
            return Mentions(quote, value) || (field.Vocabulary is { } names && vocabulary.Resolve(names, value) is { } known && Names(quote, known, vocabulary));
        return vocabulary.Find(field.Vocabulary!, value) is { } term && (Names(quote, term, vocabulary) || Mentions(quote, term.Label));
    }

    /// <summary>The quote names the term, by any of its names, or a game system through one of its editions.</summary>
    static bool Names(string quote, Term term, Vocabulary vocabulary) =>
        vocabulary.Match(quote.Normalize(NormalizationForm.FormKC)).Any(m =>
            (m.Term.Vocabulary == term.Vocabulary && m.Term.Key == term.Key)
            || (term.Vocabulary == "system" && m.Term.Vocabulary == "edition" && m.Term.ParentKey == term.Key));

    static bool Mentions(string quote, string text)
    {
        var part = Comparable(text);
        return part.Length > 0 && Comparable(quote).Contains(part, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    /// <summary>
    /// Whether a quote is on a page, as text a person would call the same. A quote with an ellipsis matches when each
    /// part long enough to count is on the page, in order.
    /// </summary>
    public static bool Appears(string quote, string comparablePage, out string? why)
    {
        var parts = quote.Split(["...", "…"], StringSplitOptions.RemoveEmptyEntries).Select(Comparable).Where(p => p.Length > 0).ToList();
        if (parts.Sum(p => p.Length) < MinQuoteCharacters || parts.All(p => p.Length < MinQuoteCharacters / 2))
        {
            why = "The quote is too short to check.";
            return false;
        }
        var from = 0;
        foreach (var part in parts)
        {
            var at = comparablePage.IndexOf(part, from, StringComparison.Ordinal);
            if (at < 0)
            {
                why = "The quote isn't on the page it cites.";
                return false;
            }
            from = at + part.Length;
        }
        why = null;
        return true;
    }

    /// <summary>
    /// Text in the form quotes are compared in: compatibility forms (so ligatures are letters), no accents, lower case,
    /// and only letters and digits, so spacing, line-break hyphens, punctuation and quote styles don't count.
    /// </summary>
    public static string Comparable(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    static string Tidy(string text) => MetadataText.Tidy(text);

    static string Cut(string text, int limit = StoredQuoteLength) => text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";
}
