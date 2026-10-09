using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>A row of the search box's field guide: a field to add, or a value for the field being typed.</summary>
public abstract partial class GuideSuggestionViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>The whole row in words, for screen readers and the row's automation name.</summary>
    public abstract string Spoken { get; }
}

/// <summary>"system:  the game system, e.g. system:5e".</summary>
public sealed class FieldSuggestionViewModel(SearchFieldInfo field) : GuideSuggestionViewModel
{
    public SearchFieldInfo Field { get; } = field;

    public string Name => Field.Prefix;

    public string Description => Field.Description + ", e.g. ";

    public string Example => Field.Example;

    public override string Spoken => $"{Field.Prefix} {Field.Description}, for example {Field.Example}";
}

/// <summary>"Dungeons &amp; Dragons  system:dnd  12".</summary>
public sealed class ValueSuggestionViewModel(SearchFieldInfo searchField, GuideValue value) : GuideSuggestionViewModel
{
    public SearchFieldInfo Field { get; } = searchField;

    public GuideValue Value { get; } = value;

    public string Label => Value.Label;

    /// <summary>What picking it puts in the box.</summary>
    public string Search => Field.Prefix + SearchGuide.Quote(Value.Value);

    public string Count => Value.Count.ToString("N0", CultureInfo.CurrentCulture);

    public override string Spoken =>
        $"{Value.Label}, {Search}, {Count} {(Value.Count == 1 ? "book" : "books")}";
}

/// <summary>
/// The panel under the search box that lists the fields you can search, and after a field's colon the values in your
/// library (slice 4 plan, item 4b). It opens when the box gets focus while empty, narrows to the field names that
/// start with the word at the cursor, and opens anywhere with Ctrl+Space. Picking a row raises <see cref="Edited"/>
/// with the new text and cursor; the window applies it to the box, which keeps focus throughout. The logic of what
/// to offer is <see cref="SearchGuide"/>'s; this keeps the state and loads the values.
/// </summary>
public sealed partial class SearchGuideViewModel(LibraryQueries queries, LibraryStore library, ILogger<SearchGuideViewModel> log) : ObservableObject
{
    /// <summary>The values of each field, loaded once each time the guide opens, so counts are fresh but typing is cheap.</summary>
    readonly Dictionary<SearchField, IReadOnlyList<GuideValue>> _values = [];
    GuideContext? _at;
    string _text = "";

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The line above the rows: what they are.</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<GuideSuggestionViewModel> Suggestions { get; private set; } = [];

    /// <summary>After a free-text field's colon, the field's own line with its example instead of rows to pick.</summary>
    [ObservableProperty]
    public partial FieldSuggestionViewModel? Hint { get; private set; }

    /// <summary>The row the arrow keys are on, which Enter or Tab picks. None until an arrow key is pressed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Announcement))]
    public partial GuideSuggestionViewModel? Highlighted { get; private set; }

    partial void OnHighlightedChanged(GuideSuggestionViewModel? oldValue, GuideSuggestionViewModel? newValue)
    {
        oldValue?.IsHighlighted = false;
        newValue?.IsHighlighted = true;
    }

    /// <summary>What a screen reader hears as the highlight moves: the highlighted row, in words.</summary>
    public string Announcement => Highlighted?.Spoken ?? "";

    /// <summary>A pick's new text and cursor, for the window to put in the box.</summary>
    public event EventHandler<GuideEdit>? Edited;

    /// <summary>The box got focus: a fresh look, which shows every field when the box is empty.</summary>
    public void Focused(string text, int caret)
    {
        _values.Clear();
        _text = text;
        if (string.IsNullOrWhiteSpace(text)) Show(SearchGuide.At(text, caret));
    }

    /// <summary>The text changed: follow the word at the cursor. An emptied box doesn't open the guide by itself.</summary>
    public void TextChanged(string text, int caret)
    {
        _text = text;
        if (!IsOpen && string.IsNullOrWhiteSpace(text)) return;
        Show(SearchGuide.At(text, caret));
    }

    /// <summary>Only the cursor moved: an open guide follows it, a closed one stays closed.</summary>
    public void CaretMoved(string text, int caret)
    {
        _text = text;
        if (IsOpen) Show(SearchGuide.At(text, caret));
    }

    /// <summary>Ctrl+Space, or Down while closed: open wherever the cursor is.</summary>
    public void Request(string text, int caret)
    {
        if (!IsOpen) _values.Clear();
        _text = text;
        Show(SearchGuide.At(text, caret, requested: true));
    }

    /// <summary>Esc, or the box losing focus.</summary>
    public void Close() => Show(null);

    /// <summary>Moves the highlight down (1) or up (-1), wrapping round. False when there is nothing to move through.</summary>
    public bool Move(int step)
    {
        if (!IsOpen || Suggestions.Count == 0) return false;
        var at = Highlighted is null ? (step > 0 ? 0 : Suggestions.Count - 1)
            : ((Suggestions.ToList().IndexOf(Highlighted) + step) % Suggestions.Count + Suggestions.Count) % Suggestions.Count;
        Highlighted = Suggestions[at];
        return true;
    }

    /// <summary>Picks the highlighted row, if there is one.</summary>
    public bool PickHighlighted()
    {
        if (!IsOpen || Highlighted is not { } row) return false;
        Pick(row);
        return true;
    }

    [RelayCommand]
    void Pick(GuideSuggestionViewModel? row)
    {
        if (_at is not { } at || row is null) return;
        var edit = row switch
        {
            FieldSuggestionViewModel field => SearchGuide.InsertField(_text, at, field.Field),
            ValueSuggestionViewModel value => SearchGuide.InsertValue(_text, at, value.Value.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        Edited?.Invoke(this, edit);
    }

    void Show(GuideContext? at)
    {
        if (at is not null && at.Equals(_at) && IsOpen) return;
        _at = at;
        Highlighted = null;
        Hint = null;
        switch (at)
        {
            case { Mode: GuideMode.Fields } fields:
                Heading = "Search by field";
                Suggestions = [.. fields.Fields.Select(f => new FieldSuggestionViewModel(f))];
                IsOpen = true;
                break;
            case { Mode: GuideMode.Example, Field: { } field }:
                Heading = "Search by field";
                Suggestions = [];
                Hint = new FieldSuggestionViewModel(field);
                IsOpen = true;
                break;
            case { Mode: GuideMode.Values, Field: { } field }:
                Heading = $"{field.Prefix} in your library";
                if (_values.TryGetValue(field.Field, out var values)) ShowValues(at, field, values);
                else
                {
                    Suggestions = [];
                    IsOpen = true;
                    _ = LoadValuesAsync(field);
                }
                break;
            default:
                Suggestions = [];
                IsOpen = false;
                break;
        }
    }

    /// <summary>The values that fit what was typed; with none in the library yet, the field's example instead.</summary>
    void ShowValues(GuideContext at, SearchFieldInfo field, IReadOnlyList<GuideValue> values)
    {
        Suggestions = [.. SearchGuide.Values(values, at.Typed).Select(v => new ValueSuggestionViewModel(field, v))];
        if (Suggestions.Count == 0 && at.Typed.Length == 0) Hint = new FieldSuggestionViewModel(field);
        IsOpen = Suggestions.Count > 0 || Hint is not null;
    }

    async Task LoadValuesAsync(SearchFieldInfo field)
    {
        try
        {
            var values = await Task.Run(async () =>
            {
                var filter = new LibraryFilter(await library.GetVisibleEntryIdsAsync());
                if (field.Kind == SearchFieldKind.Format)
                    return SearchGuide.FormatValues((await queries.GetFormatCountsAsync(filter)).Select(Value));
                var counts = await queries.GetFacetCountsAsync(field.Metadata?.Vocabulary ?? field.Name, filter);
                return (IReadOnlyList<GuideValue>)[.. counts.Select(Value)];
            });
            _values[field.Field] = values;
            // Typing may have moved on while the values loaded.
            if (IsOpen && _at is { Mode: GuideMode.Values } at && at.Field == field) ShowValues(at, field, values);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading the values of {Field} for the search guide failed", field.Name);
        }

        static GuideValue Value(FacetCount count) => new(count.Value, count.Label, count.Count);
    }
}
