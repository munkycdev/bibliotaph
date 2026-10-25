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

public enum BulkEditStep
{
    Editing,
    Summary,
    Applying,
}

/// <summary>
/// Edit metadata for the books ticked in Select mode: every field but the title, at once. A field nobody touches
/// isn't changed; what is changed is confirmed on every book, by the inspector's rules. Review changes shows what will
/// happen first, and calls out where it overrides values the user set themselves.
/// </summary>
public sealed partial class BulkEditViewModel : ObservableObject
{
    readonly MetadataService _metadata;
    readonly IReadOnlyList<EntryId> _documents;

    BulkEditViewModel(BulkEditPlan plan, MetadataService service)
    {
        _documents = plan.Books;
        _metadata = service;
        Heading = plan.Heading;
        ApplyLabel = plan.ApplyLabel;
        Fields = [.. plan.Fields.Select(field => field switch
        {
            BulkChipsField chips => (BulkFieldViewModel)new BulkChipsFieldViewModel(chips, plan.Vocabulary, OnFieldChanged),
            _ => new BulkValueFieldViewModel((BulkValueField)field, plan.Vocabulary, OnFieldChanged),
        })];
    }

    /// <summary>Raised when the dialog closes: with what was changed, or null when it was cancelled.</summary>
    public event EventHandler<BulkEditResult?>? Closed;

    public static async Task<BulkEditViewModel> LoadAsync(IReadOnlyList<EntryId> documents, MetadataService metadata) =>
        new(await Task.Run(() => BulkEditPlan.LoadAsync(metadata, documents)), metadata);

    public int BookCount => _documents.Count;

    public string Heading { get; }

    public string ApplyLabel { get; }

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

    /// <summary>Shows what will change, field by field, before anything is saved.</summary>
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
            Progress = BulkEditText.Updating(p.Done, p.Total));
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
            Problem = BulkEditText.SaveFailed(ex.Message);
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
}


/// <summary>A field in the bulk editor: what the selected books have in it, and the change to make.</summary>
public abstract partial class BulkFieldViewModel(BulkField model, Vocabulary vocabulary, Action changed) : ObservableObject
{
    public MetadataField Field => model.Field;

    public string Label => model.Label;

    protected Vocabulary Vocabulary { get; } = vocabulary;

    public abstract bool HasChange { get; }

    /// <summary>What applying does to this field, for the store. Values are what was typed or picked, or a term's key.</summary>
    public abstract IEnumerable<BulkChange> Changes();

    /// <summary>The summary's lines for this field: what changes, on how many books, and whose values it overrides.</summary>
    public abstract IEnumerable<BulkSummaryLine> Describe();

    /// <summary>Vocabulary labels to pick from, for a term field.</summary>
    public IReadOnlyList<string> Choices => model.Choices;

    public bool UsesChoices => Choices.Count > 0;

    [ObservableProperty]
    public partial string? Problem { get; protected set; }

    /// <summary>
    /// The hint while typing a term: "“One shot” is another name for Adventure", or that it is new.
    /// </summary>
    [ObservableProperty]
    public partial string Resolution { get; private set; } = "";

    protected void Changed()
    {
        OnPropertyChanged(nameof(HasChange));
        changed();
    }

    protected void Resolve(string? typed) => Resolution = FieldText.Resolution(Field, typed, Vocabulary);

    /// <summary>A typed value as the bulk edit will file it; null with <see cref="Problem"/> set when it can't be stored.</summary>
    protected BulkValue? Read(string typed)
    {
        var (value, problem) = BulkField.Read(Field, typed, Vocabulary);
        Problem = problem;
        return value;
    }
}

/// <summary>
/// A single-value field (system, edition, levels, publisher, series, year) across the selection: the value they
/// share, "Mixed (3 values)", or "Not set". Setting a value confirms it on every book; Back to suggestions forgets
/// the user's own value on all of them.
/// </summary>
public sealed partial class BulkValueFieldViewModel : BulkFieldViewModel
{
    readonly BulkValueField _model;
    BulkValue? _set;
    bool _reset;

    public BulkValueFieldViewModel(BulkValueField model, Vocabulary vocabulary, Action changed) : base(model, vocabulary, changed) => _model = model;

    public BulkValueFieldViewModel(MetadataField field, IReadOnlyList<EffectiveField> books, Vocabulary vocabulary, Action changed)
        : this(new BulkValueField(field, books, vocabulary), vocabulary, changed)
    {
    }

    /// <summary>What the books have now: the shared value, "Dungeons &amp; Dragons on 5 of 12 books", "Mixed (3 values)", "Not set".</summary>
    public string Current => _model.Current;

    /// <summary>For a mixed field, each value with how many books have it.</summary>
    public string Breakdown => _model.Breakdown;

    public bool IsKnown => _model.IsKnown;

    public override bool HasChange => _set is not null || _reset;

    /// <summary>"Will be Pathfinder on every book", "Back to suggestions on every book".</summary>
    public string PendingText => _set is { } set ? _model.WillBe(set) : _reset ? BulkValueField.ResetPending : "";

    public bool ShowEdit => !HasChange && !IsEditing;

    public bool ShowReset => _model.Own > 0 && !HasChange && !IsEditing;

    public string EditHint => _model.EditHint;

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
        EditText = _model.StartText;
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
            Problem = _model.Blank;
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

    public override IEnumerable<BulkChange> Changes() => _model.Changes(_set, _reset);

    public override IEnumerable<BulkSummaryLine> Describe() => _model.Describe(_set, _reset);
}

/// <summary>
/// A multi-value field (type, authors, setting, themes, environments, tags) across the selection: every value any
/// book has, as a chip with a count. Adding puts a value on every book, removing takes it off every book (rejected,
/// so no source suggests it again), and clicking a partial chip puts it on all of them.
/// </summary>
public sealed partial class BulkChipsFieldViewModel : BulkFieldViewModel
{
    readonly BulkChipsField _model;

    public BulkChipsFieldViewModel(BulkChipsField model, Vocabulary vocabulary, Action changed) : base(model, vocabulary, changed)
    {
        _model = model;
        foreach (var chip in model.Chips) Chips.Add(new BulkChipViewModel(this, chip, model.Books));
        Chips.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChips));
    }

    public BulkChipsFieldViewModel(MetadataField field, IReadOnlyList<EffectiveField> books, Vocabulary vocabulary, Action changed)
        : this(new BulkChipsField(field, books, vocabulary), vocabulary, changed)
    {
    }

    public ObservableCollection<BulkChipViewModel> Chips { get; } = [];

    public bool HasChips => Chips.Count > 0;

    public override bool HasChange => Chips.Any(c => c.Change is not null);

    public string AddLabel => _model.AddLabel;

    /// <summary>How to type values to add: "Separate values with commas", or how a store and its link go.</summary>
    public string AddHint => _model.AddHint;

    [ObservableProperty]
    public partial string AddText { get; set; } = "";

    partial void OnAddTextChanged(string value) => Resolve(value);

    /// <summary>A label picked from <see cref="BulkFieldViewModel.Choices"/>: it is added straight away.</summary>
    [ObservableProperty]
    public partial string? PickedChoice { get; set; }

    partial void OnPickedChoiceChanged(string? value)
    {
        if (value is null) return;
        if (Read(value) is { } picked) Add(picked);
        PickedChoice = null;
    }

    /// <summary>Adds what was typed, comma-separated, to every book: as chips showing the term each will be filed as.</summary>
    [RelayCommand]
    void AddTyped()
    {
        var (values, problem) = BulkChipsField.ReadMany(Field, AddText ?? "", Vocabulary);
        Problem = problem;
        if (problem is not null || values.Count == 0) return;
        foreach (var value in values) Add(value);
        AddText = "";
    }

    void Add(BulkValue value)
    {
        var chip = Chips.FirstOrDefault(c => c.Normalized == value.Normalized && c.IsNew == value.IsNew);
        // Typing a value some books have already is the same as clicking its chip.
        if (chip is null) Chips.Add(chip = new BulkChipViewModel(this, BulkChipsField.Typed(Field, value), _model.Books));
        chip.Change = BulkAction.Add;
    }

    internal void Remove(BulkChipViewModel chip)
    {
        Chips.Remove(chip);
        Changed();
    }

    internal void ChipChanged() => Changed();

    IEnumerable<(BulkChip, BulkAction)> Changing => Chips.Where(c => c.Change is not null).Select(c => (c.Chip, c.Change!.Value));

    public override IEnumerable<BulkChange> Changes() => _model.Changes(Changing);

    public override IEnumerable<BulkSummaryLine> Describe() => _model.Describe(Changing);
}

/// <summary>
/// A value of a multi-value field in the bulk editor: on how many of the books it is, and whether applying adds it
/// to all of them or removes it from all of them.
/// </summary>
public sealed partial class BulkChipViewModel(BulkChipsFieldViewModel owner, BulkChip chip, int total) : ObservableObject
{
    public BulkChip Chip { get; } = chip;

    /// <summary>The stored value (a term's key, text as written), or a new term's name.</summary>
    public string Value => Chip.Value;

    public string Normalized => Chip.Normalized;

    /// <summary>As it reads: the term it will be filed as.</summary>
    public string Text => Chip.Text;

    /// <summary>"new type", for a name the vocabulary doesn't know yet; null otherwise.</summary>
    public string? NewNote => Chip.NewNote;

    public bool IsNew => Chip.IsNew;

    /// <summary>Typed in the editor: no book has it yet, so applying can only add it.</summary>
    public bool IsTyped => Chip.IsTyped;

    /// <summary>Books it shows on now.</summary>
    public int Count => Chip.Count;

    /// <summary>Books where it is the user's own, or one they kept.</summary>
    public int Confirmed => Chip.Confirmed;

    /// <summary>Add, Remove, or null to leave it as it is on each book.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsAdding), nameof(IsRemoving), nameof(Description), nameof(RemoveName))]
    public partial BulkAction? Change { get; set; }

    partial void OnChangeChanged(BulkAction? value) => owner.ChipChanged();

    public bool IsAdding => Change == BulkAction.Add;

    public bool IsRemoving => Change == BulkAction.Remove;

    /// <summary>"3 of 12", or what it will be: "12 of 12" when added, "0 of 12" when removed.</summary>
    public string CountText => BulkChipsField.CountText(Chip, Change, total);

    /// <summary>For screen readers and the tooltip.</summary>
    public string Description => BulkChipsField.Description(Chip, Change, total);

    public string RemoveName => BulkChipsField.RemoveName(Chip, Change);

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
