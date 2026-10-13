using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A type-ahead filter under More filters, for a field with too many values for a menu (settings, themes,
/// publishers): typing shows the values in the library that match, with how many books each has, and picking one adds
/// a chip with an ×. Books match any of a box's chips; the Library keeps those that match every box. The Library gives
/// it the counts each time it refreshes, and hears <see cref="Changed"/> when a chip is added or taken off.
/// </summary>
public sealed partial class ChipFilterViewModel(MetadataField field, string hint) : ObservableObject
{
    /// <summary>Enough to choose from without the list running past a short window.</summary>
    const int MaxSuggestions = 8;

    IReadOnlyList<ValueCount> _counts = [];
    readonly List<Choice<string>> _picks = [];
    List<Choice<string>> _shown = [];

    /// <summary>Raised when a chip is added or taken off, not when the Library clears or restores them itself.</summary>
    public event EventHandler? Changed;

    public MetadataField Field { get; } = field;

    public string Label => Field.Label;

    /// <summary>What the box is for, as its tooltip: "Type a setting, such as a world or a city".</summary>
    public string Hint { get; } = hint;

    /// <summary>What has been typed so far; the suggestions follow it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DismissCommand))]
    public partial string Text { get; set; } = "";

    public ObservableCollection<ChipSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    public partial bool HasSuggestions { get; private set; }

    /// <summary>The values picked, as chips under the box.</summary>
    public ObservableCollection<FilterChip> Chips { get; } = [];

    /// <summary>The picks, value and label, in the order they were made.</summary>
    public IReadOnlyList<Choice<string>> Picks => _picks;

    public IReadOnlyList<string> Values => [.. _picks.Select(p => p.Value)];

    /// <summary>Shown once some book has a value, or while a chip is on.</summary>
    [ObservableProperty]
    public partial bool IsOffered { get; private set; }

    partial void OnTextChanged(string value) => ShowSuggestions();

    /// <summary>
    /// The values and counts to offer, among the books the rest of the filters keep. A chip whose value no book has now
    /// stays, so the filter never changes by itself; one restored from a saved search gets its label here.
    /// </summary>
    public void ShowCounts(IReadOnlyList<ValueCount> counts)
    {
        _counts = counts;
        var labels = counts.ToDictionary(c => c.Value, c => c.Label, StringComparer.Ordinal);
        for (var i = 0; i < _picks.Count; i++)
            if (labels.TryGetValue(_picks[i].Value, out var label) && label != _picks[i].Label) _picks[i] = _picks[i] with { Label = label };
        ShowChips();
        ShowSuggestions();
    }

    /// <summary>Takes every chip off, without raising <see cref="Changed"/>: the Library refreshes once for all its filters.</summary>
    public void Clear() => Restore([]);

    /// <summary>Puts a saved search's picks back, without raising <see cref="Changed"/>.</summary>
    public void Restore(IReadOnlyList<string> values)
    {
        _picks.Clear();
        _picks.AddRange(values.Select(v => new Choice<string>(v, _counts.FirstOrDefault(c => c.Value == v)?.Label ?? v)));
        Text = "";
        ShowChips();
    }

    /// <summary>Takes one value's chip off.</summary>
    public void Remove(string value)
    {
        if (_picks.RemoveAll(p => p.Value == value) == 0) return;
        ShowChips();
        ShowSuggestions();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    void Pick(ChipSuggestion? suggestion)
    {
        if (suggestion is null || _picks.Any(p => p.Value == suggestion.Value)) return;
        _picks.Add(new Choice<string>(suggestion.Value, suggestion.Label));
        Text = "";
        ShowChips();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enter picks the highlighted suggestion, the best match until the arrow keys move it.</summary>
    [RelayCommand]
    void PickHighlighted() => Pick(Suggestions.FirstOrDefault(s => s.IsHighlighted));

    [RelayCommand]
    void HighlightNext() => MoveHighlight(1);

    [RelayCommand]
    void HighlightPrevious() => MoveHighlight(-1);

    /// <summary>Escape empties the box; with nothing typed it goes on to the page, which closes what is open there.</summary>
    [RelayCommand(CanExecute = nameof(HasText))]
    void Dismiss() => Text = "";

    bool HasText() => Text.Length > 0;

    void MoveHighlight(int step)
    {
        if (Suggestions.Count == 0) return;
        var current = Suggestions.ToList().FindIndex(s => s.IsHighlighted);
        var next = Math.Clamp(current + step, 0, Suggestions.Count - 1);
        foreach (var (suggestion, i) in Suggestions.Select((s, i) => (s, i))) suggestion.IsHighlighted = i == next;
    }

    /// <summary>
    /// The values not yet picked whose name, or any other name they go by, contains what was typed, ignoring case,
    /// accents and punctuation: those starting with it first, then the most books.
    /// </summary>
    void ShowSuggestions()
    {
        var typed = MetadataText.Normalize(Text);
        var matches = typed.Length == 0 ? [] : _counts
            .Where(c => _picks.All(p => p.Value != c.Value))
            .Select(c => (Count: c, Label: MetadataText.Normalize(c.Label)))
            .Where(c => c.Label.Contains(typed, StringComparison.Ordinal) || c.Count.Names.Contains(typed, StringComparison.Ordinal))
            .OrderBy(c => !c.Label.StartsWith(typed, StringComparison.Ordinal))
            .Take(MaxSuggestions)
            .Select(c => new ChipSuggestion(c.Count.Value, c.Count.Label, c.Count.Count))
            .ToList();
        Suggestions.Clear();
        foreach (var match in matches) Suggestions.Add(match);
        if (Suggestions.Count > 0) Suggestions[0].IsHighlighted = true;
        HasSuggestions = Suggestions.Count > 0;
    }

    void ShowChips()
    {
        IsOffered = _counts.Count > 0 || _picks.Count > 0;
        if (_shown.SequenceEqual(_picks)) return;
        _shown = [.. _picks];
        Chips.Clear();
        foreach (var pick in _picks) Chips.Add(new FilterChip(pick.Label, $"Remove {pick.Label}", new RelayCommand(() => Remove(pick.Value))));
    }
}

/// <summary>A value a type-ahead filter offers: "The Marches   12".</summary>
public sealed partial class ChipSuggestion(string value, string label, long count) : ObservableObject
{
    public string Value { get; } = value;

    public string Label { get; } = label;

    public string Count { get; } = count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>What a screen reader says for the row: "The Marches, 12 books".</summary>
    public string Spoken { get; } = $"{label}, {count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? "book" : "books")}";

    /// <summary>The row Enter picks.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }
}
