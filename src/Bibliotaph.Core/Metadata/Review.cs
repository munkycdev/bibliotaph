using System.Text.RegularExpressions;

namespace Bibliotaph.Core.Metadata;

/// <summary>Why a field of a document is in Needs review.</summary>
public enum ReviewKind
{
    /// <summary>Sources disagree about a closed field (system, edition, type, levels) and nobody has settled it.</summary>
    Conflict,

    /// <summary>No usable title from any source: none at all, or only one like "IMG 0042".</summary>
    MissingTitle,

    /// <summary>A suggestion nobody has confirmed, shown only when the user chose to review all suggestions.</summary>
    Suggestion,
}

/// <summary>
/// One card in Needs review: a field of a document, what it shows now and what the card proposes.
/// <list type="bullet">
/// <item>Accept makes <see cref="Suggested"/> the field's values, confirmed.</item>
/// <item>Reject rejects the <see cref="Proposed"/> values (from every source, now and later) and confirms
/// <see cref="Current"/>, so the field is settled either way.</item>
/// </list>
/// </summary>
public sealed record ReviewIssue(MetadataField Field, ReviewKind Kind, IReadOnlyList<EffectiveValue> Current, IReadOnlyList<EffectiveValue> Suggested)
{
    /// <summary>The values the card adds or changes to: in <see cref="Suggested"/> but not in <see cref="Current"/>.</summary>
    public IReadOnlyList<EffectiveValue> Proposed => [.. Suggested.Where(s => Current.All(c => c.Normalized != s.Normalized))];

    public bool CanAccept => Proposed.Count > 0;

    /// <summary>The claim behind the first proposed value, which the card quotes.</summary>
    public MetadataClaim? Evidence => Proposed.Count > 0 ? Proposed[0].Source : null;

    /// <summary>
    /// Cards with the same key propose the same values for the same field, so "Accept all" can take them together
    /// (spec 5.5: bulk actions only over genuinely comparable cases).
    /// </summary>
    public string GroupKey => $"{Field.Key}|{Kind}|{string.Join('|', Proposed.Select(p => p.Normalized).Order(StringComparer.Ordinal))}";
}

/// <summary>
/// What goes to Needs review (slice 2 plan, choice 4). By default only what a person has to look at: sources that
/// disagree about a closed field, and a document without a usable title. New vocabulary terms are reviewed once per
/// term, not per document, so they aren't here. With <c>reviewAll</c>, every value nobody has confirmed is too.
/// </summary>
public static class MetadataReview
{
    public static IReadOnlyList<ReviewIssue> Find(EffectiveMetadata metadata, bool reviewAll)
    {
        var issues = new List<ReviewIssue>();
        foreach (var field in metadata.Fields)
        {
            if (field.NeedsReview) issues.Add(Conflict(field));
            else if (field.Field == MetadataFields.Title && !field.HasConfirmed && !(field.First is { } title && TitleQuality.IsUsable(title.Value)))
                issues.Add(new ReviewIssue(field.Field, ReviewKind.MissingTitle, field.Values, field.Values));
            else if (reviewAll && field.Values.Any(v => !v.Confirmed))
                issues.Add(new ReviewIssue(field.Field, ReviewKind.Suggestion, [.. field.Values.Where(v => v.Confirmed)], field.Values));
        }
        return issues;
    }

    /// <summary>
    /// The alternatives the user hasn't seen since they last decided the field, against what it shows: a single field
    /// offers the best of them instead; a multi-value field offers them as well.
    /// </summary>
    static ReviewIssue Conflict(EffectiveField field)
    {
        var fresh = field.Alternatives.Where(a => field.DecidedUtc is not { } decided || a.Source.CreatedUtc > decided).ToList();
        IReadOnlyList<EffectiveValue> suggested = field.Field.Multiple ? [.. field.Values, .. fresh] : [fresh[0]];
        return new ReviewIssue(field.Field, ReviewKind.Conflict, field.Values, suggested);
    }
}

/// <summary>Whether a title is one a person would know the book by, rather than a scanner's or a download's name.</summary>
public static partial class TitleQuality
{
    [GeneratedRegex(@"^(scan|scanned|img|image|dsc|dscn|photo|document|doc|file|untitled|download|book|pdf|page|copy|new)( ?\d+)*$")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[0-9a-f]{16,}$")]
    private static partial Regex Hash();

    public static bool IsUsable(string title)
    {
        var normalized = MetadataText.Normalize(title);
        var letters = normalized.Count(char.IsLetter);
        var digits = normalized.Count(char.IsDigit);
        return letters >= 3 && letters >= digits && !Placeholder().IsMatch(normalized) && !Hash().IsMatch(normalized.Replace(" ", "", StringComparison.Ordinal));
    }
}
