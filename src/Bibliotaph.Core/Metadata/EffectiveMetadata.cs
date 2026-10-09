namespace Bibliotaph.Core.Metadata;

/// <summary>
/// One assertion as the policy sees it: a value for a field, where it came from, its evidence and its state.
/// <see cref="Quote"/> is what the value was read from: a quote from a page, a folder name, the PDF's own title.
/// <see cref="DecidedUtc"/> is when the user confirmed or rejected it.
/// </summary>
public sealed record MetadataClaim(
    long Id,
    string Field,
    string Value,
    string Normalized,
    AssertionOrigin Origin,
    AssertionState State,
    DateTime CreatedUtc,
    string? Quote = null,
    IReadOnlyList<int>? EvidencePages = null,
    bool FromSampling = false,
    DateTime? DecidedUtc = null)
{
    public IReadOnlyList<int> Pages => EvidencePages ?? [];

    /// <summary>When the user settled this value: its confirmation, or its creation for a value the user typed.</summary>
    public DateTime Settled => DecidedUtc ?? CreatedUtc;
}

/// <summary>A value the document has for a field, with every claim that backs it, best first.</summary>
public sealed record EffectiveValue(string Value, string Normalized, bool Confirmed, IReadOnlyList<MetadataClaim> Support)
{
    /// <summary>The claim that put this value here: the confirmation, or the highest-priority suggestion.</summary>
    public MetadataClaim Source => Support[0];

    public AssertionOrigin Origin => Source.Origin;
}

/// <summary>
/// A field's values and the suggestions that lost to them. <see cref="NeedsReview"/> is set when sources disagree
/// about a closed field (system, edition, type, levels) and nobody has settled it: there are alternatives the user
/// hasn't seen since they last decided the field (<paramref name="DecidedUtc"/>). <see cref="Disputed"/> are values
/// the user set on another copy's card that disagree with what the field shows (<see cref="AssertionState.SetAside"/>).
/// </summary>
public sealed record EffectiveField(MetadataField Field, IReadOnlyList<EffectiveValue> Values, IReadOnlyList<EffectiveValue> Alternatives, DateTime? DecidedUtc = null,
    IReadOnlyList<EffectiveValue>? DisputedValues = null)
{
    public IReadOnlyList<EffectiveValue> Disputed => DisputedValues ?? [];

    public bool NeedsReview => Field.IsClosed && Alternatives.Any(a => DecidedUtc is not { } decided || a.Source.CreatedUtc > decided);

    public bool IsKnown => Values.Count > 0;

    public bool HasConfirmed => Values.Any(v => v.Confirmed);

    public EffectiveValue? First => Values.Count > 0 ? Values[0] : null;
}

/// <summary>
/// The effective-value policy (architecture, "Metadata and classification"): a document's metadata is computed from
/// its assertions every time, never stored over them, so no source can overwrite another's claim or the user's word.
/// <list type="bullet">
/// <item>A confirmed value wins and is never replaced by a suggestion (A04). Rejected values never apply, from any
/// source (a rejection is a document, field and normalised value).</item>
/// <item>A single-value field otherwise takes the highest-priority suggestion: AI over rule hints, and among hints the
/// PDF's own information over folder names over the file name; the newest wins a tie.</item>
/// <item>A multi-value field is judged value by value: confirmed values, plus the suggestions of the highest-priority
/// source that has any. Lower sources' extra values are alternatives, not values. Once the user has decided the field,
/// only suggestions that arrived after that decision are added; the older ones stay alternatives, so confirming the
/// AI's types doesn't promote the folder's extra type the user just looked past.</item>
/// </list>
/// </summary>
public sealed class EffectiveMetadata
{
    readonly Dictionary<string, EffectiveField> _fields;

    EffectiveMetadata(Dictionary<string, EffectiveField> fields) => _fields = fields;

    public static EffectiveMetadata Empty { get; } = Compute([], []);

    /// <summary>Every field, known or not, in <see cref="MetadataFields.All"/> order.</summary>
    public IEnumerable<EffectiveField> Fields => MetadataFields.All.Select(f => _fields[f.Key]);

    public EffectiveField this[MetadataField field] => _fields[field.Key];

    public bool NeedsReview => _fields.Values.Any(f => f.NeedsReview);

    /// <summary>Suggestion priority by origin. The user's own values are always confirmed, so theirs never compete.</summary>
    public static int Priority(AssertionOrigin origin) => origin switch
    {
        AssertionOrigin.User => 100,
        AssertionOrigin.Ai => 50,
        AssertionOrigin.Rule => 30,
        AssertionOrigin.Embedded => 20,
        AssertionOrigin.Folder => 15,
        AssertionOrigin.Filename => 10,
        _ => 0,
    };

    /// <param name="claims">The document's assertions, in any state.</param>
    /// <param name="rejections">(field, normalised value) pairs the user rejected for this document.</param>
    public static EffectiveMetadata Compute(IEnumerable<MetadataClaim> claims, IEnumerable<(string Field, string Normalized)> rejections)
    {
        var rejected = rejections.ToHashSet();
        var kept = claims.Where(c => !rejected.Contains((c.Field, c.Normalized))).ToList();
        var live = kept.Where(c => c.State is AssertionState.Provisional or AssertionState.Confirmed).ToLookup(c => c.Field);
        var setAside = kept.Where(c => c.State == AssertionState.SetAside).ToLookup(c => c.Field);
        var fields = new Dictionary<string, EffectiveField>(StringComparer.Ordinal);
        foreach (var field in MetadataFields.All)
        {
            var decided = Decide(field, [.. live[field.Key]]);
            List<MetadataClaim> aside = [.. setAside[field.Key]];
            if (aside.Count > 0)
                decided = decided with { DisputedValues = [.. Group(aside, confirmed: false, aside).Where(v => decided.Values.All(c => c.Normalized != v.Normalized))] };
            fields[field.Key] = decided;
        }
        return new EffectiveMetadata(fields);
    }

    static EffectiveField Decide(MetadataField field, List<MetadataClaim> claims)
    {
        var confirmed = Group(claims.Where(c => c.State == AssertionState.Confirmed), confirmed: true, claims);
        var suggested = Group(claims.Where(c => c.State == AssertionState.Provisional), confirmed: false, claims)
            .Where(v => confirmed.All(c => c.Normalized != v.Normalized))
            .ToList();

        if (!field.Multiple)
        {
            // The latest decision stands; a new confirmation supersedes the old ones, so there is normally just one.
            if (confirmed.Count > 0)
            {
                var latest = confirmed.MaxBy(v => (v.Source.Settled, v.Source.Id))!;
                return new EffectiveField(field, [latest], [], latest.Source.Settled);
            }
            if (suggested.Count == 0) return new EffectiveField(field, [], []);
            var best = suggested[0];
            return new EffectiveField(field, [best], [.. suggested.Skip(1)]);
        }

        DateTime? decided = claims.Where(c => c.State == AssertionState.Confirmed).Select(c => (DateTime?)c.Settled).Max();
        var fresh = suggested.Where(v => decided is not { } at || v.Source.CreatedUtc > at).ToList();
        if (fresh.Count == 0) return new EffectiveField(field, confirmed, suggested, decided);
        var top = Priority(fresh[0].Origin);
        var applied = fresh.Where(v => Priority(v.Origin) == top).ToList();
        return new EffectiveField(field, [.. confirmed, .. applied], [.. suggested.Except(applied)], decided);
    }

    /// <summary>
    /// One value per normalised form, from <paramref name="picked"/>, ordered best first; its support is every live
    /// claim for that value (confirmed or not), best first, so the evidence shows all the sources that agree.
    /// </summary>
    static List<EffectiveValue> Group(IEnumerable<MetadataClaim> picked, bool confirmed, List<MetadataClaim> all) =>
        [.. picked
            .GroupBy(c => c.Normalized, StringComparer.Ordinal)
            .Select(g =>
            {
                var lead = g.OrderByDescending(Rank).First();
                var support = all.Where(c => c.Normalized == g.Key).OrderByDescending(c => c.Id == lead.Id).ThenByDescending(Rank).ToList();
                return new EffectiveValue(lead.Value, g.Key, confirmed, support);
            })
            .OrderByDescending(v => Rank(v.Source))];

    static (int, DateTime, long) Rank(MetadataClaim claim) =>
        (claim.State == AssertionState.Confirmed ? 1000 : Priority(claim.Origin), claim.CreatedUtc, claim.Id);
}

/// <summary>
/// A value a source proposes for a field, before it is stored as an assertion. <paramref name="Value"/> is in stored
/// form: a term's key, a level range ("1-5"), text as written. <paramref name="FromSampling"/> marks a value read from
/// pages sampled across the book rather than its opening pages, contents or introduction.
/// </summary>
public sealed record MetadataProposal(
    MetadataField Field, string Value, AssertionOrigin Origin, string? Quote = null, IReadOnlyList<int>? Pages = null, bool FromSampling = false)
{
    public string Normalized => MetadataValues.Normalize(Field, Value);
}
