using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Search;

/// <summary>A field the search box understands. Only fields with data behind them exist.</summary>
public enum SearchField
{
    /// <summary><c>title:</c>, the document's title (its effective title, or the one from its file name).</summary>
    Title,

    /// <summary><c>format:</c> pdf, epub, mobi, azw, azw3, kfx, jpg, png, webp, image for any image format, ebook for any e-book format, or kindle for any Kindle format.</summary>
    Format,

    /// <summary><c>folder:</c>, part of a folder name between the source folder and the file.</summary>
    Folder,

    /// <summary><c>publisher:</c>, words in the publisher's name.</summary>
    Publisher,

    /// <summary><c>author:</c>, words in an author's name.</summary>
    Author,

    /// <summary><c>series:</c>, words in the series name.</summary>
    Series,

    /// <summary><c>tag:</c>, one of the user's tags.</summary>
    Tag,

    /// <summary><c>system:</c>, a game system or edition by any of its names: dnd, "D&amp;D 5e", pf2e. Also <c>system:unknown</c>.</summary>
    System,

    /// <summary><c>edition:</c>, an edition by any of its names.</summary>
    Edition,

    /// <summary><c>type:</c>, a document type: adventure, bestiary, map.</summary>
    Type,

    /// <summary><c>setting:</c>, a campaign setting: the book's own, or one the AI found in one of its chapters.</summary>
    Setting,

    /// <summary><c>theme:</c>, a theme or tone such as horror or fey, the book's own or one of its chapters'.</summary>
    Theme,

    /// <summary><c>style:</c>, the kind of adventure: heist, dungeon crawl, hexcrawl.</summary>
    Style,

    /// <summary><c>duration:</c>, how long it takes to play: one-shot, short, long, campaign.</summary>
    Duration,

    /// <summary><c>environment:</c>, where it happens: urban, underground. The book's own, or one of its chapters'.</summary>
    Environment,

    /// <summary><c>own:</c>, where else the book is owned: print, foundry.</summary>
    Own,

    /// <summary><c>store:</c>, where to get it: kickstarter, drivethrurpg.</summary>
    Store,

    /// <summary><c>level:</c> 3, 1-5, none (levels don't apply) or unknown. A range matches books whose levels overlap it.</summary>
    Level,

    /// <summary><c>players:</c> 4, 3-5 or unknown. A number matches books written for it; a range, books whose range overlaps it.</summary>
    Players,

    /// <summary><c>favorite:</c> yes or no: the books marked with a heart.</summary>
    Favorite,

    /// <summary><c>collection:</c> a collection's name: its books and its sub-collections' books.</summary>
    Collection,

    /// <summary>
    /// <c>where:</c> here for the books with a file on this PC, elsewhere for those owned only somewhere else.
    /// </summary>
    Where,

    /// <summary><c>creature:</c>, a creature the AI found in one of the book's chapters: dragon, ghoul.</summary>
    Creature,

    /// <summary><c>isbn:</c>, an ISBN printed in the book, with or without hyphens, ISBN-10 or 13, or unknown.</summary>
    Isbn,

    /// <summary><c>opened:</c> yes or no (or never): the books opened at least once, or never.</summary>
    Opened,
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
    /// <summary>The field value that finds documents with nothing known for that field: <c>system:unknown</c>, <c>level:unknown</c>.</summary>
    public const string Unknown = "unknown";

    /// <summary>The stored value of <c>favorite:yes</c>.</summary>
    public const string Yes = "yes";

    /// <summary>The stored value of <c>favorite:no</c>.</summary>
    public const string No = "no";

    /// <summary>The stored value of <c>where:here</c>.</summary>
    public const string Here = "here";

    /// <summary>The stored value of <c>where:elsewhere</c>.</summary>
    public const string Elsewhere = "elsewhere";

    public bool IsEmpty => Root is null;

    /// <summary>Parses what the user typed. Never throws; problems come back as <see cref="Issues"/>.</summary>
    public static SearchQuery Parse(string? text) => new QueryParser(text ?? "").Parse();

    /// <summary>
    /// The words and phrases to mark on a page that matched: everything searched for in page text, leaving out
    /// exclusions and field filters. Prefix terms give their stem.
    /// </summary>
    public IReadOnlyList<string> HighlightTerms()
    {
        var terms = new List<string>();
        Collect(Root);
        return [.. terms.Distinct(StringComparer.OrdinalIgnoreCase)];

        void Collect(QueryNode? node)
        {
            switch (node)
            {
                case TermNode term:
                    terms.Add(term.Text);
                    break;
                case PhraseNode phrase:
                    terms.Add(phrase.Text);
                    break;
                case AndNode and:
                    foreach (var item in and.Items) Collect(item);
                    break;
                case OrNode or:
                    foreach (var item in or.Items) Collect(item);
                    break;
            }
        }
    }

    /// <summary>The query's field clauses as typed, for saying what a search narrows to: "system:dnd", "not type:map".</summary>
    public IEnumerable<string> FieldClauses()
    {
        var clauses = Root is AndNode and ? and.Items : Root is null ? [] : [Root];
        foreach (var clause in clauses)
        {
            if (clause is FieldNode field) yield return field.ToString();
            else if (clause is NotNode { Operand: FieldNode excluded }) yield return "not " + excluded;
        }
    }
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

        if (SearchFields.Find(name) is not { } info)
        {
            Issue($"Searching by {name.ToLowerInvariant()} isn't available yet.", Span(token, valueToken));
            return null;
        }
        var field = info.Field;
        if (valueToken is null)
        {
            Issue($"{name.ToLowerInvariant()}: needs a value straight after it, like {info.Example}.", token);
            return null;
        }

        QueryNode? value = valueToken.Kind == TokenKind.Phrase ? Phrase(valueToken) : Word(valueToken);
        if (value is null) return null;
        if (field == SearchField.Format)
        {
            if (value is not TermNode { Prefix: false } term || NormalizeFormat(term.Text) is not { } format)
            {
                Issue("format: can be pdf, epub, mobi, azw3, jpg, png, webp, ebook, kindle or image.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(format);
        }
        if (field == SearchField.Level)
        {
            if (value is TermNode { Prefix: true } || NormalizeLevel(ValueText(value)) is not { } level)
            {
                Issue($"level: can be a level like 3, a range like 1-5, none, or unknown.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(level);
        }
        if (field == SearchField.Players)
        {
            if (value is TermNode { Prefix: true } || NormalizePlayers(ValueText(value)) is not { } players)
            {
                Issue("players: can be a number like 4, a range like 3-5, or unknown.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(players);
        }
        if (field == SearchField.Favorite)
        {
            if (value is not TermNode { Prefix: false } answer || NormalizeYesNo(answer.Text) is not { } yesNo)
            {
                Issue("favorite: can be yes or no.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(yesNo);
        }
        if (field == SearchField.Opened)
        {
            if (value is not TermNode { Prefix: false } answer || NormalizeOpened(answer.Text) is not { } opened)
            {
                Issue("opened: can be yes or no.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(opened);
        }
        if (field == SearchField.Isbn)
        {
            if (value is not TermNode { Prefix: false } isbn || NormalizeIsbn(isbn.Text) is not { } digits)
            {
                Issue("isbn: needs a whole ISBN, like 978-1-23-456789-7, or unknown.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(digits);
        }
        if (field == SearchField.Where)
        {
            if (value is not TermNode { Prefix: false } answer || NormalizeWhere(answer.Text) is not { } where)
            {
                Issue("where: can be here or elsewhere.", Span(token, valueToken));
                return null;
            }
            value = new TermNode(where);
        }
        return new FieldNode(field, value);
    }

    static string ValueText(QueryNode value) => value switch
    {
        TermNode t => t.Text,
        PhraseNode p => p.Text,
        _ => "",
    };

    /// <summary>The stored form of a level value: "3", "1-5", "n/a" for none, or "unknown".</summary>
    static string? NormalizeLevel(string value) =>
        MetadataText.Normalize(value) is "unknown" or "unknown levels" ? SearchQuery.Unknown
        : LevelRange.TryParse(value, out var range) ? range.ToString()
        : null;

    /// <summary>The stored form of a players value: "4", "3-5", or "unknown".</summary>
    static string? NormalizePlayers(string value) =>
        MetadataText.Normalize(value) is "unknown" ? SearchQuery.Unknown
        : PlayerRange.TryParse(value, out var range) ? range.ToString()
        : null;

    /// <summary>An ISBN's 13 digits, however it is written, or "unknown".</summary>
    static string? NormalizeIsbn(string value) =>
        MetadataText.Normalize(value) is "unknown" ? SearchQuery.Unknown : Isbn.Normalize(value);

    /// <summary>"yes" or "no", for a field that is one or the other.</summary>
    static string? NormalizeYesNo(string value) => value.ToLowerInvariant() switch
    {
        "yes" or "y" or "true" => SearchQuery.Yes,
        "no" or "n" or "false" => SearchQuery.No,
        _ => null,
    };

    /// <summary>"here" or "elsewhere", for <c>where:</c>; a few other words for each are understood.</summary>
    static string? NormalizeOpened(string value) => value.Equals("never", StringComparison.OrdinalIgnoreCase) ? SearchQuery.No : NormalizeYesNo(value);

    static string? NormalizeWhere(string value) => value.ToLowerInvariant() switch
    {
        "here" or "local" or "pc" or "file" or "files" => SearchQuery.Here,
        "elsewhere" or "online" or "away" => SearchQuery.Elsewhere,
        _ => null,
    };

    /// <summary>A file format, or image, ebook or kindle for every format of that kind.</summary>
    static string? NormalizeFormat(string value) => value.ToLowerInvariant() switch
    {
        "pdf" => SourceFormats.Pdf,
        "epub" => SourceFormats.Epub,
        "mobi" or "prc" => SourceFormats.Mobi,
        "azw" => SourceFormats.Azw,
        "azw3" => SourceFormats.Azw3,
        "kfx" => SourceFormats.Kfx,
        "kindle" => "kindle",
        "ebook" or "ebooks" or "e-book" or "e-books" => "ebook",
        "jpg" or "jpeg" => SourceFormats.Jpeg,
        "png" => SourceFormats.Png,
        "webp" => SourceFormats.Webp,
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

    /// <summary>
    /// Only known names (<see cref="SearchFields"/>, and those still to come) make a field, so "http://..." or "AD:D"
    /// stay ordinary words.
    /// </summary>
    static bool IsFieldName(string name) => SearchFields.Find(name) is not null || SearchFields.IsComing(name);
}
