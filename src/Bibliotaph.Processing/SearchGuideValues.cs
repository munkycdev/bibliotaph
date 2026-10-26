using Bibliotaph.Catalog;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What the search box's field guide lists after a field's colon: the field's values among the books the Library
/// shows, most common first, with how many books have each. The Windows app and the web list the same.
/// </summary>
public static class SearchGuideValues
{
    public static async Task<IReadOnlyList<GuideValue>> LoadAsync(LibraryQueries queries, LibraryStore library, SearchFieldInfo field,
        CancellationToken ct = default)
    {
        var filter = new LibraryFilter(await library.GetVisibleEntryIdsAsync(ct: ct));
        if (field.Kind == SearchFieldKind.Format)
            return SearchGuide.FormatValues((await queries.GetFormatCountsAsync(filter, ct)).Select(Value));
        // A field with no vocabulary of its own, such as creatures found in chapters, lists what its facets hold.
        var counts = await queries.GetFacetCountsAsync(field.Metadata?.Vocabulary ?? SearchPlan.FacetFields(field.Field)?[0] ?? field.Name, filter, ct: ct);
        return [.. counts.Select(Value)];

        static GuideValue Value(FacetCount count) => new(count.Value, count.Label, count.Count);
    }
}
