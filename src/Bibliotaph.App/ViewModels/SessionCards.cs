using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

/// <summary>A session pack's card, on the Sessions page and Home: its title, date, item count and the covers of its first two books.</summary>
public sealed class SessionCardViewModel(SessionPackInfo pack, string? dateLabel, string countLabel, IReadOnlyList<LibraryItemViewModel> covers, bool isPast)
{
    public SessionPackInfo Pack { get; } = pack;

    public long Id => Pack.Id;

    public string Title => Pack.Title;

    /// <summary>"Saturday, 17 October 2026", or "No date".</summary>
    public string DateLabel { get; } = dateLabel ?? "No date";

    public string CountLabel { get; } = countLabel;

    /// <summary>Its date has passed: it lists under Earlier.</summary>
    public bool IsPast { get; } = isPast;

    public IReadOnlyList<LibraryItemViewModel> Covers { get; } = covers;

    public bool HasCovers => Covers.Count > 0;

    public LibraryItemViewModel? FirstCover => Covers.Count > 0 ? Covers[0] : null;

    public LibraryItemViewModel? SecondCover => Covers.Count > 1 ? Covers[1] : null;

    public bool HasSecondCover => Covers.Count > 1;
}

/// <summary>A section's heading in a pack, with its actions.</summary>
public sealed class SessionSectionRow(SessionSectionInfo section)
{
    public SessionSectionInfo Section { get; } = section;

    public long Id => Section.Id;

    public string Name => Section.Name;
}

/// <summary>
/// One item in a pack (choice 12): its number, the book's cover and title, the game master's label and note, which
/// pages, and, worked out when the pack is shown, how it opens. An item that can't open stays, dimmed, saying why
/// (choice 15).
/// </summary>
public sealed partial class SessionItemRow(SessionItemInfo item, int number, string bookTitle, LibraryItemViewModel? cover, SessionItemTarget target) : ObservableObject
{
    public SessionItemInfo Item { get; } = item;

    public long Id => Item.Id;

    /// <summary>"01".</summary>
    public string Number { get; } = number.ToString("00", CultureInfo.CurrentCulture);

    public string BookTitle { get; } = bookTitle;

    public LibraryItemViewModel? Cover { get; } = cover;

    public SessionItemTarget Target { get; } = target;

    /// <summary>The label as shown and edited; blank shows the book's title.</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = item.Label ?? "";

    [ObservableProperty]
    public partial string Note { get; set; } = item.Note ?? "";

    /// <summary>Raised when the label or note was changed, so the page saves them.</summary>
    public event EventHandler? Edited;

    partial void OnLabelChanged(string value) => Edited?.Invoke(this, EventArgs.Empty);

    partial void OnNoteChanged(string value) => Edited?.Invoke(this, EventArgs.Empty);

    /// <summary>Move to section: every other section, and before the first one.</summary>
    public IReadOnlyList<SectionMove> MoveTargets { get; set; } = [];

    public bool HasMoveTargets => MoveTargets.Count > 0;

    /// <summary>The label, or the book's title when there is none.</summary>
    public string Heading => Item.Label ?? BookTitle;

    /// <summary>"The Drowned Abbey · p. 42–44", "… · Whole book".</summary>
    public string Where => $"{BookTitle} · {Pages(Item.Range)}";

    /// <summary>For the reader's counter later (3d) and the item's own line: "p. 42–44", "Whole book".</summary>
    public static string Pages(PageRange? range)
    {
        if (range is null) return "Whole book";
        var first = range.FirstLabel ?? (range.FirstPdfPage + 1).ToString(CultureInfo.CurrentCulture);
        if (range.IsSinglePage) return $"p. {first}";
        var last = range.LastLabel ?? (range.LastPdfPage + 1).ToString(CultureInfo.CurrentCulture);
        return $"p. {first}–{last}";
    }

    public bool CanOpen => Target.CanOpen;

    /// <summary>Dimmed: its file can't be reached, or it's owned elsewhere.</summary>
    public bool IsDimmed => !Target.CanOpen;

    /// <summary>The pages weren't found in the book as it is now: "This page changed: check it".</summary>
    public bool IsChanged => Target.State == SessionItemState.Changed;

    /// <summary>Why it can't open, or what opening it will do differently; null for an ordinary item.</summary>
    public string? Reason => Target.Reason;

    public bool HasReason => Reason is not null;

    public bool CanShowInFolder => Target.LastPath is not null;

    public string AccessibleName => $"{Number}. {Heading}, {Where}{(Reason is null ? "" : ". " + Reason)}";
}
