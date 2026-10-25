using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>A line of the summary shown before a bulk edit applies, with a warning when it overrides the user's own values.</summary>
public sealed record BulkSummaryLine(string Field, string Text, string? Warning)
{
    public bool HasWarning => Warning is not null;
}

/// <summary>
/// A value typed or picked in the bulk editor as it will be filed: a known term's key with its label, or the name of a
/// new term; text tidied, with a publisher's usual spelling.
/// </summary>
public sealed record BulkValue(string Value, string Normalized, string Text, bool IsNew);

/// <summary>The bulk editor's words, in the Windows app and on the web.</summary>
public static class BulkEditText
{
    public const string Eyebrow = "EDIT METADATA";

    public const string Intro = "Change any field but the title. What you set is confirmed on every book; fields you don't touch stay as they are.";

    public const string ReviewIntro = "Check what will change before it's applied. You can undo it afterwards.";

    public const string UndoFailed = "The edit couldn't be undone.";

    public static string Heading(int books) => $"Edit {SelectionRules.Books(books)}";

    public static string ApplyLabel(int books) => $"Apply to {SelectionRules.Books(books)}";

    /// <summary>"Updating search: 120 of 400 books", while it applies.</summary>
    public static string Updating(int done, int total) => $"Updating search: {done.ToString("N0", CultureInfo.CurrentCulture)} of {SelectionRules.Books(total)}";

    /// <summary>One transaction: when saving fails, nothing was changed.</summary>
    public static string SaveFailed(string message) => $"The changes couldn't be saved: {message}";
}

/// <summary>
/// The bulk editor for the books ticked in Select mode: every field but the title, at once. A field nobody touches
/// isn't changed; what is changed is confirmed on every book, by the inspector's rules.
/// </summary>
public sealed class BulkEditPlan
{
    BulkEditPlan(IReadOnlyList<EntryId> books, IReadOnlyDictionary<EntryId, EffectiveMetadata> metadata, Vocabulary vocabulary)
    {
        Books = books;
        Vocabulary = vocabulary;
        Fields = [.. MetadataFields.All.Where(f => f != MetadataFields.Title).Select(field =>
        {
            IReadOnlyList<EffectiveField> values = [.. books.Select(id => metadata[id][field])];
            return field.Multiple ? (BulkField)new BulkChipsField(field, values, vocabulary) : new BulkValueField(field, values, vocabulary);
        })];
    }

    public IReadOnlyList<EntryId> Books { get; }

    public Vocabulary Vocabulary { get; }

    public IReadOnlyList<BulkField> Fields { get; }

    public string Heading => BulkEditText.Heading(Books.Count);

    public string ApplyLabel => BulkEditText.ApplyLabel(Books.Count);

    public static async Task<BulkEditPlan> LoadAsync(MetadataService metadata, IReadOnlyList<EntryId> books, CancellationToken ct = default)
    {
        var (all, vocabulary) = await metadata.GetManyAsync(books, ct);
        return new BulkEditPlan(books, all, vocabulary);
    }
}

/// <summary>A field in the bulk editor: what the ticked books have in it, and how a typed value is read.</summary>
public abstract class BulkField(MetadataField field, Vocabulary vocabulary)
{
    public MetadataField Field { get; } = field;

    public string Label => Field.Label;

    /// <summary>The field's name inside a sentence: "game system", "type".</summary>
    protected string Lower => Field.Label.ToLower(CultureInfo.CurrentCulture);

    protected Vocabulary Vocabulary { get; } = vocabulary;

    /// <summary>Vocabulary labels to pick from, for a term field.</summary>
    public IReadOnlyList<string> Choices { get; } = FieldText.Choices(field, vocabulary);

    /// <summary>"new type", "new setting": how a name the vocabulary doesn't know is shown until it is added.</summary>
    public string NewNote => NewNoteFor(Field);

    public static string NewNoteFor(MetadataField field) => $"new {(field == MetadataFields.AlsoOwn ? "place" : field.Vocabulary)}";

    /// <summary>How a value reads: a term's label, "Levels 1–5", a store with its link, the text itself.</summary>
    protected string Display(string value) => FieldText.DisplayWithLink(Field, value, Vocabulary);

    /// <summary>The value <paramref name="typed"/> will be filed as, or why it can't be stored.</summary>
    public static (BulkValue? Value, string? Problem) Read(MetadataField field, string typed, Vocabulary vocabulary)
    {
        if (MetadataService.Check(field, typed) is { } problem) return (null, problem.Message);
        var (value, text, isNew) = Filed(field, typed, vocabulary);
        return (Given(field, value, text, isNew), null);
    }

    /// <summary>A value as <see cref="Read"/> gave it, coming back to be described or applied.</summary>
    public static BulkValue Given(MetadataField field, string value, string text, bool isNew) =>
        new(value, isNew ? MetadataText.Normalize(value) : MetadataValues.Normalize(field, value), text, isNew);

    static (string Value, string Text, bool IsNew) Filed(MetadataField field, string typed, Vocabulary vocabulary)
    {
        if (field.Kind == FieldKind.Store)
        {
            // Filed as the inspector files it when applied: a known store by key with its link, a new one as typed.
            var (store, name, url) = StoreLink.Read(typed, vocabulary, field);
            if (store is null) return (MetadataText.Tidy(typed), url is null ? name : $"{name} · {url}", true);
            var link = new StoreLink(store.Key, url).ToString();
            return (link, FieldText.DisplayWithLink(field, link, vocabulary), false);
        }
        if (field.Kind == FieldKind.Term)
            return vocabulary.Resolve(field.Vocabulary!, typed) is { } term ? (term.Key, term.Label, false) : (MetadataText.Tidy(typed), MetadataText.Tidy(typed), true);
        var value = MetadataValues.Parse(field, typed).Value!;
        if (field.Vocabulary is { } aliases && vocabulary.Resolve(aliases, value) is { } known) value = known.Label;
        return (value, FieldText.DisplayWithLink(field, value, vocabulary), false);
    }
}

/// <summary>
/// A single-value field (system, edition, levels, publisher, series, year) across the selection: the value they
/// share, "Mixed (3 values)", or "Not set". Setting a value confirms it on every book; Back to suggestions forgets
/// the user's own value on all of them.
/// </summary>
public sealed class BulkValueField : BulkField
{
    readonly IReadOnlyList<EffectiveField> _books;

    public BulkValueField(MetadataField field, IReadOnlyList<EffectiveField> books, Vocabulary vocabulary) : base(field, vocabulary)
    {
        _books = books;
        var groups = books.Where(b => b.IsKnown).GroupBy(b => b.First!.Normalized)
            .Select(g => (Text: Display(g.First().First!.Value), Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        var unset = books.Count(b => !b.IsKnown);
        IsKnown = groups.Count > 0;
        Current = groups.Count switch
        {
            0 => "Not set",
            1 when unset == 0 => groups[0].Text,
            1 => $"{groups[0].Text} on {groups[0].Count.ToString("N0", CultureInfo.CurrentCulture)} of {SelectionRules.Books(books.Count)}",
            _ => $"Mixed ({groups.Count.ToString("N0", CultureInfo.CurrentCulture)} values)",
        };
        Breakdown = groups.Count < 2 ? "" : string.Join(" · ", groups.Select(g => $"{g.Text} {g.Count.ToString("N0", CultureInfo.CurrentCulture)}")
            .Append(unset > 0 ? $"not set {unset.ToString("N0", CultureInfo.CurrentCulture)}" : null).OfType<string>());
        Own = books.Count(b => b.HasConfirmed);
        StartText = IsKnown && Breakdown.Length == 0 && books.All(b => b.IsKnown) ? Current : "";
    }

    /// <summary>What the books have now: the shared value, "Dungeons &amp; Dragons on 5 of 12 books", "Mixed (3 values)", "Not set".</summary>
    public string Current { get; }

    /// <summary>For a mixed field, each value with how many books have it.</summary>
    public string Breakdown { get; }

    public bool IsKnown { get; }

    /// <summary>Books whose value here is the user's own (or one they kept), which Back to suggestions would forget.</summary>
    public int Own { get; }

    /// <summary>What the box starts with: the value every book shares, or nothing.</summary>
    public string StartText { get; }

    public string EditHint => Field.Kind switch
    {
        FieldKind.Levels => "3, 1-5 or n/a",
        FieldKind.Players => "4 or 3-5",
        FieldKind.Year => "2019",
        _ => "",
    };

    /// <summary>When Set on every book is pressed with nothing typed.</summary>
    public string Blank => $"Type a value, or Cancel to leave the {Lower} as it is.";

    public const string ResetPending = "Back to suggestions on every book";

    /// <summary>"Will be Pathfinder on every book".</summary>
    public string WillBe(BulkValue set) => WillBe(Field, set);

    public static string WillBe(MetadataField field, BulkValue set) => $"Will be {set.Text}{(set.IsNew ? $" ({NewNoteFor(field)})" : "")} on every book";

    public IEnumerable<BulkChange> Changes(BulkValue? set, bool reset)
    {
        if (set is not null) yield return new BulkChange(Field, BulkAction.Set, set.Value);
        else if (reset) yield return new BulkChange(Field, BulkAction.Reset);
    }

    /// <summary>The summary's line for this field: what changes, on how many books, and whose values it overrides.</summary>
    public IEnumerable<BulkSummaryLine> Describe(BulkValue? set, bool reset)
    {
        if (set is null)
        {
            if (reset)
                yield return new BulkSummaryLine(Label, $"Back to suggestions on {SelectionRules.Books(Own)}.", $"Forgets your own {Lower} on {SelectionRules.Books(Own)}.");
            yield break;
        }
        // A new term matches nothing yet, so it changes every book.
        var normalized = set.IsNew ? null : MetadataValues.Normalize(Field, set.Value);
        var touched = _books.Count(b => b.First is not { Confirmed: true } first || first.Normalized != normalized);
        var replaced = _books.Count(b => b.HasConfirmed && b.First!.Normalized != normalized);
        var text = set.IsNew ? $"{set.Text} ({NewNote})" : set.Text;
        yield return touched == 0
            ? new BulkSummaryLine(Label, $"Already {text} on every book; nothing changes.", null)
            : new BulkSummaryLine(Label, $"Set to {text} on {SelectionRules.Books(touched)}.",
                replaced > 0 ? $"Replaces your own {Lower} on {SelectionRules.Books(replaced)}." : null);
    }
}

/// <summary>
/// A value of a multi-value field in the bulk editor: on how many of the books it is, and how many of those are the
/// user's own. One typed in the editor is on none of them yet.
/// </summary>
public sealed record BulkChip(string Value, string Normalized, string Text, string? NewNote, int Count, int Confirmed)
{
    public bool IsNew => NewNote is not null;

    /// <summary>Typed in the editor: no book has it yet, so applying can only add it.</summary>
    public bool IsTyped => Count == 0;
}

/// <summary>
/// A multi-value field (type, authors, setting, themes, environments, tags) across the selection: every value any
/// book has, as a chip with a count. Adding puts a value on every book, removing takes it off every book (rejected,
/// so no source suggests it again), and clicking a partial chip puts it on all of them.
/// </summary>
public sealed class BulkChipsField : BulkField
{
    public BulkChipsField(MetadataField field, IReadOnlyList<EffectiveField> books, Vocabulary vocabulary) : base(field, vocabulary)
    {
        Books = books.Count;
        Chips = [.. books.SelectMany(b => b.Values).GroupBy(v => v.Normalized)
            .OrderByDescending(g => g.Count()).ThenBy(g => Display(g.First().Value), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new BulkChip(g.First().Value, g.Key, Display(g.First().Value), null, g.Count(), g.Count(v => v.Confirmed)))];
    }

    public int Books { get; }

    /// <summary>Every value any of the books has, the most common first.</summary>
    public IReadOnlyList<BulkChip> Chips { get; }

    public string AddLabel => AddLabelFor(Field);

    public static string AddLabelFor(MetadataField field) => $"Add to {field.Label.ToLower(CultureInfo.CurrentCulture)}";

    /// <summary>How to type values to add: "Separate values with commas", or how a store and its link go.</summary>
    public string AddHint => Field.Kind == FieldKind.Store ? FieldText.LinkHint(Field) : "Separate values with commas";

    /// <summary>The values typed, comma-separated, or why one of them can't be stored.</summary>
    public static (IReadOnlyList<BulkValue> Values, string? Problem) ReadMany(MetadataField field, string typed, Vocabulary vocabulary)
    {
        var parts = MetadataValues.Split(field, typed);
        foreach (var part in parts)
            if (MetadataService.Check(field, part) is { } problem)
                return ([], problem.Message);
        var values = new List<BulkValue>();
        foreach (var part in parts)
        {
            var (value, problem) = Read(field, part, vocabulary);
            if (value is null) return ([], problem);
            values.Add(value);
        }
        return (values, null);
    }

    /// <summary>The chip a value goes on: one some books have already, or a new chip on none of them.</summary>
    public BulkChip ChipFor(BulkValue value) =>
        Chips.FirstOrDefault(c => c.Normalized == value.Normalized && c.IsNew == value.IsNew) ?? Typed(Field, value);

    public static BulkChip Typed(MetadataField field, BulkValue value) =>
        new(value.Value, value.Normalized, value.Text, value.IsNew ? NewNoteFor(field) : null, 0, 0);

    /// <summary>"3 of 12", or what it will be: "12 of 12" when added, "0 of 12" when removed.</summary>
    public static string CountText(BulkChip chip, BulkAction? change, int books) =>
        $"{(change == BulkAction.Add ? books : change == BulkAction.Remove ? 0 : chip.Count).ToString("N0", CultureInfo.CurrentCulture)} of {books.ToString("N0", CultureInfo.CurrentCulture)}";

    /// <summary>For screen readers and the tooltip.</summary>
    public static string Description(BulkChip chip, BulkAction? change, int books) => change switch
    {
        BulkAction.Add when chip.IsTyped => $"{chip.Text}{(chip.IsNew ? $", {chip.NewNote}," : "")} will be added to every book",
        BulkAction.Add => $"{chip.Text}, on {chip.Count:N0} of {books:N0} books, will be added to every book",
        BulkAction.Remove => $"{chip.Text}, will be removed from {chip.Count:N0} books",
        _ when chip.Count < books => $"{chip.Text}, on {chip.Count:N0} of {books:N0} books. Click to add it to every book",
        _ => $"{chip.Text}, on every book",
    };

    public static string RemoveName(BulkChip chip, BulkAction? change) =>
        chip.IsTyped ? $"Don't add {chip.Text}" : change == BulkAction.Remove ? $"Keep {chip.Text} as it is" : $"Remove {chip.Text} from every book";

    public IEnumerable<BulkChange> Changes(IEnumerable<(BulkChip Chip, BulkAction Change)> changes) =>
        changes.Select(c => new BulkChange(Field, c.Change, c.Chip.Value));

    /// <summary>The summary's lines for this field: what is added or removed, on how many books, and whose values it takes away.</summary>
    public IEnumerable<BulkSummaryLine> Describe(IEnumerable<(BulkChip Chip, BulkAction Change)> changes)
    {
        foreach (var (chip, change) in changes)
        {
            var name = chip.IsNew ? $"{chip.Text} ({chip.NewNote})" : chip.Text;
            if (change == BulkAction.Add)
                yield return new BulkSummaryLine(Label, $"Add {name} to {SelectionRules.Books(Books - chip.Confirmed)}.", null);
            else if (change == BulkAction.Remove)
                yield return new BulkSummaryLine(Label, $"Remove {name} from {SelectionRules.Books(chip.Count)}.",
                    chip.Confirmed > 0 ? $"Takes away your own {name} on {SelectionRules.Books(chip.Confirmed)}." : null);
        }
    }
}
