using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.App.Services;

/// <summary>
/// The books a question to the library is about: those the Library shows for its filter panel and the search box's
/// fields, as the Library last showed them, with labels saying which filters those are. Kept apart from the Library's
/// page, which comes and goes, so Ask in the top bar can use them; before the Library has been shown, the whole library.
/// </summary>
public sealed class LibraryAskScope
{
    /// <summary>The filters as last set, swapped whole so a reader on another thread never sees half of a change.</summary>
    sealed record Snapshot(LibraryFilter? Filter, SearchPlan Plan, IReadOnlyList<string> Labels);

    volatile Snapshot _current = new(null, new SearchPlan(), []);

    /// <summary>Raised when the Library's filters change.</summary>
    public event EventHandler? Changed;

    /// <summary>The Library's filter panel, or null before the Library has been shown.</summary>
    public LibraryFilter? Filter => _current.Filter;

    /// <summary>The search box's fields, without its plain words.</summary>
    public SearchPlan Plan => _current.Plan;

    /// <summary>"D&amp;D 5e", "Adventure", "setting:eberron": the filters in force, for the panel to say what is asked.</summary>
    public IReadOnlyList<string> Labels => _current.Labels;

    /// <summary>The Library ran its query: these are its filters now.</summary>
    /// <param name="filter">Its filter panel's choices and scope.</param>
    /// <param name="query">The search box's query; only its fields are kept.</param>
    /// <param name="labels">The filter panel's chips.</param>
    public void Set(LibraryFilter filter, SearchQuery query, IEnumerable<string> labels)
    {
        var plan = SearchPlan.From(query) with { TextMatch = null, TextExclude = null };
        IReadOnlyList<string> all = [.. labels, .. query.FieldClauses()];
        var was = _current;
        _current = new Snapshot(filter, plan, all);
        if (was.Filter is null || !was.Labels.SequenceEqual(all)) Changed?.Invoke(this, EventArgs.Empty);
    }
}
