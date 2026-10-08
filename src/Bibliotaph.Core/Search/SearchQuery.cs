namespace Bibliotaph.Core.Search;

/// <summary>A field the search box understands. Only fields with data behind them exist; slice 2 adds metadata fields.</summary>
public enum SearchField
{
    /// <summary><c>title:</c>, the display title (and, from slice 2, confirmed and suggested titles).</summary>
    Title,

    /// <summary><c>format:</c> pdf, jpg, png, or image for either image format.</summary>
    Format,

    /// <summary><c>folder:</c>, part of a folder name between the source folder and the file.</summary>
    Folder,
}

/// <summary>
/// A parsed search: words, "phrases", -exclusions, prefix*, OR, (groups) and fields. Smart Views will store this
/// tree, so it is plain data with a stable text form (<see cref="ToString"/>) rather than SQL.
/// </summary>
public abstract record QueryNode;

/// <summary>A word as typed. <see cref="Prefix"/> is set by a trailing <c>*</c>.</summary>
public sealed record TermNode(string Text, bool Prefix = false) : QueryNode
{
    public override string ToString() => Prefix ? Text + "*" : Text;
}

/// <summary>Words that must appear together and in order.</summary>
public sealed record PhraseNode(string Text) : QueryNode
{
    public override string ToString() => $"\"{Text}\"";
}

/// <summary>An exclusion: <c>-word</c>.</summary>
public sealed record NotNode(QueryNode Operand) : QueryNode
{
    public override string ToString() => $"-{Operand}";
}

/// <summary>Everything must match. Adjacent clauses are joined this way.</summary>
public sealed record AndNode(IReadOnlyList<QueryNode> Items) : QueryNode
{
    public bool Equals(AndNode? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Aggregate(17, (h, i) => h * 31 + i.GetHashCode());

    public override string ToString() => $"AND({string.Join(", ", Items)})";
}

/// <summary>Any one must match.</summary>
public sealed record OrNode(IReadOnlyList<QueryNode> Items) : QueryNode
{
    public bool Equals(OrNode? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Aggregate(19, (h, i) => h * 31 + i.GetHashCode());

    public override string ToString() => $"OR({string.Join(", ", Items)})";
}

/// <summary><c>field:value</c>, where the value is a word (possibly a prefix) or a phrase.</summary>
public sealed record FieldNode(SearchField Field, QueryNode Value) : QueryNode
{
    public override string ToString() => $"{Field.ToString().ToLowerInvariant()}:{Value}";
}

/// <summary>
/// Something in the query that was ignored or repaired, with where it is in the text so the search box can mark it.
/// The rest of the query still runs.
/// </summary>
public sealed record QueryIssue(string Message, int Start, int Length);

/// <summary>The result of parsing: the query tree (null when nothing searchable is left) and anything that was ignored.</summary>
public sealed record SearchQuery(string Text, QueryNode? Root, IReadOnlyList<QueryIssue> Issues)
{
    public bool IsEmpty => Root is null;

    /// <summary>Parses what the user typed. Never throws; problems come back as <see cref="Issues"/>.</summary>
    public static SearchQuery Parse(string? text) => new QueryParser(text ?? "").Parse();
}

/// <summary>A hand-written recursive-descent parser. Grammar, loosest binding first:
/// <code>
/// query   := or
/// or      := and ("OR" and)*
/// and     := unary+
/// unary   := "-" primary | primary
/// primary := phrase | word | field ":" (word | phrase) | "(" or ")"
/// </code>
/// </summary>
sealed class QueryParser(string text)
{
    /// <summary>Deeper nesting than this is almost certainly a paste accident; it also bounds recursion.</summary>
    const int MaxDepth = 16;

    /// <summary>Fields that arrive with metadata in slice 2. Typing one says so rather than searching for the word.</summary>
    static readonly HashSet<string> FutureFields =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "type", "level", "levels", "system", "publisher", "author", "series", "tag", "tags", "theme", "setting", "length",
    ];

    static readonly Dictionary<string, SearchField> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = SearchField.Title,
        ["format"] = SearchField.Format,
        ["folder"] = SearchField.Folder,
    };

    readonly List<Token> _tokens = [];
    readonly List<QueryIssue> _issues = [];
    int _at;

    public SearchQuery Parse()
    {
        Tokenize();
        QueryNode? root = null;
        if (_tokens.Count > 0)
        {
            root = ParseOr(0);
            while (_at < _tokens.Count)
            {
                // Only a stray ")" stops the top level early.
                var stray = _tokens[_at++];
                Issue("This ) has no matching (.", stray);
                if (ParseOr(0) is { } more) root = root is null ? more : Join(root, more);
            }
        }
        return new SearchQuery(text, root is null ? null : Simplify(root), _issues);
    }

    QueryNode? ParseOr(int depth)
    {
        // Each side of an OR, with the OR before it (null for the first side).
        var sides = new List<(QueryNode? Node, Token? OrBefore)>();
        Token? orBefore = null;
        while (true)
        {
            sides.Add((ParseAnd(depth), orBefore));
            if (Peek() is not { Kind: TokenKind.Or } or) break;
            _at++;
            orBefore = or;
        }
        if (sides.Count == 1) return sides[0].Node;

        var items = new List<QueryNode>();
        for (var i = 0; i < sides.Count; i++)
        {
            var (node, before) = sides[i];
            var or = before ?? sides[i + 1].OrBefore!;
            if (node is null) Issue("OR needs something on both sides.", or);
            else if (node is NotNode) Issue("An exclusion can't be one side of OR, so it was left out.", or);
            else if (ContainsField(node)) Issue("Fields like title: can't be combined with OR yet, so that side was left out.", or);
            else items.Add(node);
        }
        return items.Count switch
        {
            0 => null,
            1 => items[0],
            _ => new OrNode(items),
        };
    }

    QueryNode? ParseAnd(int depth)
    {
        var items = new List<QueryNode>();
        while (Peek() is { } token && token.Kind is not (TokenKind.Or or TokenKind.Close))
        {
            if (ParseUnary(depth) is { } node) items.Add(node);
        }
        return items.Count switch
        {
            0 => null,
            1 => items[0],
            _ => new AndNode(items),
        };
    }

    QueryNode? ParseUnary(int depth)
    {
        var token = _tokens[_at];
        if (token.Kind != TokenKind.Minus) return ParsePrimary(depth);
        _at++;
        if (Peek() is not { Kind: TokenKind.Word or TokenKind.Phrase or TokenKind.Field or TokenKind.Open })
        {
            Issue("A minus sign needs a word straight after it.", token);
            return null;
        }
        var operand = ParsePrimary(depth);
        if (operand is NotNode inner) return inner.Operand; // --word is word
        return operand is null ? null : new NotNode(operand);
    }

    QueryNode? ParsePrimary(int depth)
    {
        var token = _tokens[_at++];
        switch (token.Kind)
        {
            case TokenKind.Word:
                return Word(token);
            case TokenKind.Phrase:
                return Phrase(token);
            case TokenKind.Field:
                return Field(token);
            case TokenKind.Open:
                if (depth >= MaxDepth)
                {
                    Issue("Brackets are nested too deeply.", token);
                    SkipGroup();
                    return null;
                }
                var inner = ParseOr(depth + 1);
                if (Peek() is { Kind: TokenKind.Close }) _at++;
                else Issue("This ( is never closed, so it was closed at the end.", token);
                if (inner is null) Issue("These brackets are empty.", token);
                return inner;
            default:
                // A lone OR or ) where a word was expected; ParseOr and Parse report those.
                _at--;
                return null;
        }
    }

    void SkipGroup()
    {
        for (var open = 1; _at < _tokens.Count && open > 0; _at++)
            open += _tokens[_at].Kind switch { TokenKind.Open => 1, TokenKind.Close => -1, _ => 0 };
    }

    TermNode? Word(Token token)
    {
        var word = token.Text;
        var prefix = word.EndsWith('*');
        var stem = word.TrimEnd('*');
        if (!HasSearchableText(stem))
        {
            Issue(prefix ? "A * needs some letters before it." : $"\"{word}\" has no letters or numbers to search for.", token);
            return null;
        }
        if (stem.Contains('*', StringComparison.Ordinal))
        {
            Issue("A * only works at the end of a word, so it was ignored here.", token);
            stem = stem.Replace("*", "", StringComparison.Ordinal);
        }
        return new TermNode(stem, prefix);
    }

    PhraseNode? Phrase(Token token)
    {
        if (!token.Closed) Issue("This quote is never closed, so it was closed at the end.", token);
        var words = string.Join(' ', token.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!HasSearchableText(words))
        {
            Issue("These quotes have nothing to search for.", token);
            return null;
        }
        return new PhraseNode(words);
    }

    FieldNode? Field(Token token)
    {
        var name = token.Text;
        Token? valueToken = Peek() is { Kind: TokenKind.Word or TokenKind.Phrase } v && v.Start == token.Start + token.Length ? v : null;
        if (valueToken is not null) _at++;

        if (FutureFields.Contains(name))
        {
            Issue($"Searching by {name.ToLowerInvariant()} arrives once books have metadata.", Span(token, valueToken));
            return null;
        }
        var field = Fields[name];
        if (valueToken is null)
        {
            Issue($"{name.ToLowerInvariant()}: needs a value straight after it, like {Example(field)}.", token);
            return null;
        }

        QueryNode? value = valueToken.Kind == TokenKind.Phrase ? Phrase(valueToken) : Word(valueToken);
        if (value is null) return null;
        if (field == SearchField.Format)
        {
            if (value is not TermNode { Prefix: false } term || NormalizeFormat(term.Text) is not { } format)
            {
                Issue("format: can be pdf, jpg, png or image.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(format);
        }
        return new FieldNode(field, value);
    }

    static string Example(SearchField field) => field switch
    {
        SearchField.Title => "title:dragon",
        SearchField.Format => "format:pdf",
        _ => "folder:maps",
    };

    /// <summary>pdf, jpg or png, or image for both image formats.</summary>
    static string? NormalizeFormat(string value) => value.ToLowerInvariant() switch
    {
        "pdf" => SourceFormats.Pdf,
        "jpg" or "jpeg" => SourceFormats.Jpeg,
        "png" => SourceFormats.Png,
        "image" or "images" or "picture" or "pictures" => "image",
        _ => null,
    };

    static bool HasSearchableText(string s) => s.Any(char.IsLetterOrDigit);

    static bool ContainsField(QueryNode node) => node switch
    {
        FieldNode => true,
        NotNode n => ContainsField(n.Operand),
        AndNode a => a.Items.Any(ContainsField),
        OrNode o => o.Items.Any(ContainsField),
        _ => false,
    };

    static AndNode Join(QueryNode left, QueryNode right) => new([left, right]);

    /// <summary>Flattens AND inside AND and OR inside OR, so equivalent queries have one shape.</summary>
    static QueryNode Simplify(QueryNode node) => node switch
    {
        AndNode a => new AndNode([.. a.Items.Select(Simplify).SelectMany(i => i is AndNode inner ? inner.Items : [i])]),
        OrNode o => new OrNode([.. o.Items.Select(Simplify).SelectMany(i => i is OrNode inner ? inner.Items : [i])]),
        NotNode n => new NotNode(Simplify(n.Operand)),
        _ => node,
    };

    Token? Peek() => _at < _tokens.Count ? _tokens[_at] : null;

    void Issue(string message, Token token) => _issues.Add(new QueryIssue(message, token.Start, token.Length));

    void Issue(string message, (int Start, int Length) span) => _issues.Add(new QueryIssue(message, span.Start, span.Length));

    static (int Start, int Length) Span(Token first, Token? last) =>
        last is null ? (first.Start, first.Length) : (first.Start, last.Start + last.Length - first.Start);

    // ---- Tokens ---------------------------------------------------------------------------------------------------

    enum TokenKind { Word, Phrase, Field, Minus, Or, Open, Close }

    /// <summary>A token and where it sits in the text. For a field, <see cref="Text"/> is the name and the span covers "name:".</summary>
    sealed record Token(TokenKind Kind, string Text, int Start, int Length, bool Closed = true);

    void Tokenize()
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            switch (c)
            {
                case '(':
                    _tokens.Add(new Token(TokenKind.Open, "(", i++, 1));
                    continue;
                case ')':
                    _tokens.Add(new Token(TokenKind.Close, ")", i++, 1));
                    continue;
                case '"' or '“' or '”':
                    i = ReadPhrase(i);
                    continue;
                case '-' when AtTokenStart(i) && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]):
                    _tokens.Add(new Token(TokenKind.Minus, "-", i++, 1));
                    continue;
            }
            i = ReadWord(i);
        }
    }

    /// <summary>A minus only excludes at the start of a word; inside one (half-orc) it is part of the word.</summary>
    bool AtTokenStart(int i) => i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is '(' or ')';

    int ReadPhrase(int start)
    {
        var end = start + 1;
        while (end < text.Length && text[end] is not ('"' or '“' or '”')) end++;
        var closed = end < text.Length;
        var content = text[(start + 1)..end];
        var length = (closed ? end + 1 : end) - start;
        _tokens.Add(new Token(TokenKind.Phrase, content, start, length, closed));
        return start + length;
    }

    int ReadWord(int start)
    {
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('(' or ')' or '"' or '“' or '”'))
        {
            if (text[end] == ':' && IsFieldName(text[start..end]))
            {
                _tokens.Add(new Token(TokenKind.Field, text[start..end], start, end + 1 - start));
                return end + 1;
            }
            end++;
        }
        var word = text[start..end];
        _tokens.Add(word == "OR" ? new Token(TokenKind.Or, word, start, end - start) : new Token(TokenKind.Word, word, start, end - start));
        return end;
    }

    /// <summary>Only known names make a field, so "http://..." or "AD:D" stay ordinary words.</summary>
    static bool IsFieldName(string name) => Fields.ContainsKey(name) || FutureFields.Contains(name);
}
