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
}

/// <summary>One value, or one alternative, with where it came from.</summary>
public sealed class MetadataValueViewModel(MetadataField field, EffectiveValue value, Vocabulary vocabulary)
{
    public EffectiveValue Value { get; } = value;

    public MetadataField Field { get; } = field;

    /// <summary>As it reads: a term's label, "Levels 1–5", the text itself.</summary>
    public string Text { get; } = Display(field, value.Value, vocabulary);

    public bool Confirmed => Value.Confirmed;

    /// <summary>"From the folder name “D&amp;D 5e”", "You set this", one line per source that agrees.</summary>
    public IReadOnlyList<string> Sources { get; } = [.. value.Support.Select(Describe).Distinct()];

    public static string Display(MetadataField field, string value, Vocabulary vocabulary) => field.Kind switch
    {
        FieldKind.Term => vocabulary.Label(field.Vocabulary!, value),
        FieldKind.Levels when LevelRange.TryParse(value, out var range) => range.Describe(),
        _ => value,
    };

    static string Describe(MetadataClaim claim)
    {
        var quote = string.IsNullOrWhiteSpace(claim.Quote) ? "" : $" “{claim.Quote}”";
        var source = claim.Origin switch
        {
            AssertionOrigin.User => "You set this",
            AssertionOrigin.Folder => "From the folder name" + quote,
            AssertionOrigin.Filename => "From the file name" + quote,
            AssertionOrigin.Embedded => "From the PDF's own information" + quote,
            AssertionOrigin.Rule => "Written in the file's name or title" + quote,
            AssertionOrigin.Ai => "Suggested by the AI model" + quote + (claim.Pages.Count > 0 ? $", page {string.Join(", ", claim.Pages.Select(p => p + 1))}" : ""),
            _ => claim.Origin.ToString(),
        };
        return claim.Origin != AssertionOrigin.User && claim.State == AssertionState.Confirmed ? $"You kept this. {source}" : source;
    }
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
        Choices = field.Field.Kind == FieldKind.Term
            ? [.. vocabulary.InVocabulary(field.Field.Vocabulary!).Select(t => t.Label).Order(StringComparer.CurrentCultureIgnoreCase)]
            : [];
        EditText = string.Join(", ", Values.Select(v => v.Text));
    }

    public MetadataField Field { get; }

    public string Label => Field.Label;

    /// <summary>Shown even when unknown, so the fields that matter most can be filled in.</summary>
    public bool IsPrimary { get; }

    public IReadOnlyList<MetadataValueViewModel> Values { get; }

    public IReadOnlyList<MetadataValueViewModel> Alternatives { get; }

    public bool IsKnown => Values.Count > 0;

    public string Display => IsKnown ? string.Join(", ", Values.Select(v => v.Text)) : "Not set";

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
        FieldKind.Year => "2019",
        _ when Field.Multiple => "Separate values with commas",
        _ => "",
    };

    [ObservableProperty]
    public partial bool ShowEvidence { get; set; }

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

    partial void OnEditTextChanged(string value)
    {
        if (Field.Kind != FieldKind.Term)
        {
            Resolution = "";
            return;
        }
        var notes = new List<string>();
        foreach (var part in MetadataValues.Split(Field, value ?? ""))
        {
            if (MetadataText.Normalize(part).Length == 0) continue;
            var term = _vocabulary.Resolve(Field.Vocabulary!, part);
            if (term is null) notes.Add($"“{part}” will be added as a new {Field.Label.ToLower(CultureInfo.CurrentCulture)}.");
            else if (MetadataText.Normalize(term.Label) != MetadataText.Normalize(part)) notes.Add($"“{part}” is another name for {term.Label}.");
        }
        Resolution = string.Join(" ", notes);
    }

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
