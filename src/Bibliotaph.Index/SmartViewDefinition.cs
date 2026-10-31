using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bibliotaph.Index;

public enum LibraryLayout
{
    Grid,
    List,
}

public enum ResultsTab
{
    Documents,
    Pages,
}

/// <summary>Where the Library shows the adventures marked inside files.</summary>
public enum AdventureCards
{
    /// <summary>
    /// Inside their books: their files' cards and details list them, and a search shows those it finds on their file's
    /// cover, in place of the file.
    /// </summary>
    InBooks,

    /// <summary>Cards of their own beside their files, whether browsing or searching.</summary>
    Cards,

    /// <summary>Not shown at all: only their files show, and their pages are found under the files.</summary>
    Hidden,
}

/// <summary>
/// What a Smart View shows: the Library's search text, filters, scope, order, layout and tab, never a list of books.
/// Stored as JSON with a version, so a later app can read an older view; one saved before a filter existed reads with
/// that filter off, such as <see cref="Picked"/>, the type-ahead filters' chips. <see cref="Adventures"/> says where the
/// adventures inside files show; <see cref="HideParts"/> is how a view from before there was a choice of three said
/// so, and is read only from those.
/// </summary>
public sealed record SmartViewDefinition(
    string Query = "",
    string? Scope = null,
    bool OnlyAddedHere = false,
    KindFilter Kind = KindFilter.All,
    long? Folder = null,
    string? System = null,
    string? Type = null,
    int? Level = null,
    bool IncludeUnknownLevels = false,
    AiFilter Ai = AiFilter.All,
    bool Copies = false,
    string? Own = null,
    LibrarySort Sort = LibrarySort.RecentlyAdded,
    LibraryLayout Layout = LibraryLayout.Grid,
    ResultsTab Tab = ResultsTab.Documents,
    string? Style = null,
    string? Duration = null,
    int? Players = null,
    string? Store = null,
    bool HideParts = false,
    CollectionFilter Collections = CollectionFilter.All,
    PickedValues? Picked = null,
    AdventureCards Adventures = AdventureCards.InBooks,
    string? Found = null)
{
    /// <summary>2: <see cref="Adventures"/>, which defaults to inside their books, took over from <see cref="HideParts"/>.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The definition stored for a view; an unreadable one shows the whole library rather than failing.</summary>
    public static SmartViewDefinition Parse(string json)
    {
        try
        {
            var view = JsonSerializer.Deserialize<SmartViewDefinition>(json, Json) ?? new();
            // An older view that hid adventures keeps hiding them. Every other one stored "show" only because that was
            // the default then, so it takes today's default and keeps them inside their books.
            return view.Version >= 2 ? view
                : view with { Adventures = view.HideParts ? AdventureCards.Hidden : AdventureCards.InBooks, HideParts = false, Version = CurrentVersion };
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
