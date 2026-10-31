using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// A pin as the sidebar and Home show it: its name and icon, and for a book, the book's card, dimmed while its file
/// can't be opened.
/// </summary>
public sealed partial class PinnedItemViewModel(PinTarget target, string label, Geometry icon, string key, LibraryItemViewModel? book = null) : ObservableObject
{
    public PinTarget Target { get; } = target;

    public string Label { get; } = label;

    public Geometry Icon { get; } = icon;

    /// <summary>What <see cref="PageViewModel.PinKey"/> reads while this pin's page is shown.</summary>
    public string Key { get; } = key;

    /// <summary>For a pinned book: its card, which opens it.</summary>
    public LibraryItemViewModel? Book { get; } = book;

    /// <summary>A saved search, which also renames and deletes from its right-click menu.</summary>
    public bool IsSavedSearch => Target.Kind == PinKind.SavedSearch;

    /// <summary>A book whose file is offline or missing: the pin stays, dimmed.</summary>
    public bool IsDimmed => Book?.IsAway == true;

    public string Tip => Book?.AvailabilityTip is { } tip ? $"{Label}: {tip}" : Label;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>
/// The sidebar's Pinned group, kept in memory for the sidebar, Home and every Pin button: the pins in order, and each
/// as an item with its target's current name. It reloads after pins change, and after the collections, binders, saved
/// searches or books they point at do, on the UI thread.
/// </summary>
public sealed class PinDirectory : IPinState
{
    readonly PinsService _pins;
    readonly CollectionDirectory _collections;
    readonly SessionDirectory _binders;
    readonly SmartViewDirectory _views;
    readonly LibraryQueries _queries;
    readonly LibraryStore _library;
    readonly CoverImages _covers;
    readonly ILogger<PinDirectory> _log;
    HashSet<PinTarget> _pinned = [];
    List<PinTarget> _order = [];
    int _version;

    public PinDirectory(PinsService pins, CollectionDirectory collections, SessionDirectory binders, SmartViewDirectory views, LibraryQueries queries,
        LibraryStore library, CoverImages covers, LibraryActivity activity, ILogger<PinDirectory> log)
    {
        _pins = pins;
        _collections = collections;
        _binders = binders;
        _views = views;
        _queries = queries;
        _library = library;
        _covers = covers;
        _log = log;
        pins.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(LoadAsync);
        // A target deleted takes its pin with it, renamed shows its new name; a book's title or file can change too.
        collections.Changed += async (_, _) => await LoadAsync();
        binders.Changed += async (_, _) => await LoadAsync();
        views.Changed += async (_, _) => await LoadAsync();
        activity.Refreshed += async (_, _) => await LoadAsync();
    }

    /// <summary>Raised on the UI thread after the pins were reloaded.</summary>
    public event EventHandler? Changed;

    /// <summary>The pins in order, each with its name; a pin whose target isn't loaded yet waits for it.</summary>
    public ObservableCollection<PinnedItemViewModel> Items { get; } = [];

    public bool Any => Items.Count > 0;

    public bool IsPinned(PinTarget target) => _pinned.Contains(target);

    /// <summary>Whether a book menu's card is pinned.</summary>
    bool IPinState.IsPinned(object? context) => context is LibraryItemViewModel item && IsPinned(PinTarget.Entry(item.EntryId));

    public string PinLabel(PinTarget target) => IsPinned(target) ? "Unpin" : "Pin";

    /// <summary>Pins it at the bottom, or unpins it.</summary>
    public async Task ToggleAsync(PinTarget target)
    {
        try
        {
            if (IsPinned(target)) await Task.Run(() => _pins.UnpinAsync(target));
            else await Task.Run(() => _pins.PinAsync(target));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Changing a pin failed");
        }
    }

    /// <summary>
    /// Moves the pin to <paramref name="index"/> in <see cref="Items"/>, counted once it has been taken out; the reload
    /// that follows raises <see cref="Changed"/>. Returns false if nothing moved. A pin not shown yet keeps its place among the others.
    /// </summary>
    public async Task<bool> MoveAsync(PinTarget target, int index)
    {
        var shown = Items.Select(i => i.Target).ToList();
        if (!shown.Remove(target)) return false;
        index = Math.Clamp(index, 0, shown.Count);
        var others = _order.Where(t => t != target).ToList();
        var at = index < shown.Count ? others.IndexOf(shown[index]) : others.Count;
        try
        {
            return await Task.Run(() => _pins.MoveAsync(target, at));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Moving a pin failed");
            return false;
        }
    }

    public async Task UnpinAsync(PinTarget target)
    {
        try
        {
            await Task.Run(() => _pins.UnpinAsync(target));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unpinning failed");
        }
    }

    public async Task LoadAsync()
    {
        var version = ++_version;
        try
        {
            var pins = await Task.Run(() => _pins.ListAsync());
            var entryIds = pins.Where(p => p.Target.Kind == PinKind.Entry).Select(p => new EntryId(p.Target.Id!.Value)).ToList();
            IReadOnlyList<LibraryEntry> books = [];
            IReadOnlyDictionary<EntryId, EntryAvailability> away = new Dictionary<EntryId, EntryAvailability>();
            if (entryIds.Count > 0) (books, away) = await Task.Run(async () => (await _queries.GetEntriesAsync(entryIds), await _library.GetUnavailableAsync()));
            // A reload that finishes as the app closes has no sidebar left to fill, nor its icons.
            if (version != _version || Application.Current is null) return;
            _pinned = [.. pins.Select(p => p.Target)];
            _order = [.. pins.Select(p => p.Target)];
            var cards = books.ToDictionary(b => b.EntryId);
            var items = pins.Select(p => Item(p.Target, cards, away)).OfType<PinnedItemViewModel>().ToList();
            if (!items.Select(i => (i.Target, i.Label, i.IsDimmed)).SequenceEqual(Items.Select(i => (i.Target, i.Label, i.IsDimmed))))
            {
                Items.Clear();
                foreach (var item in items) Items.Add(item);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the pins failed");
        }
    }

    PinnedItemViewModel? Item(PinTarget target, Dictionary<EntryId, LibraryEntry> books, IReadOnlyDictionary<EntryId, EntryAvailability> away)
    {
        var id = target.Id ?? 0;
        switch (target.Kind)
        {
            case PinKind.Favorites:
                return new(target, LibraryScope.Favorites.Name, Icon("Icon.Heart"), LibraryScope.Favorites.Key);
            case PinKind.Collection when _collections.Find(id) is { } collection:
                return new(target, collection.Name, Icon("Icon.Folders"), ScopeKeys.Collection(id));
            case PinKind.Binder when _binders.All.FirstOrDefault(b => b.Id == id) is { } binder:
                return new(target, binder.Title, Icon("Icon.NotebookTabs"), BinderKey(id));
            case PinKind.SavedSearch when _views.Find(id) is { } view:
                return new(target, view.Name, Icon("Icon.SlidersHorizontal"), LibraryViewModel.SmartViewKey(id));
            case PinKind.Entry when books.TryGetValue(new EntryId(id), out var entry):
                var book = new LibraryItemViewModel(entry, _covers);
                book.ShowAvailability(away.GetValueOrDefault(entry.EntryId));
                return new(target, entry.Title, Icon("Icon.BookOpen"), "entry:" + id.ToString(CultureInfo.InvariantCulture), book);
            default:
                return null;
        }
    }

    /// <summary>What a binder's page reads as its <see cref="PageViewModel.PinKey"/>.</summary>
    public static string BinderKey(long packId) => "binder:" + packId.ToString(CultureInfo.InvariantCulture);

    static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
}
