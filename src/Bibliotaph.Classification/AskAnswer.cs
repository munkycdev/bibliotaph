using System.Text;
using System.Text.RegularExpressions;

namespace Bibliotaph.Classification;

/// <summary>What a piece of a checked answer is, and so how it is shown.</summary>
public enum AnswerPartKind
{
    /// <summary>The answer's own words.</summary>
    Text,

    /// <summary>A quotation that is on the page it cites (or, with no page cited, on a page sent), shown in its marks.</summary>
    Quote,

    /// <summary>A quotation that isn't on the page: shown without its marks, as the model's words rather than the book's.</summary>
    Paraphrase,

    /// <summary>A [p. N] to a page that was sent: a link to that page.</summary>
    Citation,

    /// <summary>Said after a sentence whose every citation was to a page that wasn't sent, and so was removed.</summary>
    Unsupported,
}

/// <summary>A piece of a checked answer: its kind, its text, and for a citation the PDF page it opens and the words to mark there.</summary>
public sealed record AnswerPart(AnswerPartKind Kind, string Text, int? PdfPage = null, string? Phrase = null);

/// <summary>
/// An answer after the citation and quote checks (Ask plan, step 5): its pieces in order, whether the model said the
/// pages don't answer the question, what the checks counted, and the pages it cites, in the order it first cites them.
/// </summary>
public sealed record CheckedAnswer(IReadOnlyList<AnswerPart> Parts, bool NotAnswered, int Citations, int RemovedCitations, int Quotes, int Paraphrases,
    IReadOnlyList<int> CitedPages)
{
    public static CheckedAnswer Empty { get; } = new([], false, 0, 0, 0, 0, []);

    /// <summary>The answer as it reads on screen, citations included, for keeping and copying.</summary>
    public string Text => string.Concat(Parts.Select(p => p.Kind == AnswerPartKind.Unsupported ? $" {p.Text}" : p.Text));
}

/// <summary>
/// Checks an Ask answer against the pages sent with the question (Ask plan, step 5). Each [p. N] to a page that was
/// sent becomes a link; one to any other page is removed, and a sentence left with no citation it had is marked as
/// unsupported. Each quotation is looked for on the page its sentence cites, compared as EvidenceCheck compares quotes
/// (letters and digits only, after compatibility normalisation); one that isn't there is shown without its marks, as a
/// paraphrase. It works on an answer still being written, so the panel can show it checked as it streams.
/// </summary>
public static partial class AskAnswerCheck
{
    /// <summary>What a sentence whose citations were all removed is marked with.</summary>
    public const string UnsupportedNote = "(not on the pages it read)";

    /// <summary>How many of a quotation's words Find looks for when its citation is opened: few enough to be on one line.</summary>
    const int PhraseWords = 5;

    [GeneratedRegex(@"\[\s*(?:pages?|pp?)\s*\.?\s*(?<refs>[^\[\]\n]{1,60})\]", RegexOptions.IgnoreCase)]
    private static partial Regex Citation();

    [GeneratedRegex("\"(?<q>[^\"\\n]{1,400})\"|“(?<q>[^”\\n]{1,400})”")]
    private static partial Regex Quotation();

    [GeneratedRegex(@"\s*(?:,|;|&|\band\b|–|—|(?<=\d)\s*-\s*(?=\d))\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RefSeparator();

    [GeneratedRegex(@"^(?:pages?|pp?)\s*\.?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RefPrefix();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Thinking();

    /// <summary>A citation in a sentence, and the page each of its numbers names (null for a page that wasn't sent).</summary>
    sealed record CiteItem(Match Match, IReadOnlyList<AskPromptPage?> Pages);

    /// <summary>A quotation in a sentence, where it is in the answer, and the page it was found on (null when it wasn't).</summary>
    sealed record QuoteItem(Match Match, int At, int? FoundOn);

    /// <summary>Checks <paramref name="answer"/>, finished or still being written, against the pages that were sent.</summary>
    public static CheckedAnswer Check(string answer, IReadOnlyList<AskPromptPage> pages)
    {
        var text = Clean(answer);
        if (text.Length == 0) return CheckedAnswer.Empty;
        if (EvidenceCheck.Comparable(text).StartsWith(EvidenceCheck.Comparable(AskPrompt.NotAnswered), StringComparison.Ordinal))
            return new CheckedAnswer([new AnswerPart(AnswerPartKind.Text, text)], true, 0, 0, 0, 0, []);

        var byMarker = new Dictionary<string, AskPromptPage>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages) byMarker.TryAdd(page.Marker.Trim(), page);
        var comparable = pages.ToDictionary(p => p.PdfPage, p => EvidenceCheck.Comparable(p.Text));
        var citations = Citation().Matches(text).ToList();

        var parts = new List<AnswerPart>();
        var cited = new List<int>();
        int valid = 0, removed = 0, quotes = 0, paraphrases = 0;
        foreach (var (start, end) in Sentences(text, citations))
        {
            var inSentence = citations.Where(c => c.Index >= start && c.Index < end).ToList();
            var refs = inSentence.Select(c => new CiteItem(c, [.. Refs(c.Groups["refs"].Value).Select(r => byMarker.GetValueOrDefault(r))])).ToList();
            var sentencePages = refs.SelectMany(r => r.Pages).OfType<AskPromptPage>().Select(p => p.PdfPage).Distinct().ToList();

            // Quotations outside citations, each checked against the pages the sentence cites, else any page sent.
            var checkedQuotes = new List<QuoteItem>();
            foreach (Match quote in Quotation().Matches(text[start..end]))
            {
                var at = start + quote.Index;
                if (inSentence.Any(c => at < c.Index + c.Length && c.Index < at + quote.Length)) continue;
                var words = quote.Groups["q"].Value;
                int? foundOn = null;
                var tooShort = false;
                foreach (var page in sentencePages.Count > 0 ? sentencePages : [.. comparable.Keys])
                {
                    if (EvidenceCheck.Appears(words, comparable[page], out var why))
                    {
                        foundOn = page;
                        break;
                    }
                    tooShort = why == "The quote is too short to check.";
                    if (tooShort) break;
                }
                // A word or two in quotation marks (a name, a term) can't be checked, and is left as written.
                if (tooShort) continue;
                checkedQuotes.Add(new QuoteItem(quote, start + quote.Index, foundOn));
            }

            var items = refs.Select(r => (Start: r.Match.Index, End: r.Match.Index + r.Match.Length, Item: (object)r))
                .Concat(checkedQuotes.Select(q => (Start: q.At, End: q.At + q.Match.Length, Item: (object)q)))
                .OrderBy(i => i.Start).ToList();
            var from = start;
            var kept = 0;
            foreach (var (itemStart, itemEnd, item) in items)
            {
                Add(parts, AnswerPartKind.Text, text[from..itemStart]);
                if (item is CiteItem cite)
                {
                    var first = true;
                    foreach (var page in cite.Pages)
                    {
                        if (page is null)
                        {
                            removed++;
                            continue;
                        }
                        if (!first) Add(parts, AnswerPartKind.Text, " ");
                        first = false;
                        valid++;
                        kept++;
                        if (!cited.Contains(page.PdfPage)) cited.Add(page.PdfPage);
                        var phrase = checkedQuotes.FirstOrDefault(q => q.FoundOn == page.PdfPage)?.Match.Groups["q"].Value;
                        parts.Add(new AnswerPart(AnswerPartKind.Citation, $"[p. {page.Marker}]", page.PdfPage, phrase is null ? null : Phrase(phrase)));
                    }
                    // A removed citation takes the space before it with it.
                    if (first) TrimEnd(parts);
                }
                else if (item is QuoteItem quote)
                {
                    if (quote.FoundOn is not null) quotes++;
                    else paraphrases++;
                    parts.Add(quote.FoundOn is not null
                        ? new AnswerPart(AnswerPartKind.Quote, quote.Match.Value)
                        : new AnswerPart(AnswerPartKind.Paraphrase, quote.Match.Groups["q"].Value));
                }
                from = itemEnd;
            }
            var rest = text[from..end];
            if (inSentence.Count > 0 && kept == 0)
            {
                var body = rest.TrimEnd();
                Add(parts, AnswerPartKind.Text, body);
                parts.Add(new AnswerPart(AnswerPartKind.Unsupported, UnsupportedNote));
                Add(parts, AnswerPartKind.Text, rest[body.Length..]);
            }
            else Add(parts, AnswerPartKind.Text, rest);
        }
        return new CheckedAnswer(parts, false, valid, removed, quotes, paraphrases, cited);
    }

    /// <summary>
    /// The answer without the model's thinking (a finished &lt;think&gt; block, or an unfinished one and all after it) or
    /// Markdown's bold and italic marks, trimmed.
    /// </summary>
    static string Clean(string answer)
    {
        var text = Thinking().Replace(answer, "");
        var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0) text = text[..open];
        return text.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// The answer cut into sentences, end to end: each ends after its full stop (or question or exclamation mark, and any
    /// closing quotation mark or bracket) and the citations straight after it, or at a line break.
    /// </summary>
    static IEnumerable<(int Start, int End)> Sentences(string text, IReadOnlyList<Match> citations)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (citations.FirstOrDefault(c => c.Index == i) is { } inside)
            {
                i += inside.Length;
                continue;
            }
            var c = text[i];
            var end = -1;
            if (c == '\n') end = i + 1;
            else if (c is '.' or '!' or '?')
            {
                var j = i + 1;
                while (j < text.Length && text[j] is '"' or '”' or '’' or '\'' or ')') j++;
                if (j == text.Length || char.IsWhiteSpace(text[j]) || citations.Any(m => m.Index == j)) end = j;
            }
            if (end < 0)
            {
                i++;
                continue;
            }
            // The citations after the full stop belong to the sentence before it: "… the mill. [p. 4]".
            while (true)
            {
                var k = end;
                while (k < text.Length && text[k] is ' ' or '\t') k++;
                if (citations.FirstOrDefault(m => m.Index == k) is not { } after) break;
                end = k + after.Length;
                if (end < text.Length && text[end] is '.') end++;
            }
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
            yield return (start, end);
            start = end;
            i = end;
        }
        if (start < text.Length) yield return (start, text.Length);
    }

    /// <summary>The pages a citation names: "12", "12, 14", "12 and 14", "12–14" (both ends), "iv".</summary>
    static IEnumerable<string> Refs(string refs) =>
        RefSeparator().Split(refs).Select(r => RefPrefix().Replace(r.Trim(), "").Trim()).Where(r => r.Length > 0);

    static string Phrase(string quote) => string.Join(' ', quote.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(PhraseWords));

    static void Add(List<AnswerPart> parts, AnswerPartKind kind, string text)
    {
        if (text.Length == 0) return;
        if (kind == AnswerPartKind.Text && parts.Count > 0 && parts[^1].Kind == AnswerPartKind.Text)
            parts[^1] = parts[^1] with { Text = parts[^1].Text + text };
        else parts.Add(new AnswerPart(kind, text));
    }

    static void TrimEnd(List<AnswerPart> parts)
    {
        if (parts.Count == 0 || parts[^1].Kind != AnswerPartKind.Text) return;
        var trimmed = parts[^1].Text.TrimEnd(' ', '\t');
        if (trimmed.Length == 0) parts.RemoveAt(parts.Count - 1);
        else parts[^1] = parts[^1] with { Text = trimmed };
    }

    /// <summary>A short, plain summary of what the checks did, for the log: counts, never text.</summary>
    public static string Counts(CheckedAnswer answer)
    {
        var text = new StringBuilder();
        text.Append(answer.Citations).Append(" citations to ").Append(answer.CitedPages.Count).Append(" pages");
        if (answer.RemovedCitations > 0) text.Append(", ").Append(answer.RemovedCitations).Append(" removed");
        text.Append(", ").Append(answer.Quotes).Append(" quotes found");
        if (answer.Paraphrases > 0) text.Append(", ").Append(answer.Paraphrases).Append(" shown as paraphrases");
        return text.ToString();
    }
}
