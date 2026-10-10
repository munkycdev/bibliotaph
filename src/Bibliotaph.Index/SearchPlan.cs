using Bibliotaph.Core;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Index;

/// <summary>
/// A vocabulary field condition: <c>type:adventure</c>, <c>-system:dnd</c>, <c>theme:unknown</c>. The value is matched
/// against the terms' names (term_alias) and labels; <see cref="Fields"/> is more than one field for <c>system:</c>,
/// which also looks at editions.
/// </summary>
public sealed record FacetCondition(IReadOnlyList<string> Fields, string Value, bool Prefix = false, bool Negated = false)
{
    public bool IsUnknown => !Prefix && Value == SearchQuery.Unknown;
}

/// <summary><c>favorite:yes</c>: entries in a group (entry_scope), or with <see cref="Negated"/> those not in it.</summary>
public sealed record ScopeCondition(string Scope, bool Negated = false);

/// <summary><c>level:</c> a stored level value ("3", "1-5", "n/a") or unknown.</summary>
public sealed record LevelCondition(string Value, bool Negated = false);

/// <summary>
/// A parsed query turned into the pieces the SQL needs. FTS5 text is assembled only from quoted strings, the
/// operators AND, OR, NOT and *, and the fixed entry_fts column names in <see cref="Column"/>, so nothing the user types
/// can become FTS5 or SQL syntax; every value reaches SQLite as a bound parameter.
/// </summary>
public sealed record SearchPlan
{
    /// <summary>
    /// A word this long or longer also finds words that start with it (drag finds dragon), as if typed drag*.
    /// Shorter words match only themselves, so "of" or "5e" doesn't sweep in every word that begins that way.
    /// Phrases stay exact.
    /// </summary>
    public const int MinWordStart = 3;

    /// <summary>The words to find, as an FTS5 expression for both page_fts and entry_fts. Null when there are none.</summary>
    public string? TextMatch { get; init; }

    /// <summary>When the query has exclusions but nothing to find (<c>-maps</c>): what to leave out.</summary>
    public string? TextExclude { get; init; }

    /// <summary>
    /// <c>title:</c>, <c>publisher:</c>, <c>author:</c>, <c>series:</c> and <c>tag:</c> values, as a entry_fts expression
    /// with each value limited to its column.
    /// </summary>
    public string? FieldMatch { get; init; }

    /// <summary>The same fields negated (<c>-publisher:</c>): documents that match are left out.</summary>
    public string? FieldExclude { get; init; }

    public IReadOnlyList<string> Formats { get; init; } = [];
    public IReadOnlyList<string> ExcludedFormats { get; init; } = [];
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyList<string> ExcludedFolders { get; init; } = [];
    public IReadOnlyList<FacetCondition> Facets { get; init; } = [];
    public IReadOnlyList<LevelCondition> Levels { get; init; } = [];
    public IReadOnlyList<ScopeCondition> Scopes { get; init; } = [];

    /// <summary>True when the query asks for nothing at all, so the library shows everything.</summary>
    public bool IsEmpty => TextMatch is null && TextExclude is null && FieldMatch is null && FieldExclude is null
        && Formats.Count == 0 && ExcludedFormats.Count == 0 && Folders.Count == 0 && ExcludedFolders.Count == 0
        && Facets.Count == 0 && Levels.Count == 0 && Scopes.Count == 0;

    /// <summary>Documents found by title and metadata: the text and text-field parts together.</summary>
    public string? DocumentMatch => (TextMatch, FieldMatch) switch
    {
        (null, null) => null,
        ({ } text, null) => text,
        (null, { } fields) => fields,
        ({ } text, { } fields) => $"({text}) AND ({fields})",
    };

    /// <summary>The entry_fts column a text field searches, or null for a field that isn't one.</summary>
    public static string? Column(SearchField field) => field switch
    {
        SearchField.Title => "title",
        SearchField.Publisher => "publisher",
        SearchField.Author => "authors",
        SearchField.Series => "series",
        SearchField.Tag => "tags",
        _ => null,
    };

    /// <summary>The entry_facet fields a vocabulary field searches, or null for a field that isn't one.</summary>
    public static IReadOnlyList<string>? FacetFields(SearchField field) => field switch
    {
        SearchField.System => ["system", "edition"],
        SearchField.Edition => ["edition"],
        SearchField.Type => ["type"],
        SearchField.Setting => ["setting"],
        SearchField.Theme => ["theme"],
        SearchField.Environment => ["environment"],
        SearchField.Own => ["own"],
        _ => null,
    };

    public static SearchPlan From(SearchQuery query)
    {
        if (query.Root is null) return new SearchPlan();
        var clauses = query.Root is AndNode and ? and.Items : [query.Root];

        var text = new List<QueryNode>();
        var excluded = new List<QueryNode>();
        var fields = new List<string>();
        var excludedFields = new List<string>();
        var facets = new List<FacetCondition>();
        var levels = new List<LevelCondition>();
        var scopes = new List<ScopeCondition>();
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
                if (Column(field.Field) is { } column)
                {
                    (negated ? excludedFields : fields).Add($"{column} : " + Fts(field.Value));
                    continue;
                }
                if (FacetFields(field.Field) is { } facetFields)
                {
                    facets.Add(new FacetCondition(facetFields, value, field.Value is TermNode { Prefix: true }, negated));
                    continue;
                }
                switch (field.Field)
                {
                    case SearchField.Level:
                        levels.Add(new LevelCondition(value, negated));
                        break;
                    case SearchField.Format:
                        (negated ? excludedFormats : formats).AddRange(value == "image" ? SourceFormats.Images : [value]);
                        break;
                    case SearchField.Folder:
                        (negated ? excludedFolders : folders).Add(value);
                        break;
                    case SearchField.Favorite:
                        scopes.Add(new ScopeCondition(ScopeKeys.Favorites, negated == (value == SearchQuery.Yes)));
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
            FieldMatch = fields.Count == 0 ? null : string.Join(" AND ", fields),
            FieldExclude = excludedFields.Count == 0 ? null : string.Join(" OR ", excludedFields),
            Formats = [.. formats.Distinct()],
            ExcludedFormats = [.. excludedFormats.Distinct()],
            Folders = folders,
            ExcludedFolders = excludedFolders,
            Facets = facets,
            Levels = levels,
            Scopes = scopes,
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
                return Quote(term.Text) + (term.Prefix || term.Text.Length >= MinWordStart ? " *" : "");
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
