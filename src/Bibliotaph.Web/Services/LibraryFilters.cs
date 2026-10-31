using System.Globalization;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Web.Services;

/// <summary>A filter that is on, as its chip shows it: its × turns it off. A folded one is under More filters.</summary>
public sealed record FilterChip(string Label, string Name, Func<LibraryQuery, LibraryQuery> Clear, bool Folded = false);

/// <summary>The Library's filters as the page shows them: the chips for those on, and how the toolbar counts them.</summary>
public static class LibraryFilters
{
    /// <summary>The filters that are on, common ones first, in the Windows app's words.</summary>
    public static List<FilterChip> Chips(LibraryQuery query, LibraryMenus m)
    {
        List<FilterChip> chips = [];
        void Add(bool on, string label, string filter, Func<LibraryQuery, LibraryQuery> clear, bool folded = false)
        {
            if (on) chips.Add(new FilterChip(label, $"Turn off the {filter} filter", clear, folded));
        }
        Add(query.System is not null, Label(m.Systems, query.System), "game system", q => q with { System = null });
        Add(query.Type is not null, Label(m.Types, query.Type), "type", q => q with { Type = null });
        Add(query.Level is not null, $"Level {query.Level}{(query.UnknownLevels ? " or unknown" : "")}", "character level", q => q with { Level = null });
        Add(query.Players is not null, Label(LibraryChoices.Players, Text(query.Players)), "players", q => q with { Players = null });
        Add(query.Style is not null, Label(m.Styles, query.Style), "style", q => q with { Style = null }, true);
        Add(query.Duration is not null, Label(m.Durations, query.Duration), "duration", q => q with { Duration = null }, true);
        foreach (var box in m.Boxes)
            foreach (var pick in box.Picked)
            {
                var label = $"{box.Label}: {pick.Label}";
                chips.Add(new FilterChip(label, $"Remove {label}", q => q with { Picked = PickedText.Remove(q.Picked, box.Key, pick.Value) }, true));
            }
        Add(query.Folder is not null, $"Folder: {Label(m.Folders, Text(query.Folder))}", "source folder", q => q with { Folder = null }, true);
        Add(query.Own is not null, $"Also own: {Label(m.Owns, query.Own)}", "also own", q => q with { Own = null }, true);
        Add(query.Store is not null, $"Get it from: {Label(m.Stores, query.Store)}", "get it from", q => q with { Store = null }, true);
        Add(query.Kind is not null, Label(LibraryChoices.Kinds, query.Kind), "kind", q => q with { Kind = null }, true);
        Add(query.Ai is not null, Label(LibraryChoices.Ai, query.Ai), "AI", q => q with { Ai = null }, true);
        Add(query.Copies, Label(LibraryChoices.Copies, "true"), "copies", q => q with { Copies = false }, true);
        Add(query.Found is not null, query.Found == SearchQuery.Unknown ? FoundOnline.NotFound : $"Found in {Label(m.Found, query.Found)}", "found online",
            q => q with { Found = null }, true);
        Add(query.Adventures is not null, query.Adventures == "Hidden" ? "Adventures inside files hidden" : "Adventures as cards", "adventures inside files",
            q => q with { Adventures = null }, true);
        Add(query.Collections is not null, Label(LibraryChoices.Collections, query.Collections), "collection", q => q with { Collections = null }, true);
        return chips;
    }

    /// <summary>"Filters", or "Filters · 2" with two on.</summary>
    public static string FiltersLabel(IReadOnlyCollection<FilterChip> chips) => chips.Count > 0 ? $"Filters · {chips.Count}" : "Filters";

    /// <summary>"More filters", with how many of them are on while they are folded away.</summary>
    public static string MoreFiltersLabel(bool shown, IEnumerable<FilterChip> chips) =>
        shown ? "Fewer filters" : chips.Count(c => c.Folded) is var n and > 0 ? $"More filters · {n}" : "More filters";

    /// <summary>A choice's label without its count: "D&amp;D 5e (12)" reads "D&amp;D 5e" on its chip.</summary>
    public static string Label(IReadOnlyList<FilterChoice> menu, string? value)
    {
        var label = menu.FirstOrDefault(c => c.Value == value)?.Label ?? value ?? "";
        return label.EndsWith(')') && label.LastIndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? label[..i] : label;
    }

    /// <summary>The query as it filters, so two that show the same books compare equal.</summary>
    public static LibraryQuery Comparable(LibraryQuery q)
    {
        var searching = q.IsSearching;
        return q with
        {
            Q = searching ? q.Q!.Trim() : null,
            Tab = searching && q.IsPagesTab ? "pages" : null,
            Sort = null,
            EachFile = false,
            UnknownLevels = q.Level is not null && q.UnknownLevels,
            AddedHere = q.Collection is not null && q.AddedHere,
            Favorites = q.Favorites && q.Collection is null,
            Binder = q.Collection is null ? q.Binder : null,
            Picked = PickedText.Canonical(q.Picked),
        };
    }

    /// <summary>A number as a filter's menu holds it.</summary>
    public static string Text(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
