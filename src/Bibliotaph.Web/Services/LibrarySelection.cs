using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The Library's Select mode: ticks are kept by book while the search and filters change, so books can be gathered from
/// several searches; the files of a pack, each on a card of its own, are ticked as their pack. The Select bar says what
/// the ticks hold and offers what can be done with them.
/// </summary>
public sealed class LibrarySelection(ApiClient api)
{
    readonly List<long> _ticked = [];
    long? _anchor;
    int _version;

    public bool On { get; private set; }

    /// <summary>The books ticked, in the order they were ticked.</summary>
    public IReadOnlyList<long> Ticked => _ticked;

    /// <summary>What the Select bar says and offers for the ticks; null until it has been asked.</summary>
    public SelectionInfo? Info { get; private set; }

    public bool IsTicked(long entryId) => _ticked.Contains(entryId);

    public void Start() => On = true;

    /// <summary>Leaves Select mode, and the ticks with it.</summary>
    public void Stop()
    {
        On = false;
        Clear();
        Info = null;
    }

    /// <summary>A click ticks a book or takes its tick away; Shift+click ticks every book from the last one clicked to it.</summary>
    public void Click(long entryId, bool shift, IReadOnlyList<long> shown)
    {
        var from = _anchor is { } anchor ? IndexOf(shown, anchor) : -1;
        var to = IndexOf(shown, entryId);
        if (shift && from >= 0 && to >= 0)
        {
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) Tick(shown[i]);
        }
        else if (!_ticked.Remove(entryId)) _ticked.Add(entryId);
        _anchor = entryId;
    }

    /// <summary>Ctrl+A: every book shown, keeping the ticks from earlier searches.</summary>
    public void TickAll(IEnumerable<long> shown)
    {
        foreach (var id in shown) Tick(id);
    }

    public void Clear()
    {
        _ticked.Clear();
        _anchor = null;
    }

    /// <summary>Asks what the ticks hold now, saying how many aren't among <paramref name="shown"/>; a later ask's answer wins.</summary>
    public async Task RefreshAsync(IReadOnlyList<long> shown)
    {
        var version = ++_version;
        var showing = shown.ToHashSet();
        try
        {
            var info = await api.SelectionAsync(new SelectionQuery([.. _ticked], _ticked.Count(id => !showing.Contains(id))));
            if (version == _version && On) Info = info;
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            // The bar keeps what it said; the next tick asks again.
        }
    }

    void Tick(long entryId)
    {
        if (!_ticked.Contains(entryId)) _ticked.Add(entryId);
    }

    static int IndexOf(IReadOnlyList<long> shown, long id)
    {
        for (var i = 0; i < shown.Count; i++)
            if (shown[i] == id) return i;
        return -1;
    }
}
