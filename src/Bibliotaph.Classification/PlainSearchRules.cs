using System.Globalization;
using System.Text.RegularExpressions;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Classification;

/// <summary>
/// Ask without a chat model: reads a request by rules instead of by AI, so Ask works offline and with no AI set up.
/// It finds the vocabulary's names (longest first, a plural as its singular), levels ("level 4", "levels 3 to 5", "3rd-5th
/// level"), players ("four players", "a party of 5", "solo"), favorites and PDFs, and anything the request leaves out
/// ("not horror", "without maps"). A phrase in quotes is words a book must have. The rest of what the request says goes
/// to about, found by meaning. The claims go through <see cref="PlainSearchCheck"/> as a model's do.
/// </summary>
public static partial class PlainSearchRules
{
    /// <summary>Words that say nothing of what is wanted, trimmed from the ends of what goes to about.</summary>
    static readonly HashSet<string> Filler =
    [
        with(StringComparer.Ordinal),
        "a", "an", "the", "some", "any", "something", "anything", "stuff", "for", "with", "in", "on", "of", "about", "and", "or", "to", "at", "from",
        "that", "which", "where", "who", "is", "are", "it", "its", "set", "me", "my", "i", "we", "our", "want", "need", "looking", "find", "show",
        "get", "give", "please", "like", "kind", "sort", "book", "books", "one", "ones", "has", "have", "can", "you", "all", "good", "great",
        "but", "also", "just", "only", "maybe", "really", "very", "would", "could", "should", "be", "will", "this", "these", "those", "there",
        "here", "group", "party", "table", "run", "play", "players", "player",
    ];

    /// <summary>Words before a name that ask to leave it out: "not horror", "no maps", "without undead".</summary>
    static readonly HashSet<string> Leaving = [with(StringComparer.Ordinal), "not", "no", "without", "except", "non", "excluding", "avoid", "avoiding"];

    static readonly HashSet<string> LevelWords = [with(StringComparer.Ordinal), "level", "levels", "lvl", "lv"];

    static readonly HashSet<string> PlayerWords =
    [
        with(StringComparer.Ordinal),
        "players", "player", "pcs", "pc", "people", "characters", "character", "heroes", "adventurers",
    ];

    /// <summary>Words between the two ends of a range.</summary>
    static readonly HashSet<string> Joins = [with(StringComparer.Ordinal), "to", "or", "through", "thru", "and"];

    /// <summary>Small words skipped between a word that leaves something out and the name it leaves out: "no more undead".</summary>
    static readonly HashSet<string> Between = [with(StringComparer.Ordinal), "a", "an", "the", "any", "more"];

    /// <summary>Longest name, in words, looked for.</summary>
    const int MaxNameWords = 6;

    [GeneratedRegex("\"([^\"]+)\"|“([^”]+)”")]
    private static partial Regex Quoted();

    [GeneratedRegex("^([0-9]{1,2})(st|nd|rd|th)$")]
    private static partial Regex OrdinalPattern();

    /// <summary>Reads <paramref name="request"/> by rules and checks the reading as a model's would be.</summary>
    public static Interpretation Read(string request, PlainSearchContext context) =>
        PlainSearchCheck.Check(request, Claims(request, context.Vocabulary), context);

    /// <summary>What the rules find in a request, in the order it says them, with about last.</summary>
    internal static IReadOnlyList<PlainSearchClaim> Claims(string request, Vocabulary vocabulary)
    {
        var text = PlainSearchPrompt.Trim(request);
        // The request's words in comparison form, a null where a quoted phrase was, so no name or about runs across it.
        var words = new List<string?>();
        var found = new List<(int At, PlainSearchClaim Claim)>();
        var from = 0;
        foreach (Match quoted in Quoted().Matches(text))
        {
            AddWords(text[from..quoted.Index]);
            var leaving = words.Count > 0 && words[^1] is { } before && Leaving.Contains(before);
            if (leaving) words[^1] = null;
            found.Add((words.Count, new PlainSearchClaim(PlainSearchPrompt.Words, quoted.Groups[1].Success ? quoted.Groups[1].Value : quoted.Groups[2].Value, leaving)));
            words.Add(null);
            from = quoted.Index + quoted.Length;
        }
        AddWords(text[from..]);
        var used = words.Select(w => w is null).ToArray();

        FindLevels();
        FindPlayers();
        FindFlags();
        FindNames();

        var claims = found.OrderBy(f => f.At).Select(f => f.Claim).ToList();
        if (About() is { } about) claims.Add(new PlainSearchClaim(PlainSearchPrompt.About, about, false));
        return claims;

        void AddWords(string part) => words.AddRange(MetadataText.Words(MetadataText.Normalize(part)));

        string? Word(int at) => at >= 0 && at < words.Count && !used[at] ? words[at] : null;

        void Take(int at, int length, PlainSearchClaim claim)
        {
            used.AsSpan(at, length).Fill(true);
            found.Add((at, claim));
        }

        // "level 4", "levels 3 5" (3-5), "levels 3 to 5", "level four", "4th level", "3rd 5th level", "3rd to 5th level".
        void FindLevels()
        {
            for (var i = 0; i < words.Count; i++)
            {
                if (Word(i) is { } word && LevelWords.Contains(word) && Number(Word(i + 1)) is { } min)
                {
                    var (max, length) = RangeEnd(i + 2, Number);
                    Take(i, 2 + length, Level(min, max ?? min));
                }
                else if (Ordinal(Word(i)) is { } first)
                {
                    var (last, length) = RangeEnd(i + 1, Ordinal);
                    if (Word(i + 1 + length) is { } after && LevelWords.Contains(after)) Take(i, 2 + length, Level(first, last ?? first));
                }
            }
        }

        // "four players", "3 5 players" (3-5), "4 to 6 characters", "four level 3 characters", "a party of 5", "solo".
        void FindPlayers()
        {
            for (var i = 0; i < words.Count; i++)
            {
                if (Word(i) == "solo") Take(i, 1, new PlainSearchClaim(SearchFields.Players.Name, "1", false));
                else if (Word(i) == "party" && Word(i + 1) == "of" && Number(Word(i + 2)) is { } party)
                    Take(i, 3, new PlainSearchClaim(SearchFields.Players.Name, Format(party), false));
                else if (Number(Word(i)) is { } min)
                {
                    var (max, length) = RangeEnd(i + 1, Number);
                    // A level said between them is already taken: "four level 3 characters".
                    var after = i + 1 + length;
                    while (after < words.Count && used[after] && words[after] is not null) after++;
                    if (Word(after) is not { } noun || !PlayerWords.Contains(noun)) continue;
                    used[after] = true;
                    Take(i, 1 + length, new PlainSearchClaim(SearchFields.Players.Name, max is { } m ? $"{Format(min)}-{Format(m)}" : Format(min), false));
                }
            }
        }

        // "my favorites", "PDFs".
        void FindFlags()
        {
            for (var i = 0; i < words.Count; i++)
            {
                switch (Word(i))
                {
                    case "favorite" or "favorites" or "favourite" or "favourites" or "faves":
                        Take(i, 1, new PlainSearchClaim(SearchFields.Favorite.Name, "yes", false));
                        break;
                    case "pdf" or "pdfs":
                        Take(i, 1, new PlainSearchClaim(SearchFields.Format.Name, "pdf", LeftOut(i)));
                        break;
                }
            }
        }

        // The vocabulary's names, longest first, each word used once, in the order Ask's fields are listed.
        void FindNames()
        {
            var fields = PlainSearchPrompt.Fields.Where(f => f.Field.Kind == SearchFieldKind.Term).Select(f => f.Field).ToList();
            for (var length = Math.Min(MaxNameWords, words.Count); length >= 1; length--)
            {
                for (var at = 0; at + length <= words.Count; at++)
                {
                    if (used.AsSpan(at, length).Contains(true)) continue;
                    var phrase = string.Join(' ', words.GetRange(at, length));
                    if (length == 1 && (Filler.Contains(phrase) || Leaving.Contains(phrase))) continue;
                    foreach (var name in (string[])[phrase, Singular(phrase)])
                    {
                        if (fields.FirstOrDefault(f => vocabulary.Resolve(f.Metadata!.Vocabulary!, name) is not null) is not { } field) continue;
                        Take(at, length, new PlainSearchClaim(field.Name, name, LeftOut(at)));
                        break;
                    }
                }
            }
        }

        // Whether the words just before position at leave it out, taking them if so.
        bool LeftOut(int at)
        {
            var before = at - 1;
            while (Word(before) is { } small && Between.Contains(small)) before--;
            if (Word(before) is not { } word || !Leaving.Contains(word)) return false;
            used.AsSpan(before, at - before).Fill(true);
            return true;
        }

        // The other end of a range starting at position at: "5", "to 5", "or 5". Its value and how many words it took.
        (int? Value, int Length) RangeEnd(int at, Func<string?, int?> read)
        {
            if (read(Word(at)) is { } next) return (next, 1);
            if (Word(at) is { } join && Joins.Contains(join) && read(Word(at + 1)) is { } after) return (after, 2);
            return (null, 0);
        }

        // Everything the rules didn't take, in the request's order, without the filler at either end of each run.
        string? About()
        {
            var runs = new List<string>();
            var run = new List<string>();
            for (var i = 0; i <= words.Count; i++)
            {
                if (i < words.Count && Word(i) is { } word)
                {
                    run.Add(word);
                    continue;
                }
                var start = run.FindIndex(w => !Filler.Contains(w) && !Leaving.Contains(w));
                var end = run.FindLastIndex(w => !Filler.Contains(w) && !Leaving.Contains(w));
                if (start >= 0) runs.Add(string.Join(' ', run.GetRange(start, end - start + 1)));
                run.Clear();
            }
            var about = string.Join(' ', runs);
            if (about.Length > PlainSearchCheck.MaxAboutLength)
                about = about[..(about.LastIndexOf(' ', PlainSearchCheck.MaxAboutLength) is var space and > 0 ? space : PlainSearchCheck.MaxAboutLength)];
            return about.Length == 0 ? null : about;
        }
    }

    static PlainSearchClaim Level(int min, int max) =>
        new(SearchFields.Level.Name, min == max ? Format(min) : $"{Format(Math.Min(min, max))}-{Format(Math.Max(min, max))}", false);

    static int? Number(string? word) =>
        word is null ? null
        : int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 99 ? n
        : PlayerRange.FromWord(word);

    static int? Ordinal(string? word) =>
        word is not null && OrdinalPattern().Match(word) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    static string Format(int number) => number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The phrase with its last word singular, as the vocabulary names things: "dungeon crawls" is "dungeon crawl".</summary>
    static string Singular(string phrase) =>
        phrase.EndsWith("ies", StringComparison.Ordinal) && phrase.Length > 4 ? phrase[..^3] + "y"
        : phrase.EndsWith('s') && !phrase.EndsWith("ss", StringComparison.Ordinal) && phrase.Length > 3 ? phrase[..^1]
        : phrase;
}
