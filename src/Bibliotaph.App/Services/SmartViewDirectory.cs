using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

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
    AdventureCards Adventures = AdventureCards.InBooks)
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

/// <summary>
/// Every saved Smart View, kept in memory for the sidebar, Home and the Library, listed by name. Changes go through
/// here, and <see cref="Changed"/> is raised on the UI thread after each.
/// </summary>
public sealed class SmartViewDirectory(SmartViewStore store, ILogger<SmartViewDirectory> log)
{
    /// <summary>Raised on the UI thread after the views were reloaded.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SmartViewInfo> All { get; private set; } = [];

    public SmartViewInfo? Find(long viewId) => All.FirstOrDefault(v => v.Id == viewId);

    public async Task LoadAsync()
    {
        try
        {
            All = await Task.Run(() => store.ListAsync());
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading the Smart Views failed");
        }
    }

    public Task<SmartViewInfo> CreateAsync(string name, SmartViewDefinition definition) => ThenLoad(() => store.CreateAsync(name, definition.ToJson()));

    public Task<bool> UpdateAsync(long viewId, SmartViewDefinition definition) => ThenLoad(() => store.UpdateAsync(viewId, definition.ToJson()));

    public Task<bool> RenameAsync(long viewId, string name) => ThenLoad(() => store.RenameAsync(viewId, name));

    public Task<SmartViewDeletion?> DeleteAsync(long viewId) => ThenLoad(() => store.DeleteAsync(viewId));

    public Task<SmartViewInfo> RestoreAsync(SmartViewDeletion deleted) => ThenLoad(() => store.RestoreAsync(deleted));

    async Task<T> ThenLoad<T>(Func<Task<T>> change)
    {
        var result = await Task.Run(change);
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(LoadAsync).Task.Unwrap();
        else await LoadAsync();
        return result;
    }
}
