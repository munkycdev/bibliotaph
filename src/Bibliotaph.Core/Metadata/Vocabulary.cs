namespace Bibliotaph.Core.Metadata;

/// <summary>
/// A term in a vocabulary: a game system, an edition (whose <see cref="ParentKey"/> is its system), a document type,
/// a setting, a theme, an environment, or a publisher's canonical name. <see cref="ShortLabel"/> is what a cover
/// card shows ("D&amp;D", "5e").
/// </summary>
public sealed record Term(string Vocabulary, string Key, string Label, string? ShortLabel = null, string? ParentKey = null)
{
    public string Brief => ShortLabel ?? Label;
}

/// <summary>A term found in a piece of text, with the words that matched it.</summary>
public sealed record TermMatch(Term Term, string Matched);

/// <summary>
/// The terms and aliases in catalog.db, loaded once and read many times: resolving what someone typed, matching
/// folder names, labelling stored keys. A term's label, short label and key all count as aliases.
/// </summary>
public sealed class Vocabulary
{
    /// <summary>Longest alias, in words, that phrase matching looks for.</summary>
    const int MaxAliasWords = 6;

    readonly Dictionary<(string Vocabulary, string Key), Term> _terms = [];
    readonly Dictionary<(string Vocabulary, string Alias), Term> _byAlias = [];
    readonly Dictionary<string, List<Term>> _anyByAlias = new(StringComparer.Ordinal);

    public Vocabulary(IEnumerable<Term> terms, IEnumerable<(string Vocabulary, string Key, string Alias)>? aliases = null)
    {
        foreach (var term in terms)
        {
            _terms[(term.Vocabulary, term.Key)] = term;
            AddAlias(term, term.Label);
            if (term.ShortLabel is { } shortLabel) AddAlias(term, shortLabel);
            AddAlias(term, term.Key);
        }
        foreach (var (vocabulary, key, alias) in aliases ?? [])
            if (_terms.TryGetValue((vocabulary, key), out var term)) AddAlias(term, alias);
    }

    public static Vocabulary Empty { get; } = new([]);

    public IEnumerable<Term> Terms => _terms.Values;

    /// <summary>Every alias in comparison form, with its term, for copying into index.db's search tables.</summary>
    public IEnumerable<(string Alias, Term Term)> Aliases => _byAlias.Select(a => (a.Key.Alias, a.Value));

    void AddAlias(Term term, string alias)
    {
        var normalized = MetadataText.Normalize(alias);
        if (normalized.Length == 0) return;
        // The first term to claim an alias keeps it, so a user's term can't steal a starter alias by accident.
        if (!_byAlias.TryAdd((term.Vocabulary, normalized), term)) return;
        if (!_anyByAlias.TryGetValue(normalized, out var list)) _anyByAlias[normalized] = list = [];
        list.Add(term);
    }

    public Term? Find(string vocabulary, string key) => _terms.GetValueOrDefault((vocabulary, key));

    public IEnumerable<Term> InVocabulary(string vocabulary) => _terms.Values.Where(t => t.Vocabulary == vocabulary);

    /// <summary>A stored key as it reads, or the key itself for a term that has since gone.</summary>
    public string Label(string vocabulary, string key) => Find(vocabulary, key)?.Label ?? key;

    public string ShortLabel(string vocabulary, string key) => Find(vocabulary, key)?.Brief ?? key;

    /// <summary>The term in <paramref name="vocabulary"/> that <paramref name="text"/> names exactly (by alias), if any.</summary>
    public Term? Resolve(string vocabulary, string text) => _byAlias.GetValueOrDefault((vocabulary, MetadataText.Normalize(text)));

    /// <summary>
    /// Terms named in a short text such as a folder name or a keyword list, in any vocabulary. Matching is by whole
    /// words, longest phrase first, and a word is used by at most one phrase: "D&amp;D 5e Adventures" gives the
    /// 5e edition (alias "D&amp;D 5e") and the Adventure type, not D&amp;D and 5e separately as well.
    /// </summary>
    public IReadOnlyList<TermMatch> Match(string text)
    {
        var words = MetadataText.Words(MetadataText.Normalize(text));
        var used = new bool[words.Length];
        var found = new List<(int At, TermMatch Match)>();
        for (var length = Math.Min(MaxAliasWords, words.Length); length >= 1; length--)
        {
            for (var start = 0; start + length <= words.Length; start++)
            {
                if (used.AsSpan(start, length).Contains(true)) continue;
                var phrase = string.Join(' ', words, start, length);
                if (!_anyByAlias.TryGetValue(phrase, out var terms)) continue;
                foreach (var term in terms) found.Add((start, new TermMatch(term, phrase)));
                used.AsSpan(start, length).Fill(true);
            }
        }
        return [.. found.OrderBy(f => f.At).Select(f => f.Match)];
    }
}
