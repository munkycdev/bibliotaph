using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// The books ticked in the Library's Select mode (slice 4e), by document. Ticks stay while the search and filters
/// change, so books can be gathered from several searches; <see cref="SetShown"/> says which are in view, for
/// "12 selected, 3 not shown". Slice 3's Add to collection is to read the same selection.
/// </summary>
public sealed class BookSelection : ObservableObject
{
    readonly Dictionary<long, LibraryItemViewModel> _ticked = [];
    HashSet<long> _shown = [];
    LibraryItemViewModel? _anchor;

    /// <summary>Raised when the ticks change, or Select mode starts or ends.</summary>
    public event EventHandler? Changed;

    /// <summary>Select mode: a click ticks a book instead of opening its details.</summary>
    public bool IsActive { get; private set; }

    public int Count => _ticked.Count;

    public bool HasAny => Count > 0;

    /// <summary>Ticked books the current results don't show.</summary>
    public int NotShown => _ticked.Keys.Count(id => !_shown.Contains(id));

    /// <summary>"12 selected", "12 selected, 3 not shown", or what to do when nothing is ticked.</summary>
    public string Summary => Count == 0
        ? "Click books to select them. Shift+click selects a range, Ctrl+A everything shown."
        : NotShown == 0
            ? $"{Count.ToString("N0", CultureInfo.CurrentCulture)} selected"
            : $"{Count.ToString("N0", CultureInfo.CurrentCulture)} selected, {NotShown.ToString("N0", CultureInfo.CurrentCulture)} not shown";

    /// <summary>The ticked documents, in the order they were ticked.</summary>
    public IReadOnlyList<long> DocumentIds => [.. _ticked.Keys];

    public void Start()
    {
        if (IsActive) return;
        IsActive = true;
        Notify();
    }

    /// <summary>Leaves Select mode. The ticks go with it.</summary>
    public void Stop()
    {
        if (!IsActive) return;
        Untick(_ticked.Values.ToList());
        IsActive = false;
        Notify();
    }

    public void Toggle(LibraryItemViewModel item)
    {
        var wasTicked = _ticked.Remove(item.DocumentId);
        if (!wasTicked) _ticked[item.DocumentId] = item;
        item.IsSelected = !wasTicked;
        _anchor = item;
        Notify();
    }

    /// <summary>Shift+click: ticks every book in <paramref name="shown"/> from the last one clicked to this one.</summary>
    public void SelectRange(IReadOnlyList<LibraryItemViewModel> shown, LibraryItemViewModel item)
    {
        var from = _anchor is null ? -1 : IndexOf(shown, _anchor);
        var to = IndexOf(shown, item);
        if (from < 0 || to < 0)
        {
            Toggle(item);
            return;
        }
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) Tick(shown[i]);
        _anchor = item;
        Notify();
    }

    /// <summary>Ctrl+A: ticks everything in the current results, keeping the ticks from earlier searches.</summary>
    public void SelectAll(IEnumerable<LibraryItemViewModel> shown)
    {
        foreach (var item in shown) Tick(item);
        Notify();
    }

    public void Clear()
    {
        Untick(_ticked.Values.ToList());
        _anchor = null;
        Notify();
    }

    /// <summary>The books the results show now, so ticked books outside them are counted as not shown.</summary>
    public void SetShown(IEnumerable<LibraryItemViewModel> shown)
    {
        _shown = [.. shown.Select(i => i.DocumentId)];
        if (Count > 0) Notify();
    }

    void Tick(LibraryItemViewModel item)
    {
        _ticked[item.DocumentId] = item;
        item.IsSelected = true;
    }

    void Untick(List<LibraryItemViewModel> items)
    {
        foreach (var item in items) item.IsSelected = false;
        _ticked.Clear();
    }

    static int IndexOf(IReadOnlyList<LibraryItemViewModel> shown, LibraryItemViewModel item)
    {
        for (var i = 0; i < shown.Count; i++)
            if (shown[i].DocumentId == item.DocumentId) return i;
        return -1;
    }

    void Notify()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(NotShown));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
