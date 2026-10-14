using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.App.Tests;

/// <summary>Saved searches on the Library: saved, updated, renamed and deleted with Undo, and put back when the page loads.</summary>
public class SmartViewsViewModelTests
{
    /// <summary>The saved searches, kept in memory; each change is done at once.</summary>
    sealed class FakeDirectory : ISmartViewDirectory
    {
        readonly List<SmartViewInfo> _views = [];
        long _nextId = 1;

        public event EventHandler? Changed;

        public IReadOnlyList<SmartViewInfo> All => [.. _views.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)];

        public SmartViewInfo? Find(long viewId) => _views.FirstOrDefault(v => v.Id == viewId);

        public SmartViewInfo Add(string name, SmartViewDefinition definition)
        {
            var view = new SmartViewInfo(_nextId++, name, definition.ToJson(), DateTime.UtcNow, DateTime.UtcNow);
            _views.Add(view);
            Changed?.Invoke(this, EventArgs.Empty);
            return view;
        }

        public Task<SmartViewInfo> CreateAsync(string name, SmartViewDefinition definition) => Task.FromResult(Add(name, definition));

        public Task<bool> UpdateAsync(long viewId, SmartViewDefinition definition) =>
            Task.FromResult(Replace(viewId, v => v with { Definition = definition.ToJson() }));

        public Task<bool> RenameAsync(long viewId, string name) => Task.FromResult(Replace(viewId, v => v with { Name = name }));

        public Task<SmartViewDeletion?> DeleteAsync(long viewId)
        {
            if (Find(viewId) is not { } view) return Task.FromResult<SmartViewDeletion?>(null);
            _views.Remove(view);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult<SmartViewDeletion?>(new SmartViewDeletion(view, 0));
        }

        public Task<SmartViewInfo> RestoreAsync(SmartViewDeletion deleted)
        {
            _views.Add(deleted.View);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(deleted.View);
        }

        bool Replace(long viewId, Func<SmartViewInfo, SmartViewInfo> change)
        {
            var index = _views.FindIndex(v => v.Id == viewId);
            if (index < 0) return false;
            _views[index] = change(_views[index]);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    /// <summary>The Library page: what it shows now, the pins in its sidebar, and the saved searches it was asked to open.</summary>
    sealed class FakePage : ISavedSearchPage
    {
        public SmartViewDefinition Current { get; set; } = new();

        public HashSet<long> Pinned { get; } = [];

        public List<SmartViewInfo> Opened { get; } = [];

        public int Shown { get; private set; }

        public SmartViewDefinition CurrentView() => Current;

        public void ViewShown() => Shown++;

        public bool IsPinned(SmartViewInfo view) => Pinned.Contains(view.Id);

        public Task TogglePinAsync(SmartViewInfo view)
        {
            if (!Pinned.Remove(view.Id)) Pinned.Add(view.Id);
            return Task.CompletedTask;
        }

        public void Open(SmartViewInfo view) => Opened.Add(view);
    }

    readonly FakeDirectory _views = new();
    readonly FakePage _page = new();
    readonly SmartViewsViewModel _searches;

    public SmartViewsViewModelTests()
    {
        _searches = new SmartViewsViewModel(_views, _page, NullLogger.Instance);
        _searches.Load();
    }

    void Name(string name)
    {
        var dialog = Assert.IsType<SmartViewDialogViewModel>(_searches.ViewDialog);
        dialog.Name = name;
        dialog.SaveCommand.Execute(null);
    }

    [Fact]
    public void Save_search_is_offered_only_for_a_search_a_filter_or_a_scope()
    {
        _page.Current = new SmartViewDefinition(Sort: LibrarySort.Title, Layout: LibraryLayout.List, Tab: ResultsTab.Pages);
        Assert.False(_searches.CanSaveView);

        _page.Current = new SmartViewDefinition("heist");
        Assert.True(_searches.CanSaveView);

        _page.Current = new SmartViewDefinition(Scope: "collection:4");
        Assert.True(_searches.CanSaveView);

        _page.Current = new SmartViewDefinition(Kind: KindFilter.Packs);
        Assert.True(_searches.CanSaveView);
    }

    [Fact]
    public void Save_search_names_what_is_shown_and_shows_it_as_the_saved_search()
    {
        _page.Current = new SmartViewDefinition("heist", Kind: KindFilter.Books);

        _searches.SaveViewCommand.Execute(null);
        Assert.Equal("Save search", _searches.ViewDialog?.Heading);
        Name("  Heists  ");

        Assert.Null(_searches.ViewDialog);
        var saved = Assert.Single(_views.All);
        Assert.Equal("Heists", saved.Name);
        Assert.Equal(_page.Current, SmartViewDefinition.Parse(saved.Definition));
        Assert.Equal(saved, _searches.ActiveView);
        Assert.True(_searches.IsView);
        Assert.False(_searches.CanSaveView);
        Assert.False(_searches.IsViewChanged);
        Assert.Equal(1, _page.Shown);
        Assert.Equal("Saved Heists. It's pinned in the sidebar.", _searches.ViewUndo.Message);
    }

    [Fact]
    public void A_cancelled_name_saves_nothing()
    {
        _page.Current = new SmartViewDefinition("heist");

        _searches.SaveViewCommand.Execute(null);
        _searches.ViewDialog!.CancelCommand.Execute(null);

        Assert.Null(_searches.ViewDialog);
        Assert.Empty(_views.All);
        Assert.False(_searches.IsView);
        Assert.Equal(0, _page.Shown);
    }

    [Fact]
    public async Task Update_keeps_what_is_shown_now_and_Undo_puts_the_saved_search_back()
    {
        var first = new SmartViewDefinition("heist");
        var view = _views.Add("Heists", first);
        _page.Current = first;
        _searches.OpenView(view);

        _page.Current = first with { Kind = KindFilter.Books };
        Assert.True(_searches.IsViewChanged);

        await _searches.UpdateViewCommand.ExecuteAsync(null);

        Assert.False(_searches.IsViewChanged);
        Assert.Equal(_page.Current, SmartViewDefinition.Parse(_views.Find(view.Id)!.Definition));
        Assert.Equal("Updated Heists.", _searches.ViewUndo.Message);

        await _searches.ViewUndo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(first, SmartViewDefinition.Parse(_views.Find(view.Id)!.Definition));
        Assert.True(_searches.IsViewChanged);
    }

    [Fact]
    public void Rename_keeps_the_saved_search_shown_under_its_new_name()
    {
        var view = _views.Add("Heists", new SmartViewDefinition("heist"));
        _searches.OpenView(view);

        _searches.RenameViewCommand.Execute(null);
        Assert.Equal("Heists", _searches.ViewDialog?.Name);
        Name("Capers");

        Assert.Equal("Capers", _searches.ActiveView?.Name);
        Assert.Equal(view.Id, _searches.ActiveView?.Id);
    }

    [Fact]
    public async Task Delete_leaves_the_books_shown_and_Undo_brings_the_saved_search_back()
    {
        var view = _views.Add("Heists", new SmartViewDefinition("heist"));
        _page.Current = new SmartViewDefinition("heist");
        _searches.OpenView(view);

        await _searches.DeleteViewCommand.ExecuteAsync(null);

        Assert.Empty(_views.All);
        Assert.False(_searches.IsView);
        Assert.True(_searches.CanSaveView);
        Assert.Equal("Deleted the saved search Heists. Your books are untouched.", _searches.ViewUndo.Message);

        await _searches.ViewUndo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(view, Assert.Single(_views.All));
        Assert.Equal(view, _searches.ActiveView);
        Assert.False(_searches.IsViewChanged);
    }

    [Fact]
    public void A_saved_search_opened_is_put_back_once_as_the_page_loads()
    {
        var saved = new SmartViewDefinition("heist", Kind: KindFilter.Books);
        _searches.OpenView(_views.Add("Heists", saved));

        Assert.Equal(saved, _searches.TakeViewOnLoad());
        Assert.Null(_searches.TakeViewOnLoad());
    }

    [Fact]
    public void Leaving_a_saved_search_keeps_what_was_shown_for_Back_and_closes_the_dialog_and_note()
    {
        var saved = new SmartViewDefinition("heist");
        _searches.OpenView(_views.Add("Heists", saved));
        _searches.TakeViewOnLoad();
        _page.Current = saved with { Kind = KindFilter.Packs };
        _searches.ViewUndo.Say("Updated Heists.");
        _searches.RenameViewCommand.Execute(null);

        _searches.Unload();

        Assert.Equal(_page.Current, _searches.TakeViewOnLoad());
        Assert.Null(_searches.ViewDialog);
        Assert.Null(_searches.ViewUndo.Message);
    }

    [Fact]
    public void Leaving_the_whole_library_keeps_nothing_to_put_back()
    {
        _page.Current = new SmartViewDefinition("heist");

        _searches.Unload();

        Assert.Null(_searches.TakeViewOnLoad());
    }

    [Fact]
    public async Task The_list_shows_every_saved_search_by_name_with_its_pin_and_follows_changes()
    {
        var maps = _views.Add("Maps", new SmartViewDefinition("map"));
        _views.Add("Heists", new SmartViewDefinition("heist"));
        _page.Pinned.Add(maps.Id);

        _searches.ShowSavedSearchesCommand.Execute(null);

        Assert.True(_searches.IsSavedSearchesOpen);
        Assert.Equal(["Heists", "Maps"], _searches.SavedSearches.Select(r => r.Name));
        Assert.Equal([false, true], _searches.SavedSearches.Select(r => r.IsPinned));

        var row = _searches.SavedSearches[0];
        await _searches.TogglePinSavedSearchCommand.ExecuteAsync(row);
        _searches.ShowSavedSearchRows();

        Assert.Same(row, _searches.SavedSearches[0]);
        Assert.True(row.IsPinned);

        _views.Add("Dungeons", new SmartViewDefinition("dungeon"));

        Assert.Equal(["Dungeons", "Heists", "Maps"], _searches.SavedSearches.Select(r => r.Name));
    }

    [Fact]
    public void Opening_a_saved_search_from_the_list_closes_the_list_and_opens_its_page()
    {
        var view = _views.Add("Heists", new SmartViewDefinition("heist"));
        _searches.ShowSavedSearchesCommand.Execute(null);

        _searches.OpenSavedSearchCommand.Execute(_searches.SavedSearches[0]);

        Assert.False(_searches.IsSavedSearchesOpen);
        Assert.Equal([view], _page.Opened);
    }

    [Fact]
    public async Task Deleting_the_last_saved_search_from_the_list_closes_it()
    {
        _views.Add("Heists", new SmartViewDefinition("heist"));
        _searches.ShowSavedSearchesCommand.Execute(null);

        await _searches.DeleteSavedSearchCommand.ExecuteAsync(_searches.SavedSearches[0]);

        Assert.False(_searches.HasSavedSearches);
        Assert.False(_searches.IsSavedSearchesOpen);
        Assert.Empty(_searches.SavedSearches);
        Assert.Equal("Deleted the saved search Heists. Your books are untouched.", _searches.ViewUndo.Message);
    }
}
