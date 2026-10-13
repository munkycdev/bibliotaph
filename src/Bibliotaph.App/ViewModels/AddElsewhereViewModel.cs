using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>A place to own a book in the dialog's Also own list: Print, Foundry VTT, and the user's own.</summary>
public sealed partial class OwnOption(Term term) : ObservableObject
{
    public Term Term { get; } = term;

    public string Label => Term.Label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// "Add a book I own elsewhere": a title, and optionally a game system, a type, a publisher and where it is owned.
/// Save makes the card; the inspector then opens on it for everything else.
/// </summary>
public sealed partial class AddElsewhereViewModel : ObservableObject
{
    readonly ElsewhereService _elsewhere;
    readonly ChosenCovers? _covers;

    /// <summary>The cover picked, already at cover size, kept once the book is made.</summary>
    byte[]? _cover;

    AddElsewhereViewModel(ElsewhereService elsewhere, ChosenCovers? covers, Vocabulary vocabulary)
    {
        _elsewhere = elsewhere;
        _covers = covers;
        static IEnumerable<string> Labels(Vocabulary vocabulary, string name) =>
            vocabulary.InVocabulary(name).Select(t => t.Label).Order(StringComparer.CurrentCultureIgnoreCase);
        // Editions are offered beside systems, by a name that finds them again: "D&D 5e" is how most people name what they play.
        Systems = [NotSet, .. Labels(vocabulary, "system").Concat(vocabulary.InVocabulary("edition").Select(e => EditionName(vocabulary, e)).OfType<string>())
            .Distinct().Order(StringComparer.CurrentCultureIgnoreCase).Select(l => new Choice<string?>(l, l))];
        Types = [NotSet, .. Labels(vocabulary, "type").Select(l => new Choice<string?>(l, l))];
        System = NotSet;
        Type = NotSet;
        Owns = [.. vocabulary.InVocabulary(MetadataFields.AlsoOwn.Vocabulary!).OrderBy(t => t.Key == "print" ? 0 : 1)
            .ThenBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).Select(t => new OwnOption(t))];
    }

    static string? EditionName(Vocabulary vocabulary, Term edition)
    {
        string?[] names = [vocabulary.Find("system", edition.ParentKey ?? "") is { } system ? $"{system.Brief} {edition.Brief}" : null, edition.Label];
        return names.FirstOrDefault(n => n is not null && vocabulary.Resolve("system", n) is null && vocabulary.Resolve("edition", n)?.Key == edition.Key);
    }

    public static async Task<AddElsewhereViewModel> LoadAsync(ElsewhereService elsewhere, ChosenCovers? covers = null) =>
        new(elsewhere, covers, await Task.Run(() => elsewhere.GetVocabularyAsync()));

    /// <summary>Raised when the dialog closes: with the new card, or null when it was cancelled.</summary>
    public event EventHandler<EntryId?>? Closed;

    static readonly Choice<string?> NotSet = new(null, "Not set");

    /// <summary>Game systems and editions. Anything else can be typed in the inspector afterwards.</summary>
    public IReadOnlyList<Choice<string?>> Systems { get; }

    public IReadOnlyList<Choice<string?>> Types { get; }

    public IReadOnlyList<OwnOption> Owns { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial Choice<string?> System { get; set; }

    [ObservableProperty]
    public partial Choice<string?> Type { get; set; }

    [ObservableProperty]
    public partial string Publisher { get; set; } = "";

    /// <summary>
    /// Somewhere not in the list, typed: "Owlbear Rodeo", which becomes a term of the user's own; or the book's page
    /// online, which opens it: "D&amp;D Beyond https://…", or the link alone.
    /// </summary>
    [ObservableProperty]
    public partial string OtherOwn { get; set; } = "";

    public bool CanChooseCover => _covers is not null;

    /// <summary>The picked cover's file name, shown in place of the button that picks it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial string? CoverName { get; private set; }

    public bool HasCover => CoverName is not null;

    /// <summary>"Choose a cover image…": read now, so a picture that can't be one says so before the book is made.</summary>
    [RelayCommand]
    async Task ChooseCover()
    {
        if (_covers is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a cover image", Filter = ChosenCovers.FileFilter };
        if (dialog.ShowDialog() != true) return;
        var (jpeg, problem) = await Task.Run(() => _covers.ReadAsync(dialog.FileName));
        Problem = problem;
        if (jpeg is null) return;
        _cover = jpeg;
        CoverName = global::System.IO.Path.GetFileName(dialog.FileName);
    }

    [RelayCommand]
    void ClearCover()
    {
        _cover = null;
        CoverName = null;
    }

    /// <summary>Why the book couldn't be saved, such as a year that isn't one.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    bool CanSave => !IsSaving && Title.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task Save()
    {
        IsSaving = true;
        Problem = null;
        try
        {
            List<string> owns = [.. Owns.Where(o => o.IsChecked).Select(o => o.Label)];
            if (OtherOwn.Trim().Length > 0) owns.AddRange(MetadataValues.Split(MetadataFields.AlsoOwn, OtherOwn));
            var draft = new ElsewhereDraft(Title.Trim(), System?.Value, Type?.Value is { } type ? [type] : null, Blank(Publisher), owns);
            var (entryId, problem) = await Task.Run(() => _elsewhere.AddAsync(draft));
            if (problem is not null && entryId is null)
            {
                Problem = problem.Message;
                return;
            }
            if (entryId is { } added && _cover is { } cover && _covers is not null) await Task.Run(() => _covers.KeepAsync(added, cover));
            Closed?.Invoke(this, entryId);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);

    static string? Blank(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
