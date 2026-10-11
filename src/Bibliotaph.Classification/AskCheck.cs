using System.Globalization;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Classification;

/// <summary>
/// One chip of Ask's reading of a request: a search field and its value (<c>system: Mothership</c>), or words to find
/// when <see cref="Field"/> is null, left out of the results with <see cref="Exclude"/>. <see cref="Clause"/> is the
/// same thing in the search box's syntax.
/// </summary>
public sealed record InterpretedPart(SearchFieldInfo? Field, string Value, bool Exclude = false)
{
    public string? Clause => QueryClause.Format(Field, Value, Exclude);
}

/// <summary>
/// Ask's reading of a request, checked: the chips to show, and notes on what was changed or left out and why
/// ("Mothership has no character levels in your library, so level 4 was ignored"). <see cref="QueryText"/> is what
/// Search runs.
/// </summary>
public sealed record Interpretation(IReadOnlyList<InterpretedPart> Parts, IReadOnlyList<string> Notes)
{
    public string QueryText => QueryClause.Join(Parts.Select(p => p.Clause));
}

/// <summary>
/// What Ask checks a reading against: the vocabulary, and the game systems and editions whose books in the library
/// are all marked as having no levels (<c>("system", "mothership")</c>), so a level asked of one of them is ignored.
/// </summary>
public sealed record AskContext(Vocabulary Vocabulary, IReadOnlyCollection<(string Field, string Key)> WithoutLevels);

/// <summary>
/// The check every reading of a request goes through before it is shown (scenario review, row 5). A field the search
/// box doesn't have is dropped. A vocabulary field's value must be one of its terms by some name: one that isn't
/// becomes words to find when the user typed those words, and is dropped otherwise. Words to find, and the values of
/// free-text fields, must be words of the request (or the start of one: "terraform" for "terraforming"), so a model
/// can't add searches nobody asked for. Levels and players must read as numbers or ranges, and a level asked of a game
/// system whose books have no levels is ignored. Every change is said in a note, and each chip is parsed by the search
/// box's own parser before it is kept. Explicit filters are never loosened, and nothing unknown is filled in.
/// </summary>
public static class AskCheck
{
    /// <summary>A value longer than this is a sentence, not a search.</summary>
    const int MaxValueLength = 80;

    /// <summary>A word of a value this short must match a word of the request whole: "a" is no start of "adventure".</summary>
    const int MinWordStart = 3;

    /// <summary>What a vocabulary field's values are called in a note.</summary>
    static string Noun(SearchFieldInfo field) => field.Field switch
    {
        SearchField.System => "game system",
        SearchField.Own => "place you own books",
        _ => field.Name,
    };

    /// <summary>Fields that hold one value, so a second one would find nothing.</summary>
    static readonly HashSet<SearchField> OneValue = [SearchField.Level, SearchField.Players, SearchField.Favorite, SearchField.Format];

    public static Interpretation Check(string request, IReadOnlyList<AskClaim> claims, AskContext context)
    {
        var requestWords = MetadataText.Words(MetadataText.Normalize(AskPrompt.Trim(request)));
        var parts = new List<InterpretedPart>();
        var notes = new List<string>();
        // The term each kept vocabulary chip names, for the checks after the loop.
        var terms = new Dictionary<InterpretedPart, Term>();

        foreach (var claim in claims)
        {
            var value = MetadataText.Tidy(claim.Value);
            if (value.Length == 0) continue;
            if (value.Length > MaxValueLength)
            {
                notes.Add("Part of the answer was too long to be a search, so it was left out.");
                continue;
            }
            if (claim.Field == AskPrompt.Words)
            {
                if (FromRequest(value, requestWords)) Add(new InterpretedPart(null, value, claim.Exclude));
                else notes.Add($"“{value}” isn't in what you asked, so it was left out.");
                continue;
            }
            if (AskPrompt.Find(claim.Field) is not { Field: var field })
            {
                notes.Add($"Ask doesn't search by “{MetadataText.Tidy(claim.Field)}”, so “{value}” was left out.");
                continue;
            }
            switch (field.Kind)
            {
                case SearchFieldKind.Term:
                    CheckTerm(field, value, claim.Exclude);
                    break;
                case SearchFieldKind.Level:
                    if (LevelRange.TryParse(value, out var levels)) Add(new InterpretedPart(field, levels.ToString(), claim.Exclude));
                    else notes.Add($"“{value}” isn't a level, so it was left out.");
                    break;
                case SearchFieldKind.Players:
                    if (PlayerRange.TryParse(value, out var players)) Add(new InterpretedPart(field, players.ToString(), claim.Exclude));
                    else notes.Add($"“{value}” isn't a number of players, so it was left out.");
                    break;
                case SearchFieldKind.Format:
                    Add(new InterpretedPart(field, value.ToLowerInvariant(), claim.Exclude));
                    break;
                default:
                    // Free text (publisher, author, series, title) and yes-or-no: words the user typed, or favorite's yes.
                    if (field.Field == SearchField.Favorite || FromRequest(value, requestWords)) Add(new InterpretedPart(field, value, claim.Exclude));
                    else notes.Add($"“{value}” isn't in what you asked, so it wasn't searched for as the {field.Name}.");
                    break;
            }
        }

        DropWordsAFilterCovers();
        DropLevelsOfSystemsWithout();
        return new Interpretation(parts, notes);

        // "jungle" as words beside environment: jungle would also ask for the word in the book, and find less.
        void DropWordsAFilterCovers()
        {
            var texts = parts.Where(f => f.Field is { Kind: SearchFieldKind.Text }).Select(f => MetadataText.Normalize(f.Value)).ToHashSet(StringComparer.Ordinal);
            parts.RemoveAll(p => p.Field is null && !p.Exclude
                && (texts.Contains(MetadataText.Normalize(p.Value)) || terms.Values.Any(t => context.Vocabulary.Resolve(t.Vocabulary, p.Value) == t)));
        }

        void CheckTerm(SearchFieldInfo field, string value, bool exclude)
        {
            var vocabulary = field.Metadata!.Vocabulary!;
            var term = context.Vocabulary.Resolve(vocabulary, value);
            // An edition's name given as the system, or a system's as the edition, still names it, in its own field.
            if (term is null && field.Field == SearchField.System && context.Vocabulary.Resolve("edition", value) is { } edition)
                (field, term) = (SearchFields.Edition, edition);
            if (term is null && field.Field == SearchField.Edition && context.Vocabulary.Resolve("system", value) is { } system)
                (field, term) = (SearchFields.System, system);
            var noun = Noun(field);
            if (term is null)
            {
                if (FromRequest(value, requestWords))
                {
                    if (Add(new InterpretedPart(null, value, exclude))) notes.Add($"There's no {noun} called “{value}” in your vocabulary, so it's searched for as words.");
                }
                else notes.Add($"There's no {noun} called “{value}” in your vocabulary, and you didn't ask for it, so it was left out.");
                return;
            }
            var part = new InterpretedPart(field, term.Label, exclude);
            if (Add(part)) terms[part] = term;
        }

        // Keeps a chip the search box's parser reads as asked, once; a second value of a one-value field is left out.
        bool Add(InterpretedPart part)
        {
            if (part.Clause is not { } clause || !Parses(part, clause))
            {
                notes.Add($"“{part.Value}” couldn't be searched for, so it was left out.");
                return false;
            }
            if (parts.Any(p => p.Clause == clause)) return false;
            if (part.Field is { } field && OneValue.Contains(field.Field) && parts.Any(p => p.Field == field))
            {
                notes.Add($"Only one {field.Name} can be searched for at a time, so “{part.Value}” was left out.");
                return false;
            }
            parts.Add(part);
            return true;
        }

        // A level asked of a game system whose books all have no levels would find nothing, so it goes, with a note.
        void DropLevelsOfSystemsWithout()
        {
            var level = parts.FirstOrDefault(p => p.Field?.Field == SearchField.Level && !p.Exclude);
            if (level is null || !LevelRange.TryParse(level.Value, out var range) || range.NotApplicable) return;
            var without = terms.Where(t => !t.Key.Exclude).Select(t => t.Value).FirstOrDefault(NoLevels);
            if (without is null) return;
            parts.Remove(level);
            var asked = range.Describe();
            notes.Add($"{without.Label} has no character levels in your library, so {char.ToLower(asked[0], CultureInfo.CurrentCulture)}{asked[1..]} "
                + (range.Min == range.Max ? "was ignored." : "were ignored."));
        }

        bool NoLevels(Term term) =>
            context.WithoutLevels.Contains((term.Vocabulary, term.Key))
            || (term.Vocabulary == "edition" && term.ParentKey is { } system && context.WithoutLevels.Contains(("system", system)));
    }

    /// <summary>
    /// Whether every word of <paramref name="value"/> is a word of the request, or the start of one, so a model's words
    /// are the user's own.
    /// </summary>
    static bool FromRequest(string value, string[] requestWords)
    {
        var words = MetadataText.Words(MetadataText.Normalize(value));
        return words.Length > 0 && words.All(w => requestWords.Any(r => r == w || (w.Length >= MinWordStart && r.StartsWith(w, StringComparison.Ordinal))));
    }

    /// <summary>The search box's parser reads the clause as this chip, with nothing ignored or repaired.</summary>
    static bool Parses(InterpretedPart part, string clause)
    {
        var query = SearchQuery.Parse(clause);
        if (query.Issues.Count > 0) return false;
        var node = query.Root is NotNode not && part.Exclude ? not.Operand : query.Root;
        if (part.Exclude != query.Root is NotNode) return false;
        return part.Field is { } field ? node is FieldNode found && found.Field == field.Field : node is TermNode or PhraseNode;
    }
}
