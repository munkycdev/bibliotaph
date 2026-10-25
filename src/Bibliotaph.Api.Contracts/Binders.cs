namespace Bibliotaph.Api.Contracts;

/// <summary>A binder's card, on the Binders page and Home: its title, date (or "At hand · …"), item count and the covers of its first two books.</summary>
public sealed record BinderCard(long Id, string Title, string Date, string Count, IReadOnlyList<BookCard> Covers);

/// <summary>
/// The Binders page's groups, each by title: upcoming, kept at hand, without a date, and earlier ones, with the words
/// of the link that shows or hides those; and the game systems a binder kept at hand can be for, "Any game system" (an empty value) first.
/// </summary>
public sealed record BinderList(IReadOnlyList<BinderCard> Upcoming, IReadOnlyList<BinderCard> AtHand, IReadOnlyList<BinderCard> Undated,
    IReadOnlyList<BinderCard> Earlier, string ShowEarlier, string HideEarlier, IReadOnlyList<FilterChoice> Systems);

/// <summary>New binder and Edit title and date: a title, a date or none, or kept at hand during games for a game system (null for any).</summary>
public sealed record BinderEdit(string Title, DateOnly? Date, bool KeepAtHand, string? GameSystem);

/// <summary>
/// A binder's page: its date as the eyebrow, title, count line and notes, its rows in order, what its dialog
/// starts with, and the headings Add section suggests.
/// </summary>
public sealed record BinderPage(long Id, string Title, string Eyebrow, string Count, DateOnly? Date, bool KeepAtHand, string? GameSystem, string Notes,
    IReadOnlyList<BinderLine> Rows, IReadOnlyList<FilterChoice> Systems, IReadOnlyList<string> SuggestedSections);

/// <summary>A row of a binder: a section's heading, or an item.</summary>
public sealed record BinderLine(BinderSection? Section, BinderItem? Item);

public sealed record BinderSection(long Id, string Name);

/// <summary>
/// An item: its number ("01"), the book's title and card, the label (blank shows the title) and note, which pages,
/// why it can't open or what opening it does differently, and where Move to section can take it.
/// </summary>
public sealed record BinderItem(long Id, string Number, string BookTitle, string Label, string Heading, string Where, string Note, BookCard? Cover,
    string? Reason, bool Dimmed, bool Changed, IReadOnlyList<BinderMove> Moves);

public sealed record BinderMove(long SectionId, string Label);

/// <summary>A section's name.</summary>
public sealed record BinderName(string Name);

public sealed record BinderItemEdit(string? Label, string? Note);

/// <summary>
/// Move up or down by <see cref="By"/>; to the end of <see cref="SectionId"/> (0 for before the first section), as
/// Move to section does, or its start with <see cref="First"/>, as a drop on its heading does; or into
/// <see cref="Onto"/>'s place, as a drop on another item does.
/// </summary>
public sealed record BinderItemMove(int By = 0, long? SectionId = null, bool First = false, long? Onto = null);

public sealed record BinderNotes(string? Notes);

/// <summary>A binder made or copied: where it opens.</summary>
public sealed record BinderCreated(long Id);

/// <summary>A binder in the picker.</summary>
public sealed record BinderChoice(long Id, string Title);

public enum BinderMenuKind
{
    /// <summary>"Add to Harbor night": the binder worked on last.</summary>
    Binder,

    /// <summary>"Add to another binder…": picks one first.</summary>
    Choose,

    /// <summary>"Add to a new binder…": names one first.</summary>
    New,
}

public sealed record BinderMenuChoice(BinderMenuKind Kind, long? Id, string Label);

/// <summary>
/// Add to binder's menu, as the Windows app's: the binder worked on last, then another (with every binder for the
/// picker) and a new one (with the game systems for its dialog); and the words of those dialogs.
/// </summary>
public sealed record BinderMenu(IReadOnlyList<BinderMenuChoice> Choices, IReadOnlyList<BinderChoice> All, IReadOnlyList<FilterChoice> Systems,
    string NewHeading, string NewAction)
{
    /// <summary>The picker's heading: "Add The Drowned Abbey to a binder".</summary>
    public static string PickHeading(string what) => $"Add {what} to a binder";
}

/// <summary>
/// What Add to binder adds: these books whole (<see cref="Title"/> names one in the note), or one page of a
/// document.
/// </summary>
public sealed record BinderAdd(IReadOnlyList<long> Books, string? Title = null, long? DocumentId = null, int? Page = null);
