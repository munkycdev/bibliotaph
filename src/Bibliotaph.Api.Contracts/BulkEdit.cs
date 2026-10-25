namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Edit metadata for the ticked books: the dialog's words and every field but the title, with what the books have
/// in it now.
/// </summary>
public sealed record BulkEditForm(string Eyebrow, string Heading, string ApplyLabel, string Intro, string ReviewIntro, int Books,
    IReadOnlyList<BulkFieldForm> Fields);

/// <summary>
/// A field across the ticked books. A single-value field has <see cref="Current"/> ("Mixed (3 values)"), its
/// breakdown, and whether Back to suggestions has anything to forget; a multi-value field has its values as chips.
/// </summary>
public sealed record BulkFieldForm(string Key, string Label, bool Multiple, string Current, string Breakdown, bool IsKnown, bool CanReset,
    string StartText, string EditHint, string Blank, string ResetPending, string AddLabel, string AddHint, IReadOnlyList<string> Choices,
    IReadOnlyList<BulkChipForm> Chips);

/// <summary>
/// A value in the bulk editor: what is stored (a term's key, text as written, or a new term's name), how it reads,
/// and its count and words as it is, once added to every book, and once removed from every book. A typed one is on
/// none of the books yet.
/// </summary>
public sealed record BulkChipForm(string Value, string Normalized, string Text, string? NewNote, bool Typed, bool Partial,
    BulkChipLook Idle, BulkChipLook Adding, BulkChipLook Removing);

/// <summary>A chip's count ("3 of 12"), its description for screen readers and the tooltip, and what its × does.</summary>
public sealed record BulkChipLook(string Count, string Description, string RemoveName);

/// <summary>A value typed or picked for <see cref="Field"/>; <see cref="Whole"/> when picked, so a comma in it doesn't split it.</summary>
public sealed record BulkReadRequest(string Field, string Text, int Books, bool Whole);

/// <summary>
/// What was typed, as it will be filed: one value for a single-value field with what it will say ("Will be
/// Pathfinder on every book"), or a chip for each value typed; or why it can't be stored.
/// </summary>
public sealed record BulkReadResult(string? Problem, string Pending, IReadOnlyList<BulkChipForm> Values);

public enum BulkEditAction
{
    Set,
    Add,
    Remove,
    Reset,
}

/// <summary>One change: a value set, added or removed (as <see cref="BulkChipForm"/> gave it), or a field back to suggestions.</summary>
public sealed record BulkFieldChange(string Field, BulkEditAction Action, string? Value = null, string? Text = null, string? Normalized = null, bool IsNew = false);

public sealed record BulkEditRequest(IReadOnlyList<long> EntryIds, IReadOnlyList<BulkFieldChange> Changes);

/// <summary>A line of Review changes, with a warning when it overrides the user's own values.</summary>
public sealed record BulkSummaryItem(string Field, string Text, string? Warning);

/// <summary>What applying did, with its Undo; or why it couldn't, with nothing changed.</summary>
public sealed record BulkApplyResult(string? Problem, ActionResult? Done);
