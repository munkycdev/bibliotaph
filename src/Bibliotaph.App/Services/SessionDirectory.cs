using System.Globalization;
using System.Windows;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Every session pack, kept in memory for the pages and menus: the current one (worked on last), which Add to session
/// adds to, and the cards the Sessions page and Home show. It reloads after any pack changes, on the UI thread, with
/// the game systems in the library that a binder kept at hand can be for.
/// </summary>
public sealed class SessionDirectory : ISessionChoices
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

    /// <summary>The binder worked on last that isn't kept at hand: Home's Current binder.</summary>
    public SessionPackInfo? Preparing => BinderGroups.Preparing(All);

    /// <summary>A menu's Add to session items, as of the last reload.</summary>
    public IReadOnlyList<SessionMenuChoice> MenuChoices { get; private set; } = [];

    /// <summary>The game systems the library's books are for, by name, with their keys.</summary>
    public IReadOnlyList<FacetCount> Systems { get; private set; } = [];

    /// <summary>Whether any binder other than <paramref name="packId"/> is kept at hand, for the At hand button.</summary>
    public bool HasAtHandBesides(long packId) => All.Any(p => p.KeepAtHand && p.Id != packId);

    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public async Task LoadAsync()
    {
        try
        {
            All = await Task.Run(() => _sessions.ListAsync());
            Systems = await LoadSystemsAsync();
            MenuChoices = MenuChoicesFor(All);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the session packs failed");
        }
    }

    /// <summary>The Add to session items for these packs, the current one first: it, then Choose when there are others, then New.</summary>
    public static IReadOnlyList<SessionMenuChoice> MenuChoicesFor(IReadOnlyList<SessionPackInfo> all) =>
    [
        .. all is [var current, ..] ? [new SessionMenuChoice(SessionMenuKind.Session, current.Id, BinderText.AddTo(current.Title))] : Array.Empty<SessionMenuChoice>(),
        .. all.Count > 1 ? [new SessionMenuChoice(SessionMenuKind.Choose, null, BinderText.AddToAnother)] : Array.Empty<SessionMenuChoice>(),
        new(SessionMenuKind.New, null, BinderText.AddToNew),
    ];

    async Task<IReadOnlyList<FacetCount>> LoadSystemsAsync()
    {
        try
        {
            return [.. (await Task.Run(() => _queries.GetFacetCountsAsync("system", new LibraryFilter())))
                .Where(s => s.Value != SearchQuery.Unknown).OrderBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Listing the library's game systems failed");
            return Systems;
        }
    }

    public SessionPackInfo? Find(long packId) => All.FirstOrDefault(p => p.Id == packId);

    /// <summary>"Saturday, 17 October 2026", or null for a pack without a date.</summary>
    public static string? DateLabel(DateOnly? date) => BinderText.DateLabel(date);

    /// <summary>A game system's name, or its key when no book in the library is for it any more.</summary>
    public string SystemLabel(string key) => Systems.FirstOrDefault(s => s.Value == key)?.Label ?? key;

    /// <summary>"At hand · D&amp;D 5e", or "At hand · any system": what a binder kept at hand shows where others show their date.</summary>
    public string AtHandLabel(SessionPackInfo pack) => BinderText.AtHandLabel(pack.GameSystem is { } system ? SystemLabel(system) : null);

    /// <summary>"3 items", "No items yet".</summary>
    public static string CountLabel(int items) => BinderText.CountLabel(items);

    /// <summary>The cards for these packs: each with its date, item count and the covers of its first two books.</summary>
    public async Task<IReadOnlyList<SessionCardViewModel>> CardsAsync(IReadOnlyList<SessionPackInfo> packs)
    {
        if (packs.Count == 0) return [];
        var entryIds = packs.SelectMany(p => p.FirstEntries).Distinct().ToList();
        var entries = (await Task.Run(() => _queries.ListAsync(new LibraryFilter(entryIds)))).ToDictionary(e => e.EntryId);
        return [.. packs.Select(p => new SessionCardViewModel(p, p.KeepAtHand ? AtHandLabel(p) : DateLabel(p.Date), CountLabel(p.ItemCount),
            [.. p.FirstEntries.Where(entries.ContainsKey).Select(e => new LibraryItemViewModel(entries[e], _covers))], !p.KeepAtHand && p.Date is { } d && d < Today))];
    }

    /// <summary>The Library limited to a pack's books.</summary>
    public static LibraryScope ScopeFor(SessionPackInfo pack) => new(ScopeKeys.Session(pack.Id), pack.Title,
        BinderText.ScopeSubtitle, BinderText.ScopeEmptyTitle, BinderText.ScopeEmptyMessage);
}
