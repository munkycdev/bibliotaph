using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>What saved searches need from the Library page they are saved from and shown on.</summary>
public interface ISavedSearchPage
{
    /// <summary>What the page shows now, as a saved search keeps it.</summary>
    SmartViewDefinition CurrentView();

    /// <summary>The saved search shown changed, or its name did: the page's title, heading and pin follow.</summary>
    void ViewShown();

    bool IsPinned(SmartViewInfo view);

    Task TogglePinAsync(SmartViewInfo view);

    /// <summary>Opens a saved search as a page of its own.</summary>
    void Open(SmartViewInfo view);
}

/// <summary>
/// The Library's saved searches (Smart Views): the one shown, with its Update, Save as new, Rename and Delete; Save
/// search for what is shown when none is; and the toolbar's list of every saved search. A note says what the last
/// change did, beside its Undo.
/// </summary>
public sealed partial class SmartViewsViewModel : ObservableObject
{
    /// <summary>The whole library with no search or filter, which isn't worth saving.</summary>
    static readonly SmartViewDefinition Unfiltered = new();

    readonly ISmartViewDirectory _views;
    readonly ISavedSearchPage _page;
    readonly ILogger _log;
    SmartViewDefinition? _viewOnLoad;
    SmartViewDefinition? _viewSaved;

    /// <param name="views">Every saved search, where changes are saved.</param>
    /// <param name="page">The page the searches are saved from and shown on.</param>
    /// <param name="log">Failures.</param>
    public SmartViewsViewModel(ISmartViewDirectory views, ISavedSearchPage page, ILogger log)
    {
        _views = views;
        _page = page;
        _log = log;
        ViewUndo = new UndoSlot(log);
    }

    /// <summary>The saved search shown, if the page was opened from one or saved as one.</summary>
    public SmartViewInfo? ActiveView { get; private set; }

    public bool IsView => ActiveView is not null;

    /// <summary>The search, filters, order, layout or tab differ from the saved search's: Update and Save as new show.</summary>
    public bool IsViewChanged => ActiveView is not null && _viewSaved is { } saved && saved != _page.CurrentView();

    /// <summary>Save search, while a search, a filter or a scope is in use and no saved search is shown.</summary>
    public bool CanSaveView => ActiveView is null
        && _page.CurrentView() with { Sort = Unfiltered.Sort, Layout = Unfiltered.Layout, Tab = Unfiltered.Tab } != Unfiltered;

    /// <summary>Why the saved search shows less than it was saved with, as when its collection is gone.</summary>
    [ObservableProperty]
    public partial string? ViewNote { get; set; }

    /// <summary>"Saved Haunted places. It's in the sidebar.", beside its Undo.</summary>
    public UndoSlot ViewUndo { get; }

    /// <summary>Naming a saved search, while the dialog is open over the page.</summary>
    [ObservableProperty]
    public partial SmartViewDialogViewModel? ViewDialog { get; private set; }

    /// <summary>Opens the page as a saved search: its search, filters and scope are put back when the page loads.</summary>
    public void OpenView(SmartViewInfo view)
    {
        _viewOnLoad = SmartViewDefinition.Parse(view.Definition);
        ShowView(view, _viewOnLoad);
    }

    /// <summary>The saved search to put back as the page loads, once: the one it was opened as, or the one shown when it was left.</summary>
    public SmartViewDefinition? TakeViewOnLoad()
    {
        var view = _viewOnLoad;
        _viewOnLoad = null;
        return view;
    }

    /// <summary>The page is shown: the list follows changes to the saved searches.</summary>
    public void Load()
    {
        _views.Changed -= OnViewsChanged;
        _views.Changed += OnViewsChanged;
        OnPropertyChanged(nameof(HasSavedSearches));
    }

    /// <summary>
    /// The page is left: the list, the dialog and the note close. A saved search shown keeps what the page shows, to
    /// put it back on Back.
    /// </summary>
    public void Unload()
    {
        _views.Changed -= OnViewsChanged;
        IsSavedSearchesOpen = false;
        ViewDialog?.CancelCommand.Execute(null);
        ViewUndo.Clear();
        if (ActiveView is not null) _viewOnLoad = _page.CurrentView();
    }

    /// <summary>The page's search, filters, order, layout or tab changed: Update, Save as new and Save search follow.</summary>
    public void NotifyCurrentChanged()
    {
        OnPropertyChanged(nameof(IsViewChanged));
        OnPropertyChanged(nameof(CanSaveView));
    }

    void ShowView(SmartViewInfo? view, SmartViewDefinition? saved)
    {
        ActiveView = view;
        _viewSaved = saved;
        OnPropertyChanged(nameof(ActiveView));
        OnPropertyChanged(nameof(IsView));
        _page.ViewShown();
        NotifyCurrentChanged();
    }

    /// <summary>Save search: names the search, filters and scope in use as a saved search, listed in the sidebar.</summary>
    [RelayCommand]
    void SaveView() => AskName("Save search", "Save", "", async name =>
    {
        var current = _page.CurrentView();
        var created = await RunView(() => _views.CreateAsync(name, current));
        if (created is null) return;
        ShowView(created, current);
        ViewUndo.Say(SavedSearchText.Saved(created.Name));
    });

    /// <summary>Update saved search: it now keeps the search and filters as they are.</summary>
    [RelayCommand]
    async Task UpdateView()
    {
        if (ActiveView is not { } view) return;
        var current = _page.CurrentView();
        if (!await RunView(() => _views.UpdateAsync(view.Id, current))) return;
        var previous = _viewSaved;
        ShowView(_views.Find(view.Id) ?? view, current);
        ViewUndo.Offer(SavedSearchText.Updated(view.Name), previous is null ? null : async () =>
        {
            await _views.UpdateAsync(view.Id, previous);
            if (ActiveView?.Id == view.Id) ShowView(_views.Find(view.Id) ?? view, previous);
        });
    }

    /// <summary>Save as new: a second saved search from the changed search and filters, leaving the first as it was.</summary>
    [RelayCommand]
    void SaveViewAsNew() => AskName("Save as a new search", "Save", ActiveView is { } view ? SavedSearchText.SecondName(view.Name) : "", async name =>
    {
        var current = _page.CurrentView();
        var created = await RunView(() => _views.CreateAsync(name, current));
        if (created is null) return;
        ShowView(created, current);
        ViewUndo.Say(SavedSearchText.Saved(created.Name));
    });

    [RelayCommand]
    void RenameView()
    {
        if (ActiveView is not { } view) return;
        AskName("Rename saved search", "Save", view.Name, async name =>
        {
            if (await RunView(() => _views.RenameAsync(view.Id, name)) && _views.Find(view.Id) is { } renamed) ShowView(renamed, _viewSaved);
        });
    }

    /// <summary>Deletes the saved search, never a book. The page keeps showing the same books, and Undo brings it back.</summary>
    [RelayCommand]
    async Task DeleteView()
    {
        if (ActiveView is not { } view) return;
        var deleted = await RunView(() => _views.DeleteAsync(view.Id));
        if (deleted is null) return;
        var saved = _viewSaved;
        ShowView(null, null);
        ViewUndo.Offer(SavedSearchText.Deleted(deleted.View.Name), async () =>
        {
            var back = await _views.RestoreAsync(deleted);
            ShowView(back, saved ?? SmartViewDefinition.Parse(back.Definition));
        });
    }

    void AskName(string heading, string action, string name, Func<string, Task> then)
    {
        var dialog = new SmartViewDialogViewModel(heading, action, name);
        dialog.Closed += async (_, chosen) =>
        {
            ViewDialog = null;
            if (chosen is not null) await then(chosen);
        };
        ViewDialog = dialog;
    }

    async Task<T?> RunView<T>(Func<Task<T>> change)
    {
        try
        {
            return await change();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Changing a saved search failed");
            ViewUndo.Say(SavedSearchText.Failed);
            return default;
        }
    }

    // The Saved searches list: every saved search, pinned or not, from the toolbar.

    public ObservableCollection<SavedSearchRow> SavedSearches { get; } = [];

    public bool HasSavedSearches => _views.All.Count > 0;

    [ObservableProperty]
    public partial bool IsSavedSearchesOpen { get; set; }

    [RelayCommand]
    void ShowSavedSearches()
    {
        ShowSavedSearchRows();
        IsSavedSearchesOpen = true;
    }

    void OnViewsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasSavedSearches));
        ShowSavedSearchRows();
        if (!HasSavedSearches) IsSavedSearchesOpen = false;
    }

    /// <summary>
    /// The rows in name order, as after a pin changed; the same searches keep their rows, so a pin toggled keeps the
    /// keyboard where it was.
    /// </summary>
    public void ShowSavedSearchRows()
    {
        var views = _views.All;
        if (views.SequenceEqual(SavedSearches.Select(r => r.View)))
        {
            foreach (var row in SavedSearches) row.IsPinned = _page.IsPinned(row.View);
            return;
        }
        SavedSearches.Clear();
        foreach (var view in views) SavedSearches.Add(new SavedSearchRow(view, _page.IsPinned(view), this));
    }

    [RelayCommand]
    void OpenSavedSearch(SavedSearchRow row)
    {
        IsSavedSearchesOpen = false;
        _page.Open(row.View);
    }

    [RelayCommand]
    Task TogglePinSavedSearch(SavedSearchRow row) => _page.TogglePinAsync(row.View);

    [RelayCommand]
    void RenameSavedSearch(SavedSearchRow row)
    {
        IsSavedSearchesOpen = false;
        var id = row.View.Id;
        AskName("Rename saved search", "Save", row.Name, async name =>
        {
            if (await RunView(() => _views.RenameAsync(id, name)) && ActiveView?.Id == id && _views.Find(id) is { } renamed) ShowView(renamed, _viewSaved);
        });
    }

    /// <summary>Deletes a saved search, never a book; the one shown is deleted as its own Delete does. Undo brings it back.</summary>
    [RelayCommand]
    async Task DeleteSavedSearch(SavedSearchRow row)
    {
        IsSavedSearchesOpen = false;
        if (ActiveView?.Id == row.View.Id)
        {
            await DeleteView();
            return;
        }
        var deleted = await RunView(() => _views.DeleteAsync(row.View.Id));
        if (deleted is null) return;
        ViewUndo.Offer(SavedSearchText.Deleted(deleted.View.Name), async () => await _views.RestoreAsync(deleted));
    }
}
