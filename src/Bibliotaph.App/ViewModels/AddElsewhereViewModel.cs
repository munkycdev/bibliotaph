using Bibliotaph.App.Services;
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
    readonly IFilePicker _files;

    /// <summary>The cover picked, already at cover size, kept once the book is made.</summary>
    byte[]? _cover;

    AddElsewhereViewModel(ElsewhereService elsewhere, IFilePicker files, ChosenCovers? covers, Vocabulary vocabulary)
    {
        _elsewhere = elsewhere;
        _files = files;
        _covers = covers;
        Systems = [NotSet, .. ElsewhereText.Systems(vocabulary).Select(l => new Choice<string?>(l, l))];
        Types = [NotSet, .. ElsewhereText.Types(vocabulary).Select(l => new Choice<string?>(l, l))];
        System = NotSet;
        Type = NotSet;
        Owns = [.. ElsewhereText.Owns(vocabulary).Select(t => new OwnOption(t))];
    }

    public static async Task<AddElsewhereViewModel> LoadAsync(ElsewhereService elsewhere, IFilePicker files, ChosenCovers? covers = null) =>
        new(elsewhere, files, covers, await Task.Run(() => elsewhere.GetVocabularyAsync()));

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
        if (_files.OpenFile("Choose a cover image", ChosenCovers.FileFilter) is not { } path) return;
        var (jpeg, problem) = await Task.Run(() => _covers.ReadAsync(path));
        Problem = problem;
        if (jpeg is null) return;
        _cover = jpeg;
        CoverName = global::System.IO.Path.GetFileName(path);
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
            var draft = ElsewhereText.Draft(Title, System?.Value, Type?.Value, Publisher, Owns.Where(o => o.IsChecked).Select(o => o.Label), OtherOwn);
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
}
