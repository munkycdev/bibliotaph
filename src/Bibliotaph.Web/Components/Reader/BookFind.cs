using System.Globalization;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Components.Reader;

/// <summary>
/// Find in the open book: what's typed, the matches for the words found last, and which of them is shown, as "3 of 12".
/// </summary>
public sealed class BookFind
{
    /// <summary>Find stops counting here, as the server's and the Windows reader's do.</summary>
    public const int MaxMatches = 2000;

    List<(int Page, int Nth)> _hits = [];
    int _index = -1;

    /// <summary>What's typed in the find box.</summary>
    public string Text { get; set; } = "";

    /// <summary>The words the matches are for.</summary>
    public string Found { get; private set; } = "";

    /// <summary>"Searching…", "3 of 12", "No matches" or nothing.</summary>
    public string Status { get; set; } = "";

    public bool HasMatches => _hits.Count > 0;

    /// <summary>Enter again on the same words steps through their matches instead of finding them again.</summary>
    public bool HasFound(string text) => text == Found && _hits.Count > 0;

    /// <summary>Each match, page by page, as the pages count them.</summary>
    public static List<(int Page, int Nth)> Hits(ReaderFind found) =>
        [.. found.Pages.SelectMany(p => Enumerable.Range(0, p.Count).Select(n => (p.Page, n)))];

    /// <summary>
    /// Takes these matches for <paramref name="text"/>: the first from <paramref name="fromPage"/> on, wrapping round
    /// to the start, or -1 when there are none.
    /// </summary>
    public int Take(string text, List<(int Page, int Nth)> hits, int fromPage)
    {
        Found = text;
        _hits = hits;
        if (hits.Count == 0)
        {
            _index = -1;
            return -1;
        }
        var next = hits.FindIndex(h => h.Page >= fromPage);
        return next < 0 ? 0 : next;
    }

    /// <summary>The match after the one shown, or before it, round from the last to the first.</summary>
    public int Next(bool forward) => forward ? (_index + 1) % _hits.Count : (_index - 1 + _hits.Count) % _hits.Count;

    /// <summary>Shows the match at <paramref name="index"/>: its page, and which match on it.</summary>
    public (int Page, int Nth) Show(int index)
    {
        _index = index;
        var culture = CultureInfo.CurrentCulture;
        Status = $"{(index + 1).ToString("N0", culture)} of {_hits.Count.ToString("N0", culture)}{(_hits.Count >= MaxMatches ? "+" : "")}";
        return _hits[index];
    }

    /// <summary>The matches no longer stand, as when Whole file changes, but the words typed do.</summary>
    public void Forget()
    {
        Found = "";
        _hits = [];
    }

    public void Clear()
    {
        Found = "";
        Status = "";
        _hits = [];
        _index = -1;
    }
}
