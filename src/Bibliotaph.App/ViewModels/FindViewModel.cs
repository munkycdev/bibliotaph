using System.Globalization;
using Bibliotaph.Core;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Viewer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Find in the reader's book: the find box, every match in the book (or in the adventure being read), Next and
/// Previous going round from the last match to the first and back, and the marks on the pages, with the match in view
/// standing out. Without a find, the marks are the words of the search the book was opened from.
/// </summary>
/// <param name="currentPage">The page in view, where Find starts looking.</param>
/// <param name="partPages">The pages of the adventure being read, or null for a whole file.</param>
/// <param name="goTo">Scrolls to a page and, when given, a spot on it (PDF points from the bottom).</param>
/// <param name="log">Failures; never what was looked for.</param>
public sealed partial class FindViewModel(Func<int> currentPage, Func<PartPages?> partPages, Action<int, double?> goTo, ILogger log) : ObservableObject
{
    const int MaxHits = 2000;

    Func<string, int, Task<IReadOnlyList<SearchHit>>>? _search;
    long _documentId;
    // Moves on as a book opens or closes, so a find that finishes after it is dropped.
    int _version;
    IReadOnlyList<PageHighlight> _terms = [];
    IReadOnlyList<PageHighlight> _hits = [];
    string _found = "";
    int _index = -1;

    /// <summary>What the find box holds.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>"3 of 41", "No matches".</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>Find looks in the whole file rather than the adventure's pages.</summary>
    [ObservableProperty]
    public partial bool WholeFile { get; set; }

    partial void OnWholeFileChanged(bool value)
    {
        _found = "";
        if (Query.Trim().Length > 0) FindCommand.Execute(null);
    }

    /// <summary>The marks on the pages: what Find found, or the words of the search the book was opened from.</summary>
    [ObservableProperty]
    public partial HighlightSet Highlights { get; private set; } = HighlightSet.None;

    /// <summary>The pages Find looks in: the adventure's, unless the reader asked for the whole file.</summary>
    PartPages? Scope => WholeFile ? null : partPages();

    /// <summary>
    /// A PDF is open: <paramref name="search"/> finds every match of some text in it, up to the number of matches
    /// given, in page order.
    /// </summary>
    public void Attach(long documentId, Func<string, int, Task<IReadOnlyList<SearchHit>>> search)
    {
        _version++;
        _documentId = documentId;
        _search = search;
    }

    /// <summary>The book closed: its matches and their marks go. What was typed in the find box stays.</summary>
    public void Detach()
    {
        _version++;
        _search = null;
        _hits = [];
        _index = -1;
        _found = "";
        Status = "";
        Highlights = HighlightSet.None;
    }

    /// <summary>
    /// Marks the words of the search the book was opened from, which show again whenever Find is cleared;
    /// <paramref name="showFirst"/> makes the first of them stand out.
    /// </summary>
    public void MarkTerms(IReadOnlyList<PageHighlight> terms, bool showFirst)
    {
        _terms = terms;
        Highlights = new HighlightSet(terms, terms.Count > 0 && showFirst ? 0 : -1);
    }

    /// <summary>Finds text in the whole book (Enter in the find box). Enter again goes to the next match.</summary>
    [RelayCommand]
    async Task Find()
    {
        var text = Query.Trim();
        if (text.Length == 0 || _search is null)
        {
            Clear();
            return;
        }
        if (text == _found && _hits.Count > 0)
        {
            Next();
            return;
        }
        if (!await SearchAsync(text)) return;
        if (_hits.Count == 0)
        {
            _index = -1;
            Status = Scope is null ? "No matches" : "No matches in this adventure";
            Highlights = new HighlightSet(_terms, -1);
            return;
        }
        // The first match from the page in view onwards, wrapping round to the start.
        var page = currentPage();
        var next = 0;
        for (var i = 0; i < _hits.Count; i++)
        {
            if (_hits[i].PageIndex < page) continue;
            next = i;
            break;
        }
        Show(next);
    }

    /// <summary>The next match (F3, or Enter again), going round to the first after the last.</summary>
    [RelayCommand]
    void Next()
    {
        if (_hits.Count > 0) Show((_index + 1) % _hits.Count);
    }

    /// <summary>The match before (Shift+F3, or Shift+Enter), going round to the last before the first.</summary>
    [RelayCommand]
    void Previous()
    {
        if (_hits.Count > 0) Show((_index - 1 + _hits.Count) % _hits.Count);
    }

    /// <summary>Clears the find box and its marks, leaving the search's own marks.</summary>
    [RelayCommand]
    void Clear()
    {
        Query = "";
        Status = "";
        _found = "";
        _hits = [];
        _index = -1;
        Highlights = new HighlightSet(_terms, -1);
    }

    /// <summary>
    /// Goes to <paramref name="page"/> with <paramref name="phrase"/> marked by Find: its first match on that page,
    /// else the next one after it. When Find can't see it on that page, the page alone, with the book's matches still
    /// marked.
    /// </summary>
    public async Task ShowOnPageAsync(int page, string phrase)
    {
        var text = phrase.Trim();
        var version = _version;
        var found = await SearchAsync(text);
        if (version != _version) return;
        if (!found || _hits.Count == 0)
        {
            if (found) Status = "";
            goTo(page, null);
            return;
        }
        Query = text;
        var at = -1;
        for (var i = 0; i < _hits.Count && at < 0; i++)
            if (_hits[i].PageIndex >= page) at = i;
        if (at < 0 || _hits[at].PageIndex != page)
        {
            // The word isn't on the page as Find sees it (an OCR'd page, say): the page, with the book's matches still marked.
            Highlights = new HighlightSet(_hits, -1);
            Status = "";
            goTo(page, null);
            return;
        }
        Show(at);
    }

    /// <summary>
    /// Finds every match of <paramref name="text"/> in the book, keeping them for Next and Previous. False when Find
    /// failed, or the book closed meanwhile.
    /// </summary>
    async Task<bool> SearchAsync(string text)
    {
        if (_search is not { } search) return false;
        Status = "Searching…";
        var version = _version;
        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await search(text, MaxHits);
            // Reading an adventure inside a file, Find looks in its pages unless told otherwise.
            if (Scope is { } scope) hits = [.. hits.Where(h => scope.Contains(h.PageIndex))];
        }
        catch (Exception ex) when (ex is WorkerException or InvalidOperationException)
        {
            log.LogWarning(ex, "Find in document {DocumentId} failed", _documentId);
            Status = "Find didn't finish";
            return false;
        }
        if (version != _version) return false;
        _found = text;
        _hits = [.. hits.Where(h => h.Rects.Count > 0).Select(h => new PageHighlight(h.PageIndex, [.. h.Rects.Select(PdfRenderer.ToPageRect)]))];
        return true;
    }

    void Show(int index)
    {
        _index = index;
        var hit = _hits[index];
        Highlights = new HighlightSet(_hits, index);
        var count = _hits.Count.ToString("N0", CultureInfo.CurrentCulture);
        Status = $"{(index + 1).ToString("N0", CultureInfo.CurrentCulture)} of {count}{(_hits.Count >= MaxHits ? "+" : "")}";
        goTo(hit.PageIndex, hit.Rects[0].Top);
    }
}
