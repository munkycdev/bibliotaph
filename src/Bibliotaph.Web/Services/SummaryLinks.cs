using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;

namespace Bibliotaph.Web.Services;

/// <summary>Where a Summary figure goes on the web: the Library's address with its search and filters.</summary>
public static class SummaryLinks
{
    public static string Href(SummaryLink link) => link.Goal switch
    {
        SummaryGoal.Collections => "collections",
        SummaryGoal.Binders => "binders",
        _ => "library" + LibraryUrl.QueryString(Query(link), forApi: false),
    };

    public static LibraryQuery Query(SummaryLink link)
    {
        var filter = link.Filter ?? new SummaryFilter();
        string? Value(string field) => filter.Field == field ? filter.Value : null;
        return new LibraryQuery
        {
            Q = link.Query.Length > 0 ? link.Query : null,
            System = Value("system"),
            Type = Value("type"),
            Own = Value("own"),
            Store = Value("store"),
            Picked = Value("setting") is { } setting ? JsonSerializer.Serialize(new Dictionary<string, string[]> { ["setting"] = [setting] }) : null,
            Folder = filter.Folder,
            Level = filter.Level,
            Kind = filter.Kind,
            Copies = filter.Copies,
            Ai = filter.ReadByAi ? "Read" : null,
            Adventures = filter.AdventureCards ? "Cards" : null,
        };
    }

    /// <summary>A folder's name on its bar, from where it is: the last of "OneDrive · Dana › RPG › Maps".</summary>
    public static string FolderName(string where) =>
        where.LastIndexOf(" › ", StringComparison.Ordinal) is var at and >= 0 ? where[(at + 3)..] : where;
}
