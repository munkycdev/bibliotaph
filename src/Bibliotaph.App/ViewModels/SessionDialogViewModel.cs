using System.Globalization;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// What the session dialog closed with: a title and date (New session, Edit), a name (a section), the pack picked, or
/// the pages to add with their label, pack and section.
/// </summary>
public sealed record SessionDialogResult(string Title, DateOnly? Date, long? PackId, int FirstPage = 0, int LastPage = 0, string? Label = null, long? SectionId = null);

/// <summary>What the session dialog asks for.</summary>
public enum SessionDialogKind
{
    /// <summary>A title and an optional date: New session, Edit.</summary>
    Naming,

    /// <summary>Just a name: a section.</summary>
    Section,

    /// <summary>Which pack, from all of them: Add to another session.</summary>
    Picking,

    /// <summary>Add pages…: From and To, a label, the pack and the section.</summary>
    Pages,
}

/// <summary>
/// The dialog over a page for session packs (slice 3 plan, choices 11 to 13): naming one, naming a section, picking a
/// pack, or Add pages… from the reader.
/// </summary>
public sealed partial class SessionDialogViewModel : ObservableObject
{
    readonly IReadOnlyList<SessionPackInfo> _packs;
    readonly Func<string, int?>? _findPage;
    readonly Func<long, Task<IReadOnlyList<SessionSectionInfo>>>? _sections;

    SessionDialogViewModel(SessionDialogKind kind, string heading, string actionText, IReadOnlyList<SessionPackInfo> packs,
        Func<string, int?>? findPage = null, Func<long, Task<IReadOnlyList<SessionSectionInfo>>>? sections = null)
    {
        Kind = kind;
        Heading = heading;
        ActionText = actionText;
        _packs = packs;
        _findPage = findPage;
        _sections = sections;
        Shown = packs;
        PackChoices = [.. packs.Select(p => new Choice<long>(p.Id, p.Title))];
    }

    /// <summary>Asks for a title and an optional date, filled in for Edit.</summary>
    public static SessionDialogViewModel Naming(string heading, string actionText, string title = "", DateOnly? date = null) =>
        new(SessionDialogKind.Naming, heading, actionText, []) { Title = title, DateText = date?.ToString("d", CultureInfo.CurrentCulture) ?? "" };

    /// <summary>Asks for a section's name.</summary>
    public static SessionDialogViewModel NamingSection(string heading, string actionText, string name = "") =>
        new(SessionDialogKind.Section, heading, actionText, []) { Title = name };

    /// <summary>Asks which pack, from <paramref name="packs"/>, the current one first.</summary>
    public static SessionDialogViewModel Picking(string heading, IReadOnlyList<SessionPackInfo> packs) =>
        new(SessionDialogKind.Picking, heading, "", packs);

    /// <summary>
    /// Add pages… (choice 13): From and To as the reader names pages (printed labels or PDF page numbers, read by
    /// <paramref name="findPage"/>), a label, and which pack and section, the current pack's end first.
    /// </summary>
    public static SessionDialogViewModel Pages(IReadOnlyList<SessionPackInfo> packs, string from, string to, string label, Func<string, int?> findPage,
        Func<long, Task<IReadOnlyList<SessionSectionInfo>>> sections)
    {
        var dialog = new SessionDialogViewModel(SessionDialogKind.Pages, "Add pages", "Add", packs, findPage, sections)
        {
            From = from,
            To = to,
            Label = label,
        };
        dialog.Pack = dialog.PackChoices.Count > 0 ? dialog.PackChoices[0] : null;
        return dialog;
    }

    /// <summary>Raised when the dialog closes: with what was chosen, or null when it was cancelled.</summary>
    public event EventHandler<SessionDialogResult?>? Closed;

    public SessionDialogKind Kind { get; }

    public string Eyebrow => Kind == SessionDialogKind.Section ? "SECTION" : "BINDER";

    public string Heading { get; }

    public string ActionText { get; }

    public bool IsNaming => Kind is SessionDialogKind.Naming or SessionDialogKind.Section;

    public bool ShowsDate => Kind == SessionDialogKind.Naming;

    public bool IsPicking => Kind == SessionDialogKind.Picking;

    public bool IsPages => Kind == SessionDialogKind.Pages;

    public string TitleLabel => Kind == SessionDialogKind.Section ? "Name" : "Title";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Title { get; set; } = "";

    /// <summary>The date as typed, in the reader's own format; blank for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string DateText { get; set; } = "";

    public string? DateProblem => ShowsDate && DateText.Trim().Length > 0 && ParseDate() is null ? "That isn't a date Bibliotaph can read, such as 17/10/2026." : null;

    DateOnly? ParseDate() =>
        DateOnly.TryParse(DateText.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date
        : DateTime.TryParse(DateText.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var time) ? DateOnly.FromDateTime(time) : null;

    /// <summary>Words that narrow the picker's list to the packs whose title has them.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatch))]
    public partial IReadOnlyList<SessionPackInfo> Shown { get; private set; }

    public bool HasNoMatch => IsPicking && Shown.Count == 0;

    partial void OnFilterChanged(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Shown = words.Length == 0 ? _packs : [.. _packs.Where(p => words.All(w => p.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)))];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string From { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string To { get; set; } = "";

    [ObservableProperty]
    public partial string Label { get; set; } = "";

    public IReadOnlyList<Choice<long>> PackChoices { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial Choice<long>? Pack { get; set; }

    /// <summary>Where in the pack: its end (null), before any section (0), or a section.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<long?>> SectionChoices { get; private set; } = [EndOfPack];

    [ObservableProperty]
    public partial Choice<long?>? Section { get; set; } = EndOfPack;

    static readonly Choice<long?> EndOfPack = new(null, "At the end");

    async partial void OnPackChanged(Choice<long>? value)
    {
        SectionChoices = [EndOfPack];
        Section = EndOfPack;
        if (value is null || _sections is null) return;
        var sections = await _sections(value.Value);
        if (Pack != value || sections.Count == 0) return;
        SectionChoices = [EndOfPack, new(SessionStore.NoSection, "Before the first section"), .. sections.Select(s => new Choice<long?>(s.Id, s.Name))];
        Section = EndOfPack;
    }

    public string? PagesProblem => IsPages && (From.Trim().Length > 0 || To.Trim().Length > 0) && Range() is null
        ? "Type pages as the book numbers them, or PDF page numbers." : null;

    (int First, int Last)? Range()
    {
        if (_findPage is null || _findPage(From) is not { } first) return null;
        var last = To.Trim().Length == 0 ? first : _findPage(To);
        return last is { } l ? (Math.Min(first, l), Math.Max(first, l)) : null;
    }

    bool CanSave => Kind switch
    {
        SessionDialogKind.Naming => Title.Trim().Length > 0 && DateProblem is null,
        SessionDialogKind.Section => Title.Trim().Length > 0,
        SessionDialogKind.Pages => Pack is not null && Range() is not null,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanSave))]
    void Save()
    {
        if (IsPages && Range() is { } range)
            Closed?.Invoke(this, new SessionDialogResult("", null, Pack!.Value, range.First, range.Last, Label.Trim() is { Length: > 0 } l ? l : null, Section?.Value));
        else if (IsNaming)
            Closed?.Invoke(this, new SessionDialogResult(Title.Trim(), ShowsDate ? ParseDate() : null, null));
    }

    /// <summary>A click or Enter on a pack in the list.</summary>
    [RelayCommand]
    void Pick(SessionPackInfo pack) => Closed?.Invoke(this, new SessionDialogResult(pack.Title, pack.Date, pack.Id));

    /// <summary>Enter in the filter box picks the first pack it leaves.</summary>
    [RelayCommand]
    void PickFirst()
    {
        if (Shown.Count > 0) Pick(Shown[0]);
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);
}
