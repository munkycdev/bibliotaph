using System.Globalization;
using System.Windows;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Every session pack, kept in memory for the pages and menus (slice 3 plan, choices 12 and 13): the current one (worked
/// on last), which Add to session adds to, and the cards the Sessions page and Home show. It reloads after any pack
/// changes, on the UI thread.
/// </summary>
public sealed class SessionDirectory
{
    readonly SessionsService _sessions;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly TimeProvider _clock;
    readonly ILogger<SessionDirectory> _log;

    public SessionDirectory(SessionsService sessions, LibraryQueries queries, CoverImages covers, TimeProvider clock, ILogger<SessionDirectory> log)
    {
        _sessions = sessions;
        _queries = queries;
        _covers = covers;
        _clock = clock;
        _log = log;
        sessions.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(LoadAsync);
    }

    /// <summary>Raised on the UI thread after the packs were reloaded.</summary>
    public event EventHandler? Changed;

    /// <summary>Every pack, the current one first.</summary>
    public IReadOnlyList<SessionPackInfo> All { get; private set; } = [];

    /// <summary>The pack worked on last, which Add page and Add to session add to; null before the first.</summary>
    public SessionPackInfo? Current => All.Count > 0 ? All[0] : null;

    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public async Task LoadAsync()
    {
        try
        {
            All = await Task.Run(() => _sessions.ListAsync());
            SessionMenu.Choices =
            [
                .. Current is { } current ? [new SessionMenuChoice(SessionMenuKind.Session, current.Id, $"Add to {current.Title}")] : Array.Empty<SessionMenuChoice>(),
                .. All.Count > 1 ? [new SessionMenuChoice(SessionMenuKind.Choose, null, "Add to another session…")] : Array.Empty<SessionMenuChoice>(),
                new(SessionMenuKind.New, null, "Add to a new session…"),
            ];
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the session packs failed");
        }
    }

    public SessionPackInfo? Find(long packId) => All.FirstOrDefault(p => p.Id == packId);

    /// <summary>"Saturday, 17 October 2026", or null for a pack without a date.</summary>
    public static string? DateLabel(DateOnly? date) => date?.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>"3 items", "No items yet".</summary>
    public static string CountLabel(int items) =>
        items == 0 ? "No items yet" : items == 1 ? "1 item" : $"{items.ToString("N0", CultureInfo.CurrentCulture)} items";

    /// <summary>The cards for these packs: each with its date, item count and the covers of its first two books.</summary>
    public async Task<IReadOnlyList<SessionCardViewModel>> CardsAsync(IReadOnlyList<SessionPackInfo> packs)
    {
        if (packs.Count == 0) return [];
        var entryIds = packs.SelectMany(p => p.FirstEntries).Distinct().ToList();
        var entries = (await Task.Run(() => _queries.ListAsync(new LibraryFilter(entryIds)))).ToDictionary(e => e.EntryId);
        return [.. packs.Select(p => new SessionCardViewModel(p, DateLabel(p.Date), CountLabel(p.ItemCount),
            [.. p.FirstEntries.Where(entries.ContainsKey).Select(e => new LibraryItemViewModel(entries[e], _covers))], p.Date is { } d && d < Today))];
    }

    /// <summary>The Library limited to a pack's books.</summary>
    public static LibraryScope ScopeFor(SessionPackInfo pack) => new(ScopeKeys.Session(pack.Id), pack.Title,
        "The books in this session, whole or for some of their pages.", "Nothing in this session yet.",
        "Add books from the Library, or pages from the reader.");
}
