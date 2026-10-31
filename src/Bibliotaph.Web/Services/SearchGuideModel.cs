using Bibliotaph.Core.Search;

namespace Bibliotaph.Web.Services;

/// <summary>A row of the search box's field guide: a field to add, or a value for the field being typed.</summary>
public abstract record GuideRow
{
    /// <summary>The whole row in words, for screen readers.</summary>
    public abstract string Spoken { get; }
}

/// <summary>"system:  the game system, e.g. system:5e".</summary>
public sealed record GuideFieldRow(SearchFieldInfo Field) : GuideRow
{
    public override string Spoken => $"{Field.Prefix} {Field.Description}, for example {Field.Example}";
}

/// <summary>"Dungeons &amp; Dragons  system:dnd  12".</summary>
public sealed record GuideValueRow(SearchFieldInfo Field, GuideValue Value) : GuideRow
{
    /// <summary>What picking it puts in the box.</summary>
    public string Search => Field.Prefix + SearchGuide.Quote(Value.Value);

    public string Count => Value.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public override string Spoken => $"{Value.Label}, {Search}, {Count} {(Value.Count == 1 ? "book" : "books")}";
}

/// <summary>
/// The field guide under the search box: the fields you can search, and after a field's colon its values
/// in your library. It opens when the box gets focus while empty, narrows to the field names that start with the word at
/// the cursor, and opens anywhere with Ctrl+Space or Down. What to offer is <see cref="SearchGuide"/>'s;
/// this keeps the state and loads the values.
/// </summary>
public sealed class SearchGuideModel(LibraryClient libraryApi)
{
    /// <summary>Each field's values, loaded once each time the guide opens, so counts are fresh but typing is cheap.</summary>
    readonly Dictionary<SearchField, IReadOnlyList<GuideValue>> _values = [];
    GuideContext? _at;
    string _text = "";

    /// <summary>The guide changed, to be drawn again.</summary>
    public event Action? Changed;

    public bool IsOpen { get; private set; }

    /// <summary>The line above the rows: what they are.</summary>
    public string Heading { get; private set; } = "";

    public IReadOnlyList<GuideRow> Rows { get; private set; } = [];

    /// <summary>After a free-text field's colon, the field's own line with its example instead of rows to pick.</summary>
    public GuideFieldRow? Hint { get; private set; }

    /// <summary>The row the arrow keys are on, which Enter or Tab picks. None until an arrow key is pressed.</summary>
    public int Highlighted { get; private set; } = -1;

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

    /// <summary>Esc, the box losing focus, or Ask mode.</summary>
    public void Close() => Show(null);

    /// <summary>Moves the highlight down (1) or up (-1), wrapping round. False when there is nothing to move through.</summary>
    public bool Move(int step)
    {
        if (!IsOpen || Rows.Count == 0) return false;
        Highlighted = Highlighted < 0 ? (step > 0 ? 0 : Rows.Count - 1) : ((Highlighted + step) % Rows.Count + Rows.Count) % Rows.Count;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The highlighted row's new text and cursor, or null with none highlighted.</summary>
    public GuideEdit? PickHighlighted() => IsOpen && Highlighted >= 0 && Highlighted < Rows.Count ? Pick(Rows[Highlighted]) : null;

    /// <summary>What picking <paramref name="row"/> makes of the box's text and cursor.</summary>
    public GuideEdit? Pick(GuideRow row) => _at is not { } at ? null : row switch
    {
        GuideFieldRow field => SearchGuide.InsertField(_text, at, field.Field),
        GuideValueRow value => SearchGuide.InsertValue(_text, at, value.Value.Value),
        _ => null,
    };

    void Show(GuideContext? at)
    {
        if (at is not null && at.Equals(_at) && IsOpen) return;
        _at = at;
        Highlighted = -1;
        Hint = null;
        switch (at)
        {
            case { Mode: GuideMode.Fields } fields:
                Heading = "Search by field";
                Rows = [.. fields.Fields.Select(f => new GuideFieldRow(f))];
                IsOpen = true;
                break;
            case { Mode: GuideMode.Example, Field: { } field }:
                Heading = "Search by field";
                Rows = [];
                Hint = new GuideFieldRow(field);
                IsOpen = true;
                break;
            case { Mode: GuideMode.Values, Field: { } field }:
                Heading = $"{field.Prefix} in your library";
                if (_values.TryGetValue(field.Field, out var values)) ShowValues(at, field, values);
                else
                {
                    Rows = [];
                    IsOpen = true;
                    _ = LoadValuesAsync(field);
                }
                break;
            default:
                Rows = [];
                IsOpen = false;
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>The values that fit what was typed; with none in the library yet, the field's example instead.</summary>
    void ShowValues(GuideContext at, SearchFieldInfo field, IReadOnlyList<GuideValue> values)
    {
        Rows = [.. SearchGuide.Values(values, at.Typed).Select(v => new GuideValueRow(field, v))];
        if (Rows.Count == 0 && at.Typed.Length == 0) Hint = new GuideFieldRow(field);
        IsOpen = Rows.Count > 0 || Hint is not null;
    }

    async Task LoadValuesAsync(SearchFieldInfo field)
    {
        try
        {
            var values = await libraryApi.GuideValuesAsync(field.Name);
            _values[field.Field] = values;
            // Typing may have moved on while the values loaded.
            if (IsOpen && _at is { Mode: GuideMode.Values } at && at.Field == field)
            {
                ShowValues(at, field, values);
                Changed?.Invoke();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiProblemException)
        {
            // The guide shows the field's example instead, as with no values.
            if (IsOpen && _at is { Mode: GuideMode.Values } at && at.Field == field)
            {
                ShowValues(at, field, []);
                Changed?.Invoke();
            }
        }
    }
}
