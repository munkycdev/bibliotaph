using Bibliotaph.Api.Contracts;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The books a question to the library is about: those the Library shows for its filters and the
/// search box's fields, as the Library last showed them, with labels saying which filters those are. Kept apart from
/// the Library page, which comes and goes, so Ask in the top bar can use them; before the Library has been shown, the
/// whole library.
/// </summary>
public sealed class AskScope
{
    /// <summary>The Library's query; the server keeps only the search box's fields, not its plain words.</summary>
    public LibraryQuery Query { get; private set; } = new();

    /// <summary>"D&amp;D 5e", "Adventure", "setting:eberron": the filters in force, for the panel to say what is asked.</summary>
    public IReadOnlyList<string> Labels { get; private set; } = [];

    public event Action? Changed;

    /// <summary>The Library showed its results: these are its filters now.</summary>
    /// <param name="query">The Library's filters and search.</param>
    /// <param name="labels">Its filter chips.</param>
    public void Set(LibraryQuery query, IEnumerable<string> labels)
    {
        IReadOnlyList<string> all = [.. labels, .. SearchQuery.Parse(query.IsSearching ? query.Q : "").FieldClauses()];
        Query = query;
        if (Labels.SequenceEqual(all)) return;
        Labels = all;
        Changed?.Invoke();
    }
}
