using System.Globalization;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// A <see cref="LibraryQuery"/> as a query string, for the API and for the Library page's own address, so a search or
/// a filter can be bookmarked and Back goes to the one before. The order and the pack view are the browser's to
/// remember, so the page's address leaves them out.
/// </summary>
public static class LibraryUrl
{
    public static string QueryString(LibraryQuery q, bool forApi)
    {
        List<string> parts = [];
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }
        Add("q", q.Q?.Trim());
        Add("tab", q.IsPagesTab ? "pages" : null);
        if (forApi)
        {
            Add("sort", q.Sort);
            Add("eachFile", q.EachFile ? "true" : null);
        }
        Add("system", q.System);
        Add("type", q.Type);
        Add("level", q.Level?.ToString(CultureInfo.InvariantCulture));
        Add("unknownLevels", q.Level is not null && q.UnknownLevels ? "true" : null);
        Add("players", q.Players?.ToString(CultureInfo.InvariantCulture));
        Add("style", q.Style);
        Add("duration", q.Duration);
        Add("folder", q.Folder?.ToString(CultureInfo.InvariantCulture));
        Add("own", q.Own);
        Add("store", q.Store);
        Add("kind", q.Kind);
        Add("ai", q.Ai);
        Add("copies", q.Copies ? "true" : null);
        Add("adventures", q.Adventures);
        Add("collections", q.Collections);
        Add("picked", q.Picked);
        Add("collection", q.Collection?.ToString(CultureInfo.InvariantCulture));
        Add("addedHere", q.Collection is not null && q.AddedHere ? "true" : null);
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    /// <summary>The book whose details Home asked the Library to open: library?book=12.</summary>
    public static long? Book(string uri) =>
        long.TryParse(Values(uri).GetValueOrDefault("book"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    static Dictionary<string, string> Values(string uri)
    {
        var query = new Uri(uri).Query.TrimStart('?');
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            values[key] = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return values;
    }

    /// <summary>The search and filters in a Library address; anything it doesn't know is left out.</summary>
    public static LibraryQuery Parse(string uri)
    {
        var values = Values(uri);
        string? Text(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        int? Number(string key) => int.TryParse(Text(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new LibraryQuery
        {
            Q = Text("q"),
            Tab = Text("tab"),
            System = Text("system"),
            Type = Text("type"),
            Level = Number("level"),
            UnknownLevels = Text("unknownLevels") == "true",
            Players = Number("players"),
            Style = Text("style"),
            Duration = Text("duration"),
            Folder = long.TryParse(Text("folder"), NumberStyles.None, CultureInfo.InvariantCulture, out var folder) ? folder : null,
            Own = Text("own"),
            Store = Text("store"),
            Kind = Text("kind"),
            Ai = Text("ai"),
            Copies = Text("copies") == "true",
            Adventures = Text("adventures"),
            Collections = Text("collections"),
            Picked = Text("picked"),
            Collection = long.TryParse(Text("collection"), NumberStyles.None, CultureInfo.InvariantCulture, out var collection) ? collection : null,
            AddedHere = Text("addedHere") == "true",
        };
    }
}
