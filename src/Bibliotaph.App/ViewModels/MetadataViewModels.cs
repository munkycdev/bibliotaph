using System.Globalization;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>What the inspector's metadata rows ask of it. The inspector saves, then loads the document again.</summary>
public interface IMetadataEditor
{
    Task<string?> SaveAsync(MetadataField field, string typed);
    Task KeepAsync(MetadataField field);
    Task RejectAsync(MetadataField field, string normalized);
    Task UseAsync(MetadataField field, string value);
    Task ResetAsync(MetadataField field);

    /// <summary>Opens a Get it from link in the browser once the user says yes.</summary>
    void OpenLink(string url);
}

/// <summary>One value, or one alternative, with where it came from.</summary>
public sealed class MetadataValueViewModel(MetadataField field, EffectiveValue value, Vocabulary vocabulary)
{
    public EffectiveValue Value { get; } = value;

    public MetadataField Field { get; } = field;

    /// <summary>As it reads: a term's label, "Levels 1–5", "4–6 players", a store's name, the text itself.</summary>
    public string Text { get; } = Display(field, value.Value, vocabulary);

    /// <summary>A store's link (Get it from), or null.</summary>
    public string? Url { get; } = field.Kind == FieldKind.Store ? StoreLink.Parse(value.Value).Url : null;

    public bool HasLink => Url is not null;

    /// <summary>The link as its button reads: "drivethrurpg.com/en/product/…".</summary>
    public string? LinkText { get; } = field.Kind == FieldKind.Store ? StoreLink.Parse(value.Value).ShortUrl : null;

    /// <summary>As it is typed back into the editor: a store's name with its link after it, otherwise <see cref="Text"/>.</summary>
    public string EditForm => Url is null ? Text : $"{Text} {Url}";

    public bool Confirmed => Value.Confirmed;

    /// <summary>"From the folder name “D&amp;D 5e”", "You set this", one line per source that agrees.</summary>
    public IReadOnlyList<string> Sources { get; } = [.. value.Support.Select(c => Describe(c)).Distinct()];

    public static string Display(MetadataField field, string value, Vocabulary vocabulary) => FieldText.Display(field, value, vocabulary);

    /// <summary>As a value reads where its link has to show too, as on a bulk edit chip: "DriveThruRPG · drivethrurpg.com/…".</summary>
    public static string DisplayWithLink(MetadataField field, string value, Vocabulary vocabulary) => FieldText.DisplayWithLink(field, value, vocabulary);

    /// <summary>Where one claim came from, in words: "From the folder name “D&amp;D 5e”", with its pages unless told not to.</summary>
    public static string Describe(MetadataClaim claim, bool pages = true) => FieldText.Source(claim, pages);
}

/// <summary>
/// One field in the inspector's Details: its values as they read, whether they are only suggested, the evidence
/// behind them and the alternatives, and inline editing. A multi-value field is edited as a comma-separated list.
/// </summary>
public sealed partial class MetadataFieldViewModel : ObservableObject
{
    readonly IMetadataEditor _editor;
    readonly Vocabulary _vocabulary;

    public MetadataFieldViewModel(EffectiveField field, Vocabulary vocabulary, IMetadataEditor editor, bool primary)
    {
        _editor = editor;
        _vocabulary = vocabulary;
        Field = field.Field;
        IsPrimary = primary;
        Values = [.. field.Values.Select(v => new MetadataValueViewModel(field.Field, v, vocabulary))];
        Alternatives = [.. field.Alternatives.Select(v => new MetadataValueViewModel(field.Field, v, vocabulary))];
        NeedsReview = field.NeedsReview;
        Choices = ChoicesFor(field.Field, vocabulary);
        EditText = string.Join(", ", Values.Select(v => v.EditForm));
    }

    public MetadataField Field { get; }

    public string Label => Field.Label;

    /// <summary>Shown even when unknown, so the fields that matter most can be filled in.</summary>
    public bool IsPrimary { get; }

    public IReadOnlyList<MetadataValueViewModel> Values { get; }

    public IReadOnlyList<MetadataValueViewModel> Alternatives { get; }

    public bool IsKnown => Values.Count > 0;

    public string Display => IsKnown ? string.Join(", ", Values.Select(v => v.Text)) : "Not set";

    /// <summary>The values with a web link (Get it from), shown as links under the field.</summary>
    public IReadOnlyList<MetadataValueViewModel> Links => [.. Values.Where(v => v.HasLink)];

    public bool HasLinks => Values.Any(v => v.HasLink);

    /// <summary>Some value here is a suggestion nobody has confirmed.</summary>
    public bool IsSuggested => Values.Any(v => !v.Confirmed);

    public bool NeedsReview { get; }

    public bool HasAlternatives => Alternatives.Count > 0;

    /// <summary>The user has decided something here that Reset would undo.</summary>
    public bool CanReset => Values.Any(v => v.Confirmed);

    /// <summary>"Suggested" or "Sources disagree", on the link that opens the evidence.</summary>
    public string EvidenceLabel => NeedsReview ? "Sources disagree" : IsSuggested ? "Suggested" : "Sources";

    public bool HasEvidence => IsKnown || HasAlternatives;

    /// <summary>Vocabulary labels to pick from, for a term field. Typing a name that isn't here adds it.</summary>
    public IReadOnlyList<string> Choices { get; }

    public bool UsesChoices => Choices.Count > 0;

    public string ChoicesLabel => Field.Multiple ? "Add from the list" : "Or pick from the list";

    public string EditHint => Field.Kind switch
    {
        FieldKind.Levels => "3, 1-5 or n/a",
        FieldKind.Players => "4 or 3-5",
        FieldKind.Year => "2019",
        FieldKind.Store => LinkHint(Field),
        _ when Field.Multiple => "Separate values with commas",
        _ => "",
    };

    /// <summary>How to type Get it from, wherever it is edited.</summary>
    public const string StoreHint = "A store, a link, or both: DriveThruRPG https://… Separate stores with commas";

    /// <summary>How to type Also own, wherever it is edited: a place, and the book's page there when it is online.</summary>
    public const string OwnHint = "Where, a link to the book there, or both: D&D Beyond https://… Separate them with commas";

    /// <summary>How to type a linked field (<see cref="FieldKind.Store"/>).</summary>
    public static string LinkHint(MetadataField field) => field == MetadataFields.AlsoOwn ? OwnHint : StoreHint;

    /// <summary>What a new term of the field is called: "a new store", "a new place", "a new setting".</summary>
    public static string TermNoun(MetadataField field) =>
        field == MetadataFields.AlsoOwn ? "place" : field.Kind == FieldKind.Store ? field.Vocabulary! : field.Label.ToLower(CultureInfo.CurrentCulture);

    [ObservableProperty]
    public partial bool ShowEvidence { get; set; }

    /// <summary>Cataloged wrong… is open: a value nobody confirmed carries a "Guessed" mark, so the guesses stand out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGuessed))]
    public partial bool MarksGuesses { get; set; }

    /// <summary>The "Guessed" mark: shown while correcting, on a field whose value is only suggested.</summary>
    public bool ShowsGuessed => MarksGuesses && IsKnown && IsSuggested;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>
    /// For a term field, how what is typed will be filed: "“Campaign” is another name for Adventure", or "“Heists” will
    /// be added as a new type". Empty when every value is typed as its own label.
    /// </summary>
    [ObservableProperty]
    public partial string Resolution { get; private set; } = "";

    partial void OnEditTextChanged(string value) => Resolution = DescribeResolution(Field, value, _vocabulary);

    /// <summary>How typed text will be filed in a term field; empty for other fields, or when every value is a label.</summary>
    public static string DescribeResolution(MetadataField field, string? typed, Vocabulary vocabulary)
    {
        if (field.Kind is not (FieldKind.Term or FieldKind.Store)) return "";
        var notes = new List<string>();
        foreach (var typedPart in MetadataValues.Split(field, typed ?? ""))
        {
            if (MetadataText.Normalize(typedPart).Length == 0) continue;
            var part = typedPart;
            Term? term;
            if (field.Kind == FieldKind.Store)
            {
                (term, part, var url) = StoreLink.Read(typedPart, vocabulary, field);
                if (term is not null && url is not null && MetadataText.Normalize(StoreLink.Split(typedPart).Name).Length == 0)
                {
                    notes.Add($"The link goes to {term.Label}.");
                    continue;
                }
            }
            else term = vocabulary.Resolve(field.Vocabulary!, part);
            var kind = TermNoun(field);
            if (term is null) notes.Add($"“{part}” will be added as a new {kind}.");
            else if (MetadataText.Normalize(term.Label) != MetadataText.Normalize(part)) notes.Add($"“{part}” is another name for {term.Label}.");
        }
        return string.Join(" ", notes);
    }

    /// <summary>The labels of a term field's vocabulary, to pick from; empty for other fields.</summary>
    public static IReadOnlyList<string> ChoicesFor(MetadataField field, Vocabulary vocabulary) =>
        field.Kind is FieldKind.Term or FieldKind.Store
            ? [.. vocabulary.InVocabulary(field.Vocabulary!).Select(t => t.Label).Order(StringComparer.CurrentCultureIgnoreCase)]
            : [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>A label picked from <see cref="Choices"/>: it replaces the text, or is added to a multi-value field's list.</summary>
    [ObservableProperty]
    public partial string? PickedChoice { get; set; }

    partial void OnPickedChoiceChanged(string? value)
    {
        if (value is null) return;
        if (!Field.Multiple) EditText = value;
        else
        {
            var current = MetadataValues.Split(Field, EditText ?? "");
            if (!current.Contains(value, StringComparer.CurrentCultureIgnoreCase)) EditText = string.Join(", ", current.Append(value));
        }
        PickedChoice = null;
    }

    [RelayCommand]
    void ToggleEvidence() => ShowEvidence = !ShowEvidence;

    [RelayCommand]
    void Edit()
    {
        EditText = string.Join(", ", Values.Select(v => v.Text));
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
    Task Save() => RunAsync(async () =>
    {
        Problem = await _editor.SaveAsync(Field, EditText ?? "");
        if (Problem is null) IsEditing = false;
    });

    [RelayCommand]
    Task Keep() => RunAsync(() => _editor.KeepAsync(Field));

    [RelayCommand]
    Task Reject(MetadataValueViewModel value) => RunAsync(() => _editor.RejectAsync(Field, value.Value.Normalized));

    [RelayCommand]
    Task Use(MetadataValueViewModel value) => RunAsync(() => _editor.UseAsync(Field, value.Value.Value));

    [RelayCommand]
    Task Reset() => RunAsync(() => _editor.ResetAsync(Field));

    /// <summary>Opens a value's link, after asking, as a web link in a PDF does.</summary>
    [RelayCommand]
    void OpenLink(MetadataValueViewModel value)
    {
        if (value.Url is { } url) _editor.OpenLink(url);
    }

    async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
