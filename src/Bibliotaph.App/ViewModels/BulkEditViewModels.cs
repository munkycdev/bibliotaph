using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>What a bulk edit changed: the fields' snapshots from before, which undo it, and how many books changed.</summary>
public sealed record BulkEditResult(IReadOnlyList<FieldSnapshot> Undo, int Books);

/// <summary>A line of the summary shown before a bulk edit applies, with a warning when it overrides the user's own values.</summary>
public sealed record BulkSummaryLine(string Field, string Text, string? Warning)
{
    public bool HasWarning => Warning is not null;
}

public enum BulkEditStep
{
    Editing,
    Summary,
    Applying,
}

/// <summary>
/// Edit metadata for the books ticked in Select mode (slice 4e): every field but the title, at once. A field nobody
/// touches isn't changed; what is changed is confirmed on every book, by the inspector's rules. Review changes shows
/// what will happen first, and calls out where it overrides values the user set themselves.
/// </summary>
public sealed partial class BulkEditViewModel : ObservableObject
{
    readonly MetadataService _metadata;
    readonly IReadOnlyList<EntryId> _documents;

    BulkEditViewModel(IReadOnlyList<EntryId> documents, IReadOnlyDictionary<EntryId, EffectiveMetadata> metadata, Vocabulary vocabulary, MetadataService service)
    {
        _documents = documents;
        _metadata = service;
        Fields = [.. MetadataFields.All.Where(f => f != MetadataFields.Title).Select(field =>
        {
            IReadOnlyList<EffectiveField> values = [.. documents.Select(id => metadata[id][field])];
            return field.Multiple
                ? (BulkFieldViewModel)new BulkChipsFieldViewModel(field, values, vocabulary, OnFieldChanged)
                : new BulkValueFieldViewModel(field, values, vocabulary, OnFieldChanged);
        })];
    }

    /// <summary>Raised when the dialog closes: with what was changed, or null when it was cancelled.</summary>
    public event EventHandler<BulkEditResult?>? Closed;

    public static async Task<BulkEditViewModel> LoadAsync(IReadOnlyList<EntryId> documents, MetadataService metadata)
    {
        var (all, vocabulary) = await Task.Run(() => metadata.GetManyAsync(documents));
        return new BulkEditViewModel(documents, all, vocabulary, metadata);
    }

    public int BookCount => _documents.Count;

    public string Heading => $"Edit {Books(BookCount)}";

    public string ApplyLabel => $"Apply to {Books(BookCount)}";

    public IReadOnlyList<BulkFieldViewModel> Fields { get; }

    public bool HasChanges => Fields.Any(f => f.HasChange);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingStep), nameof(IsSummaryStep), nameof(IsApplyingStep))]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand), nameof(ApplyCommand), nameof(BackCommand), nameof(CancelCommand))]
    public partial BulkEditStep Step { get; private set; }

    public bool IsEditingStep => Step == BulkEditStep.Editing;

    public bool IsSummaryStep => Step == BulkEditStep.Summary;

    public bool IsApplyingStep => Step == BulkEditStep.Applying;

    [ObservableProperty]
    public partial IReadOnlyList<BulkSummaryLine> Summary { get; private set; } = [];

    /// <summary>"Updating search: 120 of 400 books", while it applies.</summary>
    [ObservableProperty]
    public partial string Progress { get; private set; } = "";

    /// <summary>Why the edit couldn't be applied, shown on the editing step.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    void OnFieldChanged()
    {
        OnPropertyChanged(nameof(HasChanges));
        ReviewCommand.NotifyCanExecuteChanged();
    }

    bool CanReview => IsEditingStep && HasChanges;

    /// <summary>Shows what will change, field by field, before anything is saved (plan choice 6).</summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    void Review()
    {
        Summary = [.. Fields.SelectMany(f => f.Describe())];
        Problem = null;
        Step = BulkEditStep.Summary;
    }

    [RelayCommand(CanExecute = nameof(IsSummaryStep))]
    void Back() => Step = BulkEditStep.Editing;

    [RelayCommand(CanExecute = nameof(IsSummaryStep))]
    async Task Apply()
    {
        Step = BulkEditStep.Applying;
        Progress = "Saving…";
        var progress = new Progress<(int Done, int Total)>(p =>
            Progress = $"Updating search: {p.Done.ToString("N0", CultureInfo.CurrentCulture)} of {Books(p.Total)}");
        IReadOnlyList<BulkChange> changes = [.. Fields.SelectMany(f => f.Changes())];
        try
        {
            var (problem, undo) = await Task.Run(() => _metadata.EditManyAsync(_documents, changes, progress));
            if (problem is not null)
            {
                Problem = problem.Message;
                Step = BulkEditStep.Editing;
                return;
            }
            Closed?.Invoke(this, new BulkEditResult(undo!, undo!.Select(s => s.EntryId).Distinct().Count()));
        }
        catch (Exception ex)
        {
            // One transaction: when saving fails, nothing was changed.
            Problem = $"The changes couldn't be saved: {ex.Message}";
            Step = BulkEditStep.Editing;
        }
    }

    bool CanCancel => !IsApplyingStep;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel() => Closed?.Invoke(this, null);

    /// <summary>Esc: from the summary back to editing; otherwise closes the dialog, unless it is applying.</summary>
    [RelayCommand]
    void Escape()
    {
        if (IsSummaryStep) Step = BulkEditStep.Editing;
        else if (IsEditingStep) Cancel();
    }

    internal static string Books(int count) => count == 1 ? "1 book" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} books";
}

/// <summary>A field in the bulk editor: what the selected books have in it, and the change to make.</summary>
public abstract partial class BulkFieldViewModel(MetadataField field, Vocabulary vocabulary, Action changed) : ObservableObject
{
    public MetadataField Field { get; } = field;

    public string Label => Field.Label;

    /// <summary>The field's name inside a sentence: "game system", "type".</summary>
    protected string Lower => Field.Label.ToLower(CultureInfo.CurrentCulture);

    protected Vocabulary Vocabulary { get; } = vocabulary;

    public abstract bool HasChange { get; }

    /// <summary>What applying does to this field, for the store. Values are what was typed or picked, or a term's key.</summary>
    public abstract IEnumerable<BulkChange> Changes();

    /// <summary>The summary's lines for this field: what changes, on how many books, and whose values it overrides.</summary>
    public abstract IEnumerable<BulkSummaryLine> Describe();

    /// <summary>Vocabulary labels to pick from, for a term field.</summary>
    public IReadOnlyList<string> Choices { get; } = MetadataFieldViewModel.ChoicesFor(field, vocabulary);

    public bool UsesChoices => Choices.Count > 0;

    [ObservableProperty]
    public partial string? Problem { get; protected set; }

    /// <summary>2a's hint while typing a term: "“One shot” is another name for Adventure", or that it is new.</summary>
    [ObservableProperty]
    public partial string Resolution { get; private set; } = "";

    protected void Changed()
    {
        OnPropertyChanged(nameof(HasChange));
        changed();
    }

    protected void Resolve(string? typed) => Resolution = MetadataFieldViewModel.DescribeResolution(Field, typed, Vocabulary);

    /// <summary>How a value reads: a term's label, "Levels 1–5", the text itself.</summary>
    protected string Display(string value) => MetadataValueViewModel.Display(Field, value, Vocabulary);

    /// <summary>
    /// A typed value as the bulk edit will file it: a known term's key and label, or the name of a new term; text
    /// tidied, with a publisher's usual spelling. Null with <see cref="Problem"/> set when it can't be stored.
    /// </summary>
    protected (string Value, string Text, bool IsNew)? Read(string typed)
    {
        if (MetadataService.Check(Field, typed) is { } problem)
        {
            Problem = problem.Message;
            return null;
        }
        Problem = null;
        if (Field.Kind == FieldKind.Term)
            return Vocabulary.Resolve(Field.Vocabulary!, typed) is { } term ? (term.Key, term.Label, false) : (MetadataText.Tidy(typed), MetadataText.Tidy(typed), true);
        var value = MetadataValues.Parse(Field, typed).Value!;
        if (Field.Vocabulary is { } aliases && Vocabulary.Resolve(aliases, value) is { } known) value = known.Label;
        return (value, Display(value), false);
    }

    /// <summary>"new type", "new setting": how a name the vocabulary doesn't know is shown until it is added.</summary>
    protected string NewNote => $"new {Field.Vocabulary}";
}

/// <summary>
/// A single-value field (system, edition, levels, publisher, series, year) across the selection (plan choice 3): the
/// value they share, "Mixed (3 values)", or "Not set". Setting a value confirms it on every book; Back to suggestions
/// forgets the user's own value on all of them.
/// </summary>
public sealed partial class BulkValueFieldViewModel : BulkFieldViewModel
{
    readonly IReadOnlyList<EffectiveField> _books;
    (string Value, string Text, bool IsNew)? _set;
    bool _reset;

    public BulkValueFieldViewModel(MetadataField field, IReadOnlyList<EffectiveField> books, Vocabulary vocabulary, Action changed)
        : base(field, vocabulary, changed)
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
            1 => $"{groups[0].Text} on {groups[0].Count.ToString("N0", CultureInfo.CurrentCulture)} of {BulkEditViewModel.Books(books.Count)}",
            _ => $"Mixed ({groups.Count.ToString("N0", CultureInfo.CurrentCulture)} values)",
        };
        Breakdown = groups.Count < 2 ? "" : string.Join(" · ", groups.Select(g => $"{g.Text} {g.Count.ToString("N0", CultureInfo.CurrentCulture)}")
            .Append(unset > 0 ? $"not set {unset.ToString("N0", CultureInfo.CurrentCulture)}" : null).OfType<string>());
        Own = books.Count(b => b.HasConfirmed);
    }

    /// <summary>What the books have now: the shared value, "Dungeons &amp; Dragons on 5 of 12 books", "Mixed (3 values)", "Not set".</summary>
    public string Current { get; }

    /// <summary>For a mixed field, each value with how many books have it.</summary>
    public string Breakdown { get; }

    public bool IsKnown { get; }

    /// <summary>Books whose value here is the user's own (or one they kept), which Back to suggestions would forget.</summary>
    int Own { get; }

    public override bool HasChange => _set is not null || _reset;

    /// <summary>"Will be Pathfinder on every book", "Back to suggestions on every book".</summary>
    public string PendingText => _set is { } set ? $"Will be {set.Text}{(set.IsNew ? $" ({NewNote})" : "")} on every book"
        : _reset ? "Back to suggestions on every book" : "";

    public bool ShowEdit => !HasChange && !IsEditing;

    public bool ShowReset => Own > 0 && !HasChange && !IsEditing;

    public string EditHint => Field.Kind switch
    {
        FieldKind.Levels => "3, 1-5 or n/a",
        FieldKind.Year => "2019",
        _ => "",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEdit), nameof(ShowReset))]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    partial void OnEditTextChanged(string value) => Resolve(value);

    /// <summary>A label picked from <see cref="BulkFieldViewModel.Choices"/>, which replaces the text.</summary>
    [ObservableProperty]
    public partial string? PickedChoice { get; set; }

    partial void OnPickedChoiceChanged(string? value)
    {
        if (value is null) return;
        EditText = value;
        PickedChoice = null;
    }

    [RelayCommand]
    void Edit()
    {
        EditText = IsKnown && Breakdown.Length == 0 && _books.All(b => b.IsKnown) ? Current : "";
        Problem = null;
        IsEditing = true;
    }

    [RelayCommand]
    void CancelEdit()
    {
        IsEditing = false;
        Problem = null;
    }

    [RelayCommand]
    void Save()
    {
        if (string.IsNullOrWhiteSpace(EditText))
        {
            Problem = $"Type a value, or Cancel to leave the {Lower} as it is.";
            return;
        }
        if (Read(EditText) is not { } set) return;
        _set = set;
        _reset = false;
        IsEditing = false;
        Update();
    }

    [RelayCommand]
    void Reset()
    {
        _reset = true;
        _set = null;
        Update();
    }

    /// <summary>Takes back the change, so the field is left as it is.</summary>
    [RelayCommand]
    void Undo()
    {
        _set = null;
        _reset = false;
        Update();
    }

    void Update()
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(ShowEdit));
        OnPropertyChanged(nameof(ShowReset));
        Changed();
    }

    public override IEnumerable<BulkChange> Changes()
    {
        if (_set is { } set) yield return new BulkChange(Field, BulkAction.Set, set.Value);
        else if (_reset) yield return new BulkChange(Field, BulkAction.Reset);
    }

    public override IEnumerable<BulkSummaryLine> Describe()
    {
        if (_reset)
        {
            yield return new BulkSummaryLine(Label, $"Back to suggestions on {BulkEditViewModel.Books(Own)}.",
                $"Forgets your own {Lower} on {BulkEditViewModel.Books(Own)}.");
            yield break;
        }
        if (_set is not { } set) yield break;
        // A new term matches nothing yet, so it changes every book.
        var normalized = set.IsNew ? null : MetadataValues.Normalize(Field, set.Value);
        var touched = _books.Count(b => b.First is not { Confirmed: true } first || first.Normalized != normalized);
        var replaced = _books.Count(b => b.HasConfirmed && b.First!.Normalized != normalized);
        var text = set.IsNew ? $"{set.Text} ({NewNote})" : set.Text;
        yield return touched == 0
            ? new BulkSummaryLine(Label, $"Already {text} on every book; nothing changes.", null)
            : new BulkSummaryLine(Label, $"Set to {text} on {BulkEditViewModel.Books(touched)}.",
                replaced > 0 ? $"Replaces your own {Lower} on {BulkEditViewModel.Books(replaced)}." : null);
    }
}

/// <summary>
/// A multi-value field (type, authors, setting, themes, environments, tags) across the selection (plan choice 4):
/// every value any book has, as a chip with a count. Adding puts a value on every book, removing takes it off every
/// book (rejected, so no source suggests it again), and clicking a partial chip puts it on all of them.
/// </summary>
public sealed partial class BulkChipsFieldViewModel : BulkFieldViewModel
{
    readonly int _books;

    public BulkChipsFieldViewModel(MetadataField field, IReadOnlyList<EffectiveField> books, Vocabulary vocabulary, Action changed)
        : base(field, vocabulary, changed)
    {
        _books = books.Count;
        foreach (var group in books.SelectMany(b => b.Values).GroupBy(v => v.Normalized).OrderByDescending(g => g.Count()).ThenBy(g => Display(g.First().Value), StringComparer.CurrentCultureIgnoreCase))
            Chips.Add(new BulkChipViewModel(this, group.First().Value, group.Key, Display(group.First().Value), null, group.Count(), group.Count(v => v.Confirmed), _books));
        Chips.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChips));
    }

    public ObservableCollection<BulkChipViewModel> Chips { get; } = [];

    public bool HasChips => Chips.Count > 0;

    public override bool HasChange => Chips.Any(c => c.Change is not null);

    public string AddLabel => $"Add to {Lower}";

    [ObservableProperty]
    public partial string AddText { get; set; } = "";

    partial void OnAddTextChanged(string value) => Resolve(value);

    /// <summary>A label picked from <see cref="BulkFieldViewModel.Choices"/>: it is added straight away.</summary>
    [ObservableProperty]
    public partial string? PickedChoice { get; set; }

    partial void OnPickedChoiceChanged(string? value)
    {
        if (value is null) return;
        Add(value);
        PickedChoice = null;
    }

    /// <summary>Adds what was typed, comma-separated, to every book: as chips showing the term each will be filed as.</summary>
    [RelayCommand]
    void AddTyped()
    {
        var parts = MetadataValues.Split(Field, AddText ?? "");
        if (parts.Count == 0) return;
        foreach (var part in parts)
            if (MetadataService.Check(Field, part) is { } problem)
            {
                Problem = problem.Message;
                return;
            }
        foreach (var part in parts) Add(part);
        AddText = "";
    }

    void Add(string typed)
    {
        if (Read(typed) is not { } value) return;
        var normalized = value.IsNew ? MetadataText.Normalize(value.Value) : MetadataValues.Normalize(Field, value.Value);
        var chip = Chips.FirstOrDefault(c => c.Normalized == normalized && c.IsNew == value.IsNew);
        // Typing a value some books have already is the same as clicking its chip.
        if (chip is null)
            Chips.Add(chip = new BulkChipViewModel(this, value.Value, normalized, value.Text, value.IsNew ? NewNote : null, 0, 0, _books));
        chip.Change = BulkAction.Add;
    }

    internal void Remove(BulkChipViewModel chip)
    {
        Chips.Remove(chip);
        Changed();
    }

    internal void ChipChanged() => Changed();

    public override IEnumerable<BulkChange> Changes() =>
        Chips.Where(c => c.Change is not null).Select(c => new BulkChange(Field, c.Change!.Value, c.Value));

    public override IEnumerable<BulkSummaryLine> Describe()
    {
        foreach (var chip in Chips)
        {
            var name = chip.IsNew ? $"{chip.Text} ({chip.NewNote})" : chip.Text;
            if (chip.Change == BulkAction.Add)
                yield return new BulkSummaryLine(Label, $"Add {name} to {BulkEditViewModel.Books(_books - chip.Confirmed)}.", null);
            else if (chip.Change == BulkAction.Remove)
                yield return new BulkSummaryLine(Label, $"Remove {name} from {BulkEditViewModel.Books(chip.Count)}.",
                    chip.Confirmed > 0 ? $"Takes away your own {name} on {BulkEditViewModel.Books(chip.Confirmed)}." : null);
        }
    }
}

/// <summary>
/// A value of a multi-value field in the bulk editor: on how many of the books it is, and whether applying adds it
/// to all of them or removes it from all of them.
/// </summary>
public sealed partial class BulkChipViewModel(BulkChipsFieldViewModel owner, string value, string normalized, string text, string? newNote,
    int count, int confirmed, int total) : ObservableObject
{
    /// <summary>The stored value (a term's key, text as written), or a new term's name.</summary>
    public string Value { get; } = value;

    public string Normalized { get; } = normalized;

    /// <summary>As it reads: the term it will be filed as.</summary>
    public string Text { get; } = text;

    /// <summary>"new type", for a name the vocabulary doesn't know yet; null otherwise.</summary>
    public string? NewNote { get; } = newNote;

    public bool IsNew => NewNote is not null;

    /// <summary>Typed in the editor: no book has it yet, so applying can only add it.</summary>
    public bool IsTyped => Count == 0;

    /// <summary>Books it shows on now.</summary>
    public int Count { get; } = count;

    /// <summary>Books where it is the user's own, or one they kept.</summary>
    public int Confirmed { get; } = confirmed;

    /// <summary>Add, Remove, or null to leave it as it is on each book.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsAdding), nameof(IsRemoving), nameof(Description), nameof(RemoveName))]
    public partial BulkAction? Change { get; set; }

    partial void OnChangeChanged(BulkAction? value) => owner.ChipChanged();

    public bool IsAdding => Change == BulkAction.Add;

    public bool IsRemoving => Change == BulkAction.Remove;

    /// <summary>"3 of 12", or what it will be: "12 of 12" when added, "0 of 12" when removed.</summary>
    public string CountText => $"{(IsAdding ? total : IsRemoving ? 0 : Count).ToString("N0", CultureInfo.CurrentCulture)} of {total.ToString("N0", CultureInfo.CurrentCulture)}";

    /// <summary>For screen readers and the tooltip.</summary>
    public string Description => Change switch
    {
        BulkAction.Add when IsTyped => $"{Text}{(IsNew ? $", {NewNote}," : "")} will be added to every book",
        BulkAction.Add => $"{Text}, on {Count:N0} of {total:N0} books, will be added to every book",
        BulkAction.Remove => $"{Text}, will be removed from {Count:N0} books",
        _ when Count < total => $"{Text}, on {Count:N0} of {total:N0} books. Click to add it to every book",
        _ => $"{Text}, on every book",
    };

    public string RemoveName => IsTyped ? $"Don't add {Text}" : IsRemoving ? $"Keep {Text} as it is" : $"Remove {Text} from every book";

    /// <summary>Clicking a chip on only some books puts it on all of them; clicking again takes that back.</summary>
    [RelayCommand]
    void ToggleAll()
    {
        if (IsTyped) return;
        if (IsAdding) Change = null;
        else if (Count < total) Change = BulkAction.Add;
    }

    /// <summary>× removes the value from every book; on a removed chip it takes that back, and a typed one goes away.</summary>
    [RelayCommand]
    void Remove()
    {
        if (IsTyped) owner.Remove(this);
        else Change = IsRemoving ? null : BulkAction.Remove;
    }
}
