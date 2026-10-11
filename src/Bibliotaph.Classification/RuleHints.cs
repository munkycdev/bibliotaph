using System.Text.RegularExpressions;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Classification;

/// <summary>
/// What the Hints from names stage reads for one document: where its file is under its source folder, the PDF's own
/// document information (null for images, and for PDFs that don't fill it in), and its watermark lines once its text
/// has been read (<see cref="Watermarks.Find"/>). Watermark lines name the buyer, so only the store they name is kept.
/// <see cref="Rules"/> are the folder rules switched on in its library folder (scenario review, row 8).
/// </summary>
public sealed record HintSource(
    string RelativePath,
    string? EmbeddedTitle = null,
    string? EmbeddedAuthor = null,
    string? EmbeddedSubject = null,
    string? EmbeddedKeywords = null,
    string? EmbeddedProducer = null,
    IReadOnlyList<string>? WatermarkLines = null,
    IReadOnlyList<FolderRuleHint>? Rules = null);

/// <summary>
/// A folder rule the user switched on (scenario review, row 8): files in <see cref="Folder"/> (a path under their
/// library folder, "" for all of it) and its subfolders get <see cref="Value"/>, in stored form, as a suggestion.
/// </summary>
public sealed record FolderRuleHint(string Folder, MetadataField Field, string Value);

/// <summary>A vocabulary term a folder name stands for, as Settings > Library lists it.</summary>
public sealed record FolderLabel(string Folder, Term Term);

/// <summary>
/// Rule hints: provisional metadata from folder names, the file name and the PDF's own information, with no model
/// involved. They are the weakest suggestions (below AI, far below the user) and exist so a library is usable
/// before, or without, AI.
/// <list type="bullet">
/// <item>Folder names count only through the vocabulary: "D&amp;D 5e/Adventures" gives the 5e edition (and so D&amp;D)
/// and the Adventure type. Deeper folders win for single-value fields. A label the user switched off is skipped.</item>
/// <item>The PDF's title and authors are used unless they look like leftovers from the software that made it
/// ("Microsoft Word - draft3.docx", "Administrator"). Its keywords count only through the vocabulary.</item>
/// <item>The file name gives a title, and level ranges and party sizes written out in a PDF's name, title or subject
/// ("Levels 1-4", "3rd-level", "for 4-6 players") become rule suggestions.</item>
/// <item>Where to get it (scenario review, row 7) comes from a store named in a folder name, the file name, the PDF's
/// producer or a watermark line. Only the store is kept: the quote is the store's name as matched, never the line it
/// was in, which holds the buyer's name and order number.</item>
/// <item>A folder rule the user switched on gives its value to every file in its folder and below; for a field with one
/// value, the deepest folder's rule wins. Its quote is the folder's name.</item>
/// </list>
/// </summary>
public static partial class RuleHints
{
    /// <summary>Which field each vocabulary's terms fill.</summary>
    public static MetadataField? FieldFor(string vocabulary) => vocabulary switch
    {
        "system" => MetadataFields.System,
        "edition" => MetadataFields.Edition,
        "type" => MetadataFields.Types,
        "setting" => MetadataFields.Settings,
        "duration" => MetadataFields.Duration,
        "style" => MetadataFields.Styles,
        "theme" => MetadataFields.Themes,
        "environment" => MetadataFields.Environments,
        "publisher" => MetadataFields.Publisher,
        "store" => MetadataFields.GetItFrom,
        _ => null,
    };

    public static IReadOnlyList<MetadataProposal> Propose(
        HintSource source, Vocabulary vocabulary, IReadOnlySet<(string Folder, string Vocabulary, string Key)>? ignored = null)
    {
        var single = new Dictionary<(MetadataField, AssertionOrigin), MetadataProposal>();
        var multi = new List<MetadataProposal>();

        void Add(MetadataProposal proposal)
        {
            if (proposal.Field.Multiple) multi.Add(proposal);
            else single[(proposal.Field, proposal.Origin)] = proposal; // from one source, the later (deeper, more specific) wins
        }

        foreach (var folder in FolderNames(source.RelativePath))
        {
            foreach (var label in Labels(folder, vocabulary, ignored))
                foreach (var proposal in TermProposals(label.Term, vocabulary, AssertionOrigin.Folder, folder))
                    Add(proposal);
        }

        // Shallow rules first, so a deeper folder's wins for a field with one value.
        foreach (var rule in (source.Rules ?? []).Where(r => new FolderKey(0, r.Folder).Holds(0, source.RelativePath))
                     .OrderBy(r => r.Folder.Length == 0 ? 0 : r.Folder.Count(c => c is '\\' or '/') + 1))
            Add(new MetadataProposal(rule.Field, rule.Value, AssertionOrigin.FolderRule, new FolderKey(0, rule.Folder).Name is { Length: > 0 } name ? name : null));

        var fileName = Path.GetFileName(source.RelativePath);
        Add(new MetadataProposal(MetadataFields.Title, DisplayTitle.FromFileName(fileName), AssertionOrigin.Filename, fileName));

        if (Clean(source.EmbeddedTitle) is { } title && !IsJunkTitle(title))
            Add(new MetadataProposal(MetadataFields.Title, title, AssertionOrigin.Embedded, source.EmbeddedTitle));

        foreach (var author in Authors(source.EmbeddedAuthor))
            Add(new MetadataProposal(MetadataFields.Authors, author, AssertionOrigin.Embedded, source.EmbeddedAuthor));

        if (Clean(source.EmbeddedKeywords) is { } keywords)
            foreach (var match in vocabulary.Match(keywords))
                foreach (var proposal in TermProposals(match.Term, vocabulary, AssertionOrigin.Embedded, keywords))
                    Add(proposal);

        // The file name is the most specific place a level range is written, so it is looked at last. Images are left
        // out, and so are folder names: there "Level 2" is far more often a dungeon level than a character level.
        if (!SourceFormats.IsImage(SourceFormats.FromFileName(fileName) ?? ""))
            foreach (var text in new[] { source.EmbeddedSubject, source.EmbeddedTitle, Path.GetFileNameWithoutExtension(fileName) })
                if (Clean(text) is { } t)
                {
                    if (FindLevels(t) is { } levels) Add(new MetadataProposal(MetadataFields.Levels, levels.Range.ToString(), AssertionOrigin.Rule, levels.Quote));
                    if (FindPlayers(t) is { } players) Add(new MetadataProposal(MetadataFields.Players, players.Range.ToString(), AssertionOrigin.Rule, players.Quote));
                }

        // Where to get it: from the file name, the PDF's producer and the watermark, by the store's name only.
        foreach (var store in Stores(Path.GetFileNameWithoutExtension(fileName), vocabulary))
            Add(new MetadataProposal(MetadataFields.GetItFrom, store.Term.Key, AssertionOrigin.Filename, store.Matched));
        foreach (var store in Stores(source.EmbeddedProducer, vocabulary))
            Add(new MetadataProposal(MetadataFields.GetItFrom, store.Term.Key, AssertionOrigin.Embedded, store.Matched));
        foreach (var store in (source.WatermarkLines ?? []).SelectMany(line => Stores(line, vocabulary)))
            Add(new MetadataProposal(MetadataFields.GetItFrom, store.Term.Key, AssertionOrigin.Rule, store.Matched));

        return [.. single.Values, .. multi.DistinctBy(p => (p.Field, p.Normalized, p.Origin))];
    }

    /// <summary>
    /// The stores a text names, each with the words that named it and nothing more, so a watermark's buyer and order
    /// number never become a quote. A site name in a link counts: "www.drivethrurpg.com".
    /// </summary>
    internal static IEnumerable<TermMatch> Stores(string? text, Vocabulary vocabulary)
    {
        if (Clean(text) is not { } clean) return [];
        var store = MetadataFields.GetItFrom.Vocabulary!;
        return vocabulary.Match(clean).Where(m => m.Term.Vocabulary == store).DistinctBy(m => m.Term.Key);
    }

    /// <summary>The folders between the source folder and the file, outermost first.</summary>
    public static IReadOnlyList<string> FolderNames(string relativePath) =>
        (Path.GetDirectoryName(relativePath) ?? "").Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The terms a folder name stands for, leaving out those the user switched off.</summary>
    public static IEnumerable<FolderLabel> Labels(string folder, Vocabulary vocabulary, IReadOnlySet<(string Folder, string Vocabulary, string Key)>? ignored = null)
    {
        var normalized = MetadataText.Normalize(folder);
        return vocabulary.Match(folder)
            .Where(m => FieldFor(m.Term.Vocabulary) is not null && ignored?.Contains((normalized, m.Term.Vocabulary, m.Term.Key)) != true)
            .Select(m => new FolderLabel(folder, m.Term));
    }

    /// <summary>A term as a suggestion for its field; an edition also suggests its game system.</summary>
    static IEnumerable<MetadataProposal> TermProposals(Term term, Vocabulary vocabulary, AssertionOrigin origin, string quote)
    {
        if (FieldFor(term.Vocabulary) is not { } field) yield break;
        if (term.Vocabulary == "edition" && term.ParentKey is { } parent && vocabulary.Find("system", parent) is not null)
            yield return new MetadataProposal(MetadataFields.System, parent, origin, quote);
        // A publisher is free text that the vocabulary only tidies, so it is stored by name rather than key.
        yield return new MetadataProposal(field, field.Kind is FieldKind.Term or FieldKind.Store ? term.Key : term.Label, origin, quote);
    }

    static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : MetadataText.Tidy(text);

    [GeneratedRegex(@"^(microsoft (word|powerpoint|excel)\b|untitled|document\d*$|book\d*$|title$|new document|layout \d|cover$|pdf$)", RegexOptions.IgnoreCase)]
    private static partial Regex JunkTitleStart();

    [GeneratedRegex(@"\.(indd|idml|docx?|odt|rtf|txt|qxd|qxp|pmd|pdf|psd|ai|pub|tex|dvi|ps)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtension();

    /// <summary>Titles authoring software leaves behind rather than ones a publisher wrote.</summary>
    internal static bool IsJunkTitle(string title)
    {
        var normalized = MetadataText.Normalize(title);
        if (normalized.Length < 3 || normalized.All(c => char.IsDigit(c) || c == ' ')) return true;
        if (JunkTitleStart().IsMatch(title) || FileExtension().IsMatch(title)) return true;
        // "CoS_Cover_v3" and "dnd5e-lmop-final": a file name, not a title.
        return !title.Contains(' ', StringComparison.Ordinal) && (title.Contains('_', StringComparison.Ordinal) || title.Count(c => c == '-') >= 2);
    }

    static readonly HashSet<string> JunkAuthors =
    [
        with(StringComparer.Ordinal),
        "administrator", "admin", "user", "owner", "unknown", "author", "default", "microsoft", "adobe", "windows user", "authorised user",
        "authorized user", "staff", "test", "me", "pc", "home",
    ];

    [GeneratedRegex(@"\s*(?:[,;/]|\band\b|&)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorSeparators();

    /// <summary>The PDF's author entry split into people, leaving out placeholders and e-mail addresses.</summary>
    internal static IEnumerable<string> Authors(string? author)
    {
        if (Clean(author) is not { } text) return [];
        return AuthorSeparators().Split(text)
            .Select(MetadataText.Tidy)
            .Where(a => MetadataText.Normalize(a) is { Length: >= 3 } n && !JunkAuthors.Contains(n) && !a.Contains('@', StringComparison.Ordinal)
                && a.Any(char.IsLetter) && !FileExtension().IsMatch(a))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\b(?:levels?|lvls?\.?|lv\.?)\s*(\d{1,2})(?:\s*(?:-|–|—|to|thru|through)\s*(\d{1,2}))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex LevelWord();

    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)(?:\s*(?:-|–|—|to|through)\s*(\d{1,2})(?:st|nd|rd|th))?[\s-]*levels?\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalLevel();

    const string Count = @"\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve";

    [GeneratedRegex(@"\b(" + Count + @")(?:\s*(?:-|–|—|to|or)\s*(" + Count + @"))?\s+(?:players?|pcs?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerCount();

    /// <summary>"The Seven Heroes" is a title, so characters and heroes count only after "for".</summary>
    [GeneratedRegex(@"\bfor (" + Count + @")(?:\s*(?:-|–|—|to|or)\s*(" + Count + @"))?\s+(?:characters?|adventurers|heroes)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForCharacters();

    [GeneratedRegex(@"\b(?:party|group) of (" + Count + @")(?:\s*(?:-|–|—|to|or)\s*(" + Count + @"))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex PartyOf();

    [GeneratedRegex(@"\b(?:solo|single[- ]player|one[- ]player)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Solo();

    /// <summary>
    /// A party size written out: "for 4-6 players", "for four to six characters", "a party of 4", "solo". "3rd-level
    /// characters" is a level, not a count, so only plain numbers count.
    /// </summary>
    internal static (PlayerRange Range, string Quote)? FindPlayers(string text)
    {
        var spaced = text.Replace('_', ' ');
        foreach (var pattern in new[] { PlayerCount(), ForCharacters(), PartyOf() })
        {
            var match = pattern.Match(spaced);
            if (!match.Success) continue;
            var range = match.Groups[2].Success ? $"{match.Groups[1].Value} to {match.Groups[2].Value}" : match.Groups[1].Value;
            if (PlayerRange.TryParse(range, out var players)) return (players, match.Value.Trim());
        }
        return Solo().Match(spaced) is { Success: true } solo ? (new PlayerRange(1, 1), solo.Value) : null;
    }

    /// <summary>A level range written out in a name: "Levels 1-4", "Lvl 3", "for 5th-level characters", "1st to 4th level".</summary>
    internal static (LevelRange Range, string Quote)? FindLevels(string text)
    {
        var spaced = text.Replace('_', ' ');
        foreach (var pattern in new[] { LevelWord(), OrdinalLevel() })
        {
            var match = pattern.Match(spaced);
            if (!match.Success) continue;
            var range = match.Groups[2].Success ? $"{match.Groups[1].Value}-{match.Groups[2].Value}" : match.Groups[1].Value;
            if (LevelRange.TryParse(range, out var levels) && !levels.NotApplicable) return (levels, match.Value.Trim());
        }
        return null;
    }
}
