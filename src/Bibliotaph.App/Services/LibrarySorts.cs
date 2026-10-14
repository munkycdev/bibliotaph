using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// The Library's last order for browsing and for searching, kept from one run to the next. They are kept apart because
/// Best match only means something with a search.
/// </summary>
public sealed class LibrarySorts(SettingsStore settings, ILogger<LibrarySorts> log)
{
    public LibrarySort Browse { get; private set; } = LibrarySort.RecentlyAdded;

    public LibrarySort Search { get; private set; } = LibrarySort.Relevance;

    /// <summary>Read once at start, so a Library page can take its order before its first query.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            Browse = Parse(await settings.GetAsync(SettingKeys.LibrarySort), searching: false) ?? Browse;
            Search = Parse(await settings.GetAsync(SettingKeys.LibrarySearchSort), searching: true) ?? Search;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reading the Library's saved order failed; it starts with the usual one");
        }
    }

    public LibrarySort For(bool searching) => searching ? Search : Browse;

    /// <summary>The order the user picked, kept for the next time the Library is browsed or searched.</summary>
    public async Task RememberAsync(bool searching, LibrarySort sort)
    {
        if (!searching && sort == LibrarySort.Relevance) return;
        if (sort == For(searching)) return;
        if (searching) Search = sort;
        else Browse = sort;
        try
        {
            await settings.SetAsync(searching ? SettingKeys.LibrarySearchSort : SettingKeys.LibrarySort, sort.ToString());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving the Library's order failed; it is kept until the app closes");
        }
    }

    /// <summary>A saved order, or null for one that is missing, unknown, or Best match while browsing.</summary>
    public static LibrarySort? Parse(string? saved, bool searching) =>
        Enum.TryParse<LibrarySort>(saved, out var sort) && Enum.IsDefined(sort) && (searching || sort != LibrarySort.Relevance) ? sort : null;
}
