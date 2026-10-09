using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>One vocabulary as a tab: "Types" for "type".</summary>
public sealed partial class VocabularyChoice(string key, string label, Action<VocabularyChoice> selected) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

/// <summary>A term in Settings > Vocabulary: its names, and editing them in place.</summary>
public sealed partial class TermRowViewModel(VocabularyEntry entry, VocabularyViewModel page) : ObservableObject
{
    public VocabularyEntry Entry { get; } = entry;

    public string Label => Entry.Label;

    /// <summary>"Also: module, scenario, one shot", or how a user's own term is marked.</summary>
    public string Detail
    {
        get
        {
            var names = Entry.Aliases.Select(a => a.Text).Prepend(Entry.ShortLabel).OfType<string>().ToList();
            var also = names.Count == 0 ? "" : "Also " + string.Join(", ", names);
            var yours = Entry.Origin == TermOrigin.User ? "Added by you" : "";
            return string.Join(" · ", new[] { yours, also }.Where(s => s.Length > 0));
        }
    }

    public IReadOnlyList<AliasEntry> Aliases => Entry.Aliases;

    public bool HasAliases => Entry.Aliases.Count > 0;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditLabel { get; set; } = entry.Label;

    [ObservableProperty]
    public partial string EditShort { get; set; } = entry.ShortLabel ?? "";

    [ObservableProperty]
    public partial string NewAlias { get; set; } = "";

    /// <summary>Why the last edit was refused, or what else it changed.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    [RelayCommand]
    void Edit() => page.StartEditing(this);

    [RelayCommand]
    void Close() => IsEditing = false;

    [RelayCommand]
    Task Rename() => page.RenameAsync(this);

    [RelayCommand]
    Task AddAlias() => page.AddAliasAsync(this);

    [RelayCommand]
    Task RemoveAlias(AliasEntry alias) => page.RemoveAliasAsync(this, alias);
}

/// <summary>
/// Settings > Vocabulary: the terms Bibliotaph files books under and the other names it knows them by. Renaming
/// keeps a term's key, so nothing filed under it moves; a name taken from another term's aliases says so. The library
/// catches up in the background once the edits pause.
/// </summary>
public sealed partial class VocabularyViewModel : PageViewModel
{
    readonly VocabularyService _vocabulary;
    readonly ILogger<VocabularyViewModel> _log;
    List<TermRowViewModel> _all = [];

    public VocabularyViewModel(VocabularyService vocabulary, ILogger<VocabularyViewModel> log)
    {
        _vocabulary = vocabulary;
        _log = log;
        Vocabularies =
        [
            new("system", "Game systems", OnSelected),
            new("edition", "Editions", OnSelected),
            new("type", "Types", OnSelected),
            new("setting", "Settings", OnSelected),
            new("theme", "Themes", OnSelected),
            new("environment", "Environments", OnSelected),
            new("publisher", "Publishers", OnSelected),
        ];
        Selected = Vocabularies[2];
    }

    public override Route Route => Route.Vocabulary;
    public override string Title => "Vocabulary";
    public override string Section => "Settings";
    public override Route? SectionRoute => Route.Settings;

    public IReadOnlyList<VocabularyChoice> Vocabularies { get; }

    public VocabularyChoice Selected { get; private set; }

    public ObservableCollection<TermRowViewModel> Terms { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => ShowTerms();

    [ObservableProperty]
    public partial string NewTerm { get; set; } = "";

    [ObservableProperty]
    public partial string? AddProblem { get; set; }

    [ObservableProperty]
    public partial string? AddNote { get; set; }

    public string AddLabel => $"Add to {Selected.Label.ToLowerInvariant()}";

    public override async Task LoadAsync()
    {
        if (Selected.IsSelected) await ReloadAsync();
        else Selected.IsSelected = true; // lists it
    }

    void OnSelected(VocabularyChoice choice)
    {
        if (ReferenceEquals(choice, Selected) && _all.Count > 0) return;
        foreach (var other in Vocabularies.Where(v => !ReferenceEquals(v, choice))) other.IsSelected = false;
        Selected = choice;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(AddLabel));
        Filter = "";
        AddProblem = AddNote = null;
        _ = ReloadAsync();
    }

    /// <summary>Lists the selected vocabulary again, keeping <paramref name="editing"/>'s term open with its message.</summary>
    async Task ReloadAsync(long? editing = null, string? problem = null, string? note = null)
    {
        try
        {
            var vocabulary = Selected.Key;
            var entries = await _vocabulary.ListAsync(vocabulary);
            if (vocabulary != Selected.Key) return; // another tab was picked meanwhile
            _all = [.. entries.Select(e => new TermRowViewModel(e, this))];
            if (editing is { } id && _all.FirstOrDefault(t => t.Entry.Id == id) is { } row)
            {
                row.IsEditing = true;
                row.Problem = problem;
                row.Note = note;
            }
            ShowTerms();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Listing the {Vocabulary} vocabulary failed", Selected.Key);
        }
    }

    void ShowTerms()
    {
        var filter = Filter.Trim();
        Terms.Clear();
        foreach (var term in _all.Where(t => filter.Length == 0
            || t.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || t.Entry.Aliases.Any(a => a.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase))))
            Terms.Add(term);
    }

    internal void StartEditing(TermRowViewModel row)
    {
        foreach (var other in _all.Where(t => !ReferenceEquals(t, row))) other.IsEditing = false;
        row.Problem = row.Note = null;
        row.IsEditing = true;
    }

    [RelayCommand]
    async Task AddTerm()
    {
        if (string.IsNullOrWhiteSpace(NewTerm)) return;
        var edit = await _vocabulary.AddTermAsync(Selected.Key, NewTerm);
        AddProblem = edit.Problem;
        AddNote = edit.Problem is null ? $"Added {NewTerm.Trim()}." + (edit.Note is { } note ? " " + note : "") : null;
        if (edit.Problem is not null) return;
        NewTerm = "";
        Filter = "";
        await ReloadAsync(edit.TermId);
    }

    internal async Task RenameAsync(TermRowViewModel row)
    {
        var edit = await _vocabulary.RenameAsync(row.Entry.Id, row.EditLabel, row.EditShort);
        if (edit.Problem is not null)
        {
            row.Problem = edit.Problem;
            return;
        }
        await ReloadAsync(row.Entry.Id, note: edit.Note ?? "Saved.");
    }

    internal async Task AddAliasAsync(TermRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.NewAlias)) return;
        var edit = await _vocabulary.AddAliasAsync(row.Entry.Id, row.NewAlias);
        if (edit.Problem is not null)
        {
            row.Problem = edit.Problem;
            row.Note = null;
            return;
        }
        await ReloadAsync(row.Entry.Id, note: edit.Note);
    }

    internal async Task RemoveAliasAsync(TermRowViewModel row, AliasEntry alias)
    {
        await _vocabulary.RemoveAliasAsync(alias.Id);
        await ReloadAsync(row.Entry.Id, note: $"“{alias.Text}” no longer means {row.Label}.");
    }
}
