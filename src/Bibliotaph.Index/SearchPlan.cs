using Bibliotaph.Core;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Index;

/// <summary>
/// A parsed query turned into the pieces the SQL needs. FTS5 text is assembled only from quoted strings, the
/// operators AND, OR, NOT and *, and the one column name <c>title</c>, so nothing the user types can become FTS5 or
/// SQL syntax; every value reaches SQLite as a bound parameter.
/// </summary>
public sealed record SearchPlan
{
    /// <summary>The words to find, as an FTS5 expression for both page_fts and doc_fts. Null when there are none.</summary>
    public string? TextMatch { get; init; }

    /// <summary>When the query has exclusions but nothing to find (<c>-maps</c>): what to leave out.</summary>
    public string? TextExclude { get; init; }

    /// <summary><c>title:</c> values, as a doc_fts expression limited to the title column.</summary>
    public string? TitleMatch { get; init; }

    /// <summary><c>-title:</c> values: documents whose title matches are left out.</summary>
    public string? TitleExclude { get; init; }

    public IReadOnlyList<string> Formats { get; init; } = [];
    public IReadOnlyList<string> ExcludedFormats { get; init; } = [];
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyList<string> ExcludedFolders { get; init; } = [];

    /// <summary>True when the query asks for nothing at all, so the library shows everything.</summary>
    public bool IsEmpty => TextMatch is null && TextExclude is null && TitleMatch is null && TitleExclude is null
        && Formats.Count == 0 && ExcludedFormats.Count == 0 && Folders.Count == 0 && ExcludedFolders.Count == 0;

    /// <summary>Documents found by title and metadata: the text and title parts together.</summary>
    public string? DocumentMatch => (TextMatch, TitleMatch) switch
    {
        (null, null) => null,
        ({ } text, null) => text,
        (null, { } title) => title,
        ({ } text, { } title) => $"({text}) AND ({title})",
    };

    public static SearchPlan From(SearchQuery query)
    {
        if (query.Root is null) return new SearchPlan();
        var clauses = query.Root is AndNode and ? and.Items : [query.Root];

        var text = new List<QueryNode>();
        var excluded = new List<QueryNode>();
        var titles = new List<string>();
        var excludedTitles = new List<string>();
        var formats = new List<string>();
        var excludedFormats = new List<string>();
        var folders = new List<string>();
        var excludedFolders = new List<string>();

        foreach (var clause in clauses)
        {
            var (node, negated) = clause is NotNode not ? (not.Operand, true) : (clause, false);
            if (node is FieldNode field)
            {
                var value = ValueText(field.Value);
                switch (field.Field)
                {
                    case SearchField.Title:
                        (negated ? excludedTitles : titles).Add("title : " + Fts(field.Value));
                        break;
                    case SearchField.Format:
                        (negated ? excludedFormats : formats).AddRange(value == "image" ? [SourceFormats.Jpeg, SourceFormats.Png] : [value]);
                        break;
                    case SearchField.Folder:
                        (negated ? excludedFolders : folders).Add(value);
                        break;
                }
            }
            else if (negated) excluded.Add(node);
            else text.Add(node);
        }

        string? textMatch = null, textExclude = null;
        if (text.Count > 0) textMatch = Fts(new AndNode([.. text, .. excluded.Select(e => new NotNode(e))]));
        else if (excluded.Count > 0) textExclude = string.Join(" OR ", excluded.Select(Fts));

        return new SearchPlan
        {
            TextMatch = textMatch,
            TextExclude = textExclude,
            TitleMatch = titles.Count == 0 ? null : string.Join(" AND ", titles),
            TitleExclude = excludedTitles.Count == 0 ? null : string.Join(" OR ", excludedTitles),
            Formats = [.. formats.Distinct()],
            ExcludedFormats = [.. excludedFormats.Distinct()],
            Folders = folders,
            ExcludedFolders = excludedFolders,
        };
    }

    static string ValueText(QueryNode value) => value switch
    {
        TermNode t => t.Text,
        PhraseNode p => p.Text,
        _ => throw new ArgumentException($"A field value is a word or a phrase, not {value.GetType().Name}.", nameof(value)),
    };

    /// <summary>An FTS5 expression for a text node. Fields never reach here; the parser keeps them out of OR and groups.</summary>
    static string Fts(QueryNode node)
    {
        switch (node)
        {
            case TermNode term:
                return Quote(term.Text) + (term.Prefix ? " *" : "");
            case PhraseNode phrase:
                return Quote(phrase.Text);
            case OrNode or:
                return "(" + string.Join(" OR ", or.Items.Select(Fts)) + ")";
            case AndNode and:
                var positive = and.Items.Where(i => i is not NotNode).Select(Fts).ToList();
                var negative = and.Items.OfType<NotNode>().Select(n => Fts(n.Operand)).ToList();
                // FTS5 has no unary NOT. A group of only exclusions inside OR can't be expressed, so it matches nothing.
                if (positive.Count == 0) return Quote("\u0001");
                var expression = "(" + string.Join(" AND ", positive) + ")";
                return negative.Count == 0 ? expression : $"{expression} NOT ({string.Join(" OR ", negative)})";
            case NotNode:
                throw new ArgumentException("An exclusion is compiled by the AND around it.", nameof(node));
            default:
                throw new ArgumentException($"{node.GetType().Name} is not text.", nameof(node));
        }
    }

    /// <summary>An FTS5 string: double quotes with any inside doubled. The tokenizer splits it into words.</summary>
    static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
