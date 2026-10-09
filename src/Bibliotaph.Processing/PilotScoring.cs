using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>The fields the pilot asks about and scores (slice 2d, choice P5).</summary>
public static class PilotFields
{
    /// <summary>Every field the review page asks about, in its order.</summary>
    public static IReadOnlyList<MetadataField> Core { get; } =
        [MetadataFields.Title, MetadataFields.System, MetadataFields.Types, MetadataFields.Publisher, MetadataFields.Levels];

    /// <summary>The fields the precision and coverage gates are measured on. Levels are reported, not gated.</summary>
    public static IReadOnlyList<MetadataField> Gated { get; } =
        [MetadataFields.Title, MetadataFields.System, MetadataFields.Types, MetadataFields.Publisher];
}

/// <summary>
/// Picks the pilot's books (choice P1): spread across top folders and game systems, the same books every time for the
/// same library and seed.
/// </summary>
public static class PilotSample
{
    public const int DefaultCount = 100;
    public const int DefaultSeed = 2026;

    public static IReadOnlyList<long> Pick(IReadOnlyList<PilotCandidate> candidates, int count = DefaultCount, int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var groups = candidates
            .OrderBy(c => c.DocumentId)
            .GroupBy(c => (TopFolder(c.Folder), c.System ?? ""))
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal)
            .Select(g => new Queue<long>(g.Select(c => c.DocumentId).OrderBy(_ => random.Next())))
            .ToList();
        var picked = new List<long>(Math.Min(count, candidates.Count));
        // Round robin, so a folder holding half the library gives no more than its turn while the others last.
        while (picked.Count < count && groups.Count > 0)
            foreach (var group in groups.ToList())
            {
                if (picked.Count == count) break;
                picked.Add(group.Dequeue());
                if (group.Count == 0) groups.Remove(group);
            }
        return picked;
    }

    /// <summary>"D&amp;D 5e" from "D&amp;D 5e / Adventures": the folder hint's first folder.</summary>
    static string TopFolder(string folderHint) => folderHint.Split(" / ", 2)[0];
}

/// <summary>
/// Whether two values of a field mean the same: a term's names all count as it, titles match with or without a subtitle,
/// publishers without "Inc." or "LLC", and levels by their range.
/// </summary>
public sealed class PilotMatcher(Vocabulary vocabulary)
{
    static readonly string[] SubtitleMarks = [":", " — ", " – ", " - ", "—", "–"];
    static readonly HashSet<string> CompanyWords = ["inc", "llc", "ltd", "co", "company", "corp", "corporation", "gmbh", "publishing"];

    public bool Same(MetadataField field, string a, string b)
    {
        if (field.Kind == FieldKind.Term) return TermKey(field, a) == TermKey(field, b);
        if (field.Kind == FieldKind.Levels) return MetadataValues.Normalize(field, a) == MetadataValues.Normalize(field, b);
        if (field == MetadataFields.Title) return SameTitle(a, b);
        if (field == MetadataFields.Publisher) return Publisher(a) == Publisher(b);
        return MetadataText.Normalize(a) == MetadataText.Normalize(b);
    }

    /// <summary>A term value's key: as stored, by any of its names, or failing both its comparison form.</summary>
    public string TermKey(MetadataField field, string value) =>
        vocabulary.Find(field.Vocabulary!, value) is not null ? value
        : vocabulary.Resolve(field.Vocabulary!, value)?.Key ?? MetadataText.Normalize(value);

    /// <summary>How a value reads: a term by its label, anything else as stored.</summary>
    public string Label(MetadataField field, string value) =>
        field.Kind == FieldKind.Term && vocabulary.Find(field.Vocabulary!, value) is { } term ? term.Label : value;

    static bool SameTitle(string a, string b)
    {
        if (MetadataText.Normalize(a) == MetadataText.Normalize(b)) return true;
        // "Tomb of Annihilation" is right for "Tomb of Annihilation: A Jungle Adventure", and the other way round.
        var (mainA, hasSubA) = MainTitle(a);
        var (mainB, hasSubB) = MainTitle(b);
        return (hasSubA != hasSubB) && mainA.Length > 0 && mainA == mainB;
    }

    static (string Main, bool HasSubtitle) MainTitle(string title)
    {
        var cut = SubtitleMarks.Select(m => title.IndexOf(m, StringComparison.Ordinal)).Where(i => i > 0).DefaultIfEmpty(-1).Min();
        return cut < 0 ? (MetadataText.Normalize(title), false) : (MetadataText.Normalize(title[..cut]), true);
    }

    /// <summary>A publisher by the vocabulary's name for it, with or without "LLC", or else by its name without one.</summary>
    string Publisher(string name) =>
        (vocabulary.Resolve("publisher", name) ?? vocabulary.Resolve("publisher", Company(name)))?.Key ?? Company(name);

    static string Company(string publisher) =>
        string.Join(' ', MetadataText.Words(MetadataText.Normalize(publisher)).Where(w => !CompanyWords.Contains(w)));
}

/// <summary>One field's counts for one model: values proposed and right, and answered books it filled completely and correctly.</summary>
public sealed record PilotFieldScore(string Field, int Proposed, int Correct, int Answerable, int Filled)
{
    public double? Precision => Proposed == 0 ? null : (double)Correct / Proposed;

    public double? Coverage => Answerable == 0 ? null : (double)Filled / Answerable;
}

/// <summary>A value a model got wrong on an answered book, for the report.</summary>
public sealed record PilotMistake(long DocumentId, string Field, string Proposed, string Answer);

/// <summary>
/// One model's score (choice P6). <see cref="Precision"/> and <see cref="Coverage"/> count the gated fields;
/// <see cref="CorrectionRate"/> is the share of answered books with any wrong core value the user would have to fix.
/// </summary>
public sealed record PilotModelScore(
    string Model, int Books, int Failures, double? MedianSeconds, int Answered, int NeedCorrection,
    IReadOnlyList<PilotFieldScore> Fields, IReadOnlyList<PilotMistake> Mistakes)
{
    public const double PrecisionGate = 0.95;
    public const double CoverageGate = 0.70;
    public const double CorrectionGate = 0.20;

    IEnumerable<PilotFieldScore> GatedFields => Fields.Where(f => PilotFields.Gated.Any(g => g.Key == f.Field));

    public double? Precision => GatedFields.Sum(f => f.Proposed) is var n and > 0 ? (double)GatedFields.Sum(f => f.Correct) / n : null;

    public double? Coverage => GatedFields.Sum(f => f.Answerable) is var n and > 0 ? (double)GatedFields.Sum(f => f.Filled) / n : null;

    public double? CorrectionRate => Answered == 0 ? null : (double)NeedCorrection / Answered;

    /// <summary>Meets every gate on at least one answered book.</summary>
    public bool Passes => Precision >= PrecisionGate && Coverage >= CoverageGate && CorrectionRate < CorrectionGate;
}

/// <summary>Scores each model's proposals against the user's answers (choice P6). Pure, so the arithmetic is tested alone.</summary>
public static class PilotScoring
{
    public static IReadOnlyList<PilotModelScore> Score(
        IReadOnlyList<string> models, IReadOnlyList<PilotRun> runs, IReadOnlyList<PilotProposal> proposals,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>> answers, PilotMatcher matcher)
    {
        var scores = new List<PilotModelScore>();
        foreach (var model in models)
        {
            var modelRuns = runs.Where(r => r.Model == model).ToList();
            var kept = proposals.Where(p => p.Model == model && p.Kept).ToLookup(p => (p.DocumentId, p.Field));
            var counts = PilotFields.Core.ToDictionary(f => f.Key, _ => new int[4]);
            var mistakes = new List<PilotMistake>();
            var answered = 0;
            var needCorrection = 0;
            foreach (var run in modelRuns)
            {
                if (!answers.TryGetValue(run.DocumentId, out var bookAnswers)) continue;
                answered++;
                var wrong = false;
                foreach (var field in PilotFields.Core)
                {
                    if (!bookAnswers.TryGetValue(field.Key, out var fieldAnswers)) continue;
                    var real = fieldAnswers.Where(a => a != PilotStore.NotInBook).ToList();
                    var values = kept[(run.DocumentId, field.Key)].Select(p => p.Value).ToList();
                    // The Classify stage stores only the first value of a single-value field.
                    if (!field.Multiple) values = [.. values.Take(1)];
                    values = Distinct(field, values, matcher);
                    var right = values.Where(v => real.Any(a => matcher.Same(field, v, a))).ToList();
                    var c = counts[field.Key];
                    c[0] += values.Count;
                    c[1] += right.Count;
                    if (real.Count > 0)
                    {
                        c[2]++;
                        if (values.Count > 0 && right.Count == values.Count && real.All(a => values.Any(v => matcher.Same(field, v, a)))) c[3]++;
                    }
                    foreach (var value in values.Except(right))
                    {
                        wrong = true;
                        mistakes.Add(new PilotMistake(run.DocumentId, field.Label, matcher.Label(field, value),
                            real.Count == 0 ? "not in this book" : string.Join(", ", real.Select(a => matcher.Label(field, a)))));
                    }
                }
                if (wrong) needCorrection++;
            }
            var seconds = modelRuns.Where(r => r.Problem is null).Select(r => r.Seconds).Order().ToList();
            scores.Add(new PilotModelScore(model, modelRuns.Count, modelRuns.Count(r => r.Problem is not null), Median(seconds), answered, needCorrection,
                [.. PilotFields.Core.Select(f => new PilotFieldScore(f.Key, counts[f.Key][0], counts[f.Key][1], counts[f.Key][2], counts[f.Key][3]))],
                mistakes));
        }
        return scores;
    }

    /// <summary>The model to offer by default (choice P8): the most precise of those that pass, then the fastest.</summary>
    public static PilotModelScore? Best(IReadOnlyList<PilotModelScore> scores) =>
        scores.Where(s => s.Passes).OrderByDescending(s => s.Precision).ThenBy(s => s.MedianSeconds).FirstOrDefault();

    static List<string> Distinct(MetadataField field, List<string> values, PilotMatcher matcher)
    {
        var distinct = new List<string>();
        foreach (var value in values)
            if (!distinct.Any(d => matcher.Same(field, d, value))) distinct.Add(value);
        return distinct;
    }

    static double? Median(List<double> sorted) =>
        sorted.Count == 0 ? null : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
