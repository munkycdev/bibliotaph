using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
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
/// One of the user's own tags in Settings > Vocabulary: its name, the words that find it, how many books show it, and
/// editing them in place.
/// </summary>
public sealed partial class UserTagRowViewModel(UserTagEntry entry, VocabularyViewModel page) : ObservableObject
{
    public UserTagEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    /// <summary>"Found by undead, zombie, lich · on 12 books".</summary>
    public string Detail => $"Found by {string.Join(", ", Entry.Words)} · {VocabularyViewModel.Books(Entry.Books)}";

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = entry.Name;

    [ObservableProperty]
    public partial string EditWords { get; set; } = string.Join(", ", entry.Words);

    /// <summary>Why the last edit was refused, or what it found.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    [RelayCommand]
    void Edit() => page.StartEditing(this);

    [RelayCommand]
    void Close() => IsEditing = false;

    [RelayCommand]
    Task Save() => page.SaveTagAsync(this);

    [RelayCommand]
    Task Delete() => page.DeleteTagAsync(this);
}

/// <summary>
/// Settings > Vocabulary: the terms Bibliotaph files books under and the other names it knows them by. Renaming
/// keeps a term's key, so nothing filed under it moves; a name taken from another term's aliases says so. The library
/// catches up in the background once the edits pause. Its last tab holds the user's own tags, each with the words that
/// find it in the books' pages; saving one looks through the library's text at once.
/// </summary>
public sealed partial class VocabularyViewModel : SettingsSectionViewModel
{
    /// <summary>The tab of the user's own tags, which aren't a vocabulary of terms.</summary>
    public const string TagsKey = "tags";

    readonly VocabularyService _vocabulary;
    readonly UserTagService _tags;
    readonly ILogger<VocabularyViewModel> _log;
    List<TermRowViewModel> _all = [];

    public VocabularyViewModel(VocabularyService vocabulary, UserTagService tags, ILogger<VocabularyViewModel> log)
    {
        _vocabulary = vocabulary;
        _tags = tags;
        _log = log;
        Vocabularies =
        [
            new("system", "Game systems", OnSelected),
            new("edition", "Editions", OnSelected),
            new("type", "Types", OnSelected),
            new("duration", "Durations", OnSelected),
            new("setting", "Settings", OnSelected),
            new("style", "Styles", OnSelected),
            new("theme", "Themes", OnSelected),
            new("environment", "Environments", OnSelected),
            new("own", "Also own", OnSelected),
            new("store", "Get it from", OnSelected),
            new("publisher", "Publishers", OnSelected),
            new(TagsKey, "Your tags", OnSelected),
        ];
        Selected = Vocabularies[2];
    }

    public override SettingsSection Section => SettingsSection.Vocabulary;
    public override string Label => "Vocabulary";

    public IReadOnlyList<VocabularyChoice> Vocabularies { get; }

    public VocabularyChoice Selected { get; private set; }

    public ObservableCollection<TermRowViewModel> Terms { get; } = [];

    /// <summary>Whether the user's own tags are showing rather than a vocabulary's terms.</summary>
    public bool IsTags => Selected.Key == TagsKey;

    public ObservableCollection<UserTagRowViewModel> Tags { get; } = [];

    [ObservableProperty]
    public partial string NewTagName { get; set; } = "";

    [ObservableProperty]
    public partial string NewTagWords { get; set; } = "";

    /// <summary>How a tag's words are matched, and how often a book must use them to get it.</summary>
    public static string TagRuleHelp { get; } =
        "A word also finds words that start with it, as in search: zombie finds zombies. A book gets the tag when its words are on at least "
        + $"{UserTagRule.MinimumPages} of its pages, and on at least one page in {(int)Math.Round(1 / UserTagRule.MinimumShare)}. New books are checked once their text is read.";

    /// <summary>While a saved tag's words are being looked for in the library.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; set; }

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
        if (ReferenceEquals(choice, Selected) && (_all.Count > 0 || Tags.Count > 0)) return;
        foreach (var other in Vocabularies.Where(v => !ReferenceEquals(v, choice))) other.IsSelected = false;
        Selected = choice;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(AddLabel));
        OnPropertyChanged(nameof(IsTags));
        Filter = "";
        AddProblem = AddNote = null;
        ReloadAsync().FireAndForget();
    }

    /// <summary>Lists the selected vocabulary again, keeping <paramref name="editing"/>'s term open with its message.</summary>
    async Task ReloadAsync(long? editing = null, string? problem = null, string? note = null)
    {
        if (IsTags)
        {
            await ReloadTagsAsync(editing, note);
            return;
        }
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

    /// <summary>Lists the user's tags again, keeping <paramref name="editing"/>'s tag open with its note.</summary>
    async Task ReloadTagsAsync(long? editing = null, string? note = null)
    {
        try
        {
            var tags = await _tags.ListAsync();
            if (!IsTags) return; // another tab was picked meanwhile
            Tags.Clear();
            foreach (var tag in tags)
            {
                var row = new UserTagRowViewModel(tag, this);
                if (tag.Id == editing)
                {
                    row.IsEditing = true;
                    row.Note = note;
                }
                Tags.Add(row);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Listing the user's tags failed");
        }
    }

    /// <summary>"On 12 books", "On no book yet": how many books show a tag.</summary>
    internal static string Books(int books) => books switch
    {
        0 => "on no book yet",
        1 => "on 1 book",
        _ => $"on {books} books",
    };

    /// <summary>What a saved tag found, for the note under it.</summary>
    static string Found(UserTagEntry tag) => tag.Books == 0
        ? "No book's pages use these words often enough yet."
        : $"It's on {(tag.Books == 1 ? "1 book" : $"{tag.Books} books")}, suggested until you keep it.";

    [RelayCommand]
    async Task AddTag()
    {
        if (string.IsNullOrWhiteSpace(NewTagName) || IsSearching) return;
        IsSearching = true;
        try
        {
            var edit = await _tags.SaveAsync(null, NewTagName, NewTagWords);
            AddProblem = edit.Problem;
            AddNote = edit.Tag is { } tag ? $"Added {tag.Name}. {Found(tag)}" : null;
            if (edit.Tag is null) return;
            NewTagName = NewTagWords = "";
            await ReloadTagsAsync();
        }
        finally
        {
            IsSearching = false;
        }
    }

    internal void StartEditing(UserTagRowViewModel row)
    {
        foreach (var other in Tags.Where(t => !ReferenceEquals(t, row))) other.IsEditing = false;
        row.Problem = row.Note = null;
        row.IsEditing = true;
    }

    internal async Task SaveTagAsync(UserTagRowViewModel row)
    {
        if (IsSearching) return;
        IsSearching = true;
        try
        {
            var edit = await _tags.SaveAsync(row.Entry.Id, row.EditName, row.EditWords);
            if (edit.Tag is not { } tag)
            {
                row.Problem = edit.Problem;
                row.Note = null;
                return;
            }
            await ReloadTagsAsync(tag.Id, "Saved. " + Found(tag));
        }
        finally
        {
            IsSearching = false;
        }
    }

    internal async Task DeleteTagAsync(UserTagRowViewModel row)
    {
        await _tags.DeleteAsync(row.Entry.Id);
        AddProblem = null;
        AddNote = $"Deleted {row.Name}. Books where you kept it keep it.";
        await ReloadTagsAsync();
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
