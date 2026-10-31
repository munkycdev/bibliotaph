using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// The words binders show, in the Windows app and on the web: their cards, groups, page and dialogs, and what each
/// change says.
/// </summary>
public static class BinderText
{
    public const string Subtitle = "Books and pages gathered in one place, in your order.";

    public const string EmptyTitle = "An empty notebook.";

    public const string EmptyMessage =
        "A binder gathers books and pages in your order, with your notes: a focused set of references, an adventure's maps and encounters, or any other pages you want together. Make one here, or add a book or page from your library to a new one.";

    public const string EmptyBinderMessage = "Add a book or page from your library to get started: right-click a book, or use Add to binder in the reader.";

    public const string Sequence = "Binder sequence";

    public const string SequenceNote = "Books and pages, ready at hand";

    public const string Notes = "Binder notes";

    public const string AnySystem = "Any game system";

    public const string NoBook = "A book no longer in your library";

    public const string Failed = "That didn't work. The log has the details.";

    /// <summary>The headings Add section suggests.</summary>
    public static IReadOnlyList<string> SuggestedSections { get; } = ["Adventure", "Maps", "Encounters", "Rules"];

    /// <summary>"Saturday, 17 October 2026", or null for a binder without a date.</summary>
    public static string? DateLabel(DateOnly? date) => date?.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>"3 items", "No items yet".</summary>
    public static string CountLabel(int items) =>
        items == 0 ? "No items yet" : items == 1 ? "1 item" : $"{items.ToString("N0", CultureInfo.CurrentCulture)} items";

    /// <summary>"At hand · D&amp;D 5e", or "At hand · any system": what a binder kept at hand shows where others show their date.</summary>
    public static string AtHandLabel(string? systemLabel) => $"At hand · {systemLabel ?? "any system"}";

    /// <summary>What a binder's card says under its title: its date, "At hand · …", or "No date".</summary>
    public static string CardDate(SessionPackInfo pack, Func<string, string> systemLabel) =>
        pack.KeepAtHand ? AtHandLabel(pack.GameSystem is { } system ? systemLabel(system) : null) : DateLabel(pack.Date) ?? "No date";

    public static string EarlierLabel(bool shown, int count) => $"{(shown ? "Hide" : "Show")} earlier binders ({count.ToString("N0", CultureInfo.CurrentCulture)})";

    /// <summary>"SATURDAY, 17 OCTOBER 2026", or "BINDER" without a date: the eyebrow over a binder's title.</summary>
    public static string Eyebrow(DateOnly? date) => (DateLabel(date) ?? "Binder").ToUpperInvariant();

    /// <summary>"3 items · Your order, your notes".</summary>
    public static string ListCount(int items) => $"{CountLabel(items)} · Your order, your notes";

    /// <summary>The name a duplicate starts with.</summary>
    public static string CopyTitle(string title) => $"{title} (copy)";

    public static string Added(string what, string binder) => $"Added {what} to {binder}.";

    /// <summary>Add to binder's first item: the binder worked on last.</summary>
    public static string AddTo(string binder) => $"Add to {binder}";

    public const string AddToAnother = "Add to another binder…";

    public const string AddToNew = "Add to a new binder…";

    /// <summary>The picker's heading: "Add The Drowned Abbey to a binder", "Add 3 books to a binder".</summary>
    public static string PickHeading(string what) => $"Add {what} to a binder";

    public const string NewHeading = "Add to a new binder";

    public const string NewAction = "Create and add";

    /// <summary>What a search hit's Add calls its page: "page 42" by its printed label, else its place in the file.</summary>
    public static string PageWhat(string? label, int pdfPage) =>
        $"page {(string.IsNullOrWhiteSpace(label) ? (pdfPage + 1).ToString(CultureInfo.CurrentCulture) : label)}";

    public const string NotAdded = "That couldn't be added.";

    public static string SectionAdded(string name) => $"Added {name}. Items you add now go at its end.";

    public static string SectionDeleted(string name) => $"Deleted the {name} heading. Its items moved up to the section above.";

    public static string Removed(string what, string binder) => $"Removed {what} from {binder}.";

    public static string Deleted(string title) => $"Deleted {title}. Its books are still in your library.";

    public const string DateProblem = "That isn't a date Bibliotaph can read, such as 17/10/2026.";

    /// <summary>"Move to Maps", or "Move to Before the first section".</summary>
    public static string MoveTo(string section) => $"Move to {section}";

    public const string BeforeFirstSection = "Before the first section";

    /// <summary>"The Drowned Abbey · p. 42–44", "… · Whole book", "… · Page on D&amp;D Beyond".</summary>
    public static string Where(string bookTitle, SessionItemInfo item, string? onlinePlace) =>
        item.Link is not null ? $"{bookTitle} · Page on {onlinePlace ?? "the web"}" : $"{bookTitle} · {DetailsText.Pages(item.Range)}";

    /// <summary>Why an item can't open, or what opening it will do differently; null for an ordinary item.</summary>
    public static string? Reason(SessionItemTarget target, bool opensOnline, string? onlinePlace) =>
        opensOnline ? $"Opens on {onlinePlace ?? "the web"} in the online reader." : target.Reason;

    /// <summary>"01".</summary>
    public static string Number(int number) => number.ToString("00", CultureInfo.CurrentCulture);

    /// <summary>The label, or the book's title when there is none.</summary>
    public static string Heading(string? label, string bookTitle) => string.IsNullOrWhiteSpace(label) ? bookTitle : label.Trim();

    /// <summary>The Library limited to a binder's books: its subtitle, and what it says while the binder is empty.</summary>
    public const string ScopeSubtitle = "The books in this binder, whole or for some of their pages.";

    public const string ScopeEmptyTitle = "Nothing in this binder yet.";

    public const string ScopeEmptyMessage = "Add books from the Library, or pages from the reader.";
}

/// <summary>
/// At hand's list in a binder's reader: the other binders kept at hand, those for a game system of this binder's books
/// first, then those for any system, then the rest, each by title; a group with nothing in it is left out.
/// </summary>
public static class AtHandGroups
{
    public const string ForTheseSystems = "For this binder's game systems";

    public const string ForAnySystem = "For any game system";

    public const string ForOtherSystems = "Other game systems";

    public const string NothingInIt = "Nothing in this binder yet";

    public const string NoneAtHand = "No other binder is kept at hand";

    /// <summary>The binders kept at hand besides <paramref name="packId"/>, by title.</summary>
    public static IReadOnlyList<SessionPackInfo> Besides(IReadOnlyList<SessionPackInfo> all, long packId) =>
        [.. all.Where(p => p.KeepAtHand && p.Id != packId).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The game systems of these books, as the Library's filter names them.</summary>
    public static async Task<IReadOnlySet<string>> SystemsOfAsync(LibraryQueries queries, IReadOnlyList<EntryId> entries, CancellationToken ct = default) =>
        entries.Count == 0 ? new HashSet<string>()
            : (await queries.GetFacetCountsAsync("system", new LibraryFilter(entries), ct: ct)).Select(f => f.Value).Where(v => v != SearchQuery.Unknown).ToHashSet();

    public static IReadOnlyList<(string Heading, IReadOnlyList<T> Binders)> Of<T>(IReadOnlyList<T> binders, Func<T, string?> gameSystem, IReadOnlySet<string> systems)
    {
        var matching = binders.Where(b => gameSystem(b) is { } system && systems.Contains(system)).ToList();
        var any = binders.Where(b => gameSystem(b) is null).ToList();
        (string, IReadOnlyList<T>)[] groups =
        [
            (ForTheseSystems, matching),
            (ForAnySystem, any),
            (ForOtherSystems, [.. binders.Except(matching).Except(any)]),
        ];
        return [.. groups.Where(g => g.Item2.Count > 0)];
    }
}

/// <summary>The Binders page's groups, each by title: upcoming, kept at hand, without a date, and earlier ones (date passed).</summary>
public sealed record BinderGroups(IReadOnlyList<SessionPackInfo> Upcoming, IReadOnlyList<SessionPackInfo> AtHand, IReadOnlyList<SessionPackInfo> Undated,
    IReadOnlyList<SessionPackInfo> Earlier)
{
    public static BinderGroups Of(IReadOnlyList<SessionPackInfo> all, DateOnly today)
    {
        var dated = all.Where(p => !p.KeepAtHand).ToList();
        IReadOnlyList<SessionPackInfo> ByTitle(IEnumerable<SessionPackInfo> packs) => [.. packs.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)];
        return new(ByTitle(dated.Where(p => p.Date >= today)), ByTitle(all.Where(p => p.KeepAtHand)), ByTitle(dated.Where(p => p.Date is null)),
            ByTitle(dated.Where(p => p.Date < today)));
    }

    /// <summary>The binder worked on last that isn't kept at hand: Home's Current binder.</summary>
    public static SessionPackInfo? Preparing(IReadOnlyList<SessionPackInfo> all) => all.FirstOrDefault(p => !p.KeepAtHand);
}

/// <summary>
/// One item in a binder as it shows: its number, the book's card (or its file's, for an image in a pack), how it
/// opens now, and where online when it opens in the online reader. An item that can't open stays, saying why.
/// </summary>
public sealed record BinderItemRow(SessionItemInfo Item, int Number, LibraryEntry? Card, SessionItemTarget Target, string? OnlineLink, string? BookLink,
    string? OnlinePlace)
{
    public string BookTitle => Card?.Title ?? BinderText.NoBook;

    public bool OpensOnline => OnlineLink is not null;

    public string Heading => BinderText.Heading(Item.Label, BookTitle);

    public string Where => BinderText.Where(BookTitle, Item, OnlinePlace);

    public string? Reason => BinderText.Reason(Target, OpensOnline, OnlinePlace);

    /// <summary>Dimmed: its file can't be reached, or it's owned elsewhere with nowhere to open.</summary>
    public bool IsDimmed => !Target.CanOpen && !OpensOnline;

    /// <summary>The pages weren't found in the book as it is now: "This page changed: check it".</summary>
    public bool IsChanged => Target.State == SessionItemState.Changed;
}

/// <summary>A row of a binder's list: a section's heading, or an item.</summary>
public sealed record BinderRow(SessionSectionInfo? Section, BinderItemRow? Item);

public static class BinderRows
{
    /// <summary>The binder's rows in order: items before any section, then each section's heading and items, numbered throughout.</summary>
    public static async Task<IReadOnlyList<BinderRow>> BuildAsync(SessionsService sessions, LibraryQueries queries, SessionPackContents contents,
        CancellationToken ct = default)
    {
        var targets = await sessions.ResolveAsync(contents.Items, ct);
        var cards = (await queries.ListAsync(new LibraryFilter([.. contents.Items.Select(i => i.EntryId).Distinct()]), ct: ct)).ToDictionary(e => e.EntryId);
        // Items whose book has no card of its own (an image in a pack) show their file's name and cover.
        var documents = await queries.GetDocumentCardsAsync([.. contents.Items.Where(i => !cards.ContainsKey(i.EntryId))
            .Select(i => targets[i.Id].DocumentId ?? i.Range?.DocumentId).OfType<long>()], ct);
        var rows = new List<BinderRow>();
        var number = 0;
        void AddItems(long? sectionId)
        {
            foreach (var item in contents.Items.Where(i => i.SectionId == sectionId))
            {
                var target = targets[item.Id];
                var card = cards.GetValueOrDefault(item.EntryId)
                    ?? ((target.DocumentId ?? item.Range?.DocumentId) is { } d && documents.TryGetValue(d, out var doc) ? doc with { EntryId = item.EntryId } : null);
                // A page added online, or a whole book owned only online, opens in the online reader.
                var online = item.Link ?? (target.State == SessionItemState.NoFile ? card?.OnlineLink : null);
                rows.Add(new BinderRow(null, new BinderItemRow(item, ++number, card, target, online, card?.OnlineLink ?? online,
                    card?.OnlineLabel ?? (online is null ? null : OnlinePages.SiteOf(online)))));
            }
        }
        AddItems(null);
        foreach (var section in contents.Sections)
        {
            rows.Add(new BinderRow(section, null));
            AddItems(section.Id);
        }
        return rows;
    }

    /// <summary>Move to section in an item's menu: every other section, and before the first one.</summary>
    public static IReadOnlyList<(long SectionId, string Label)> MoveTargets(SessionPackContents contents, SessionItemInfo item) =>
    [
        .. new[] { (SessionStore.NoSection, BinderText.BeforeFirstSection) }.Concat(contents.Sections.Select(s => (s.Id, s.Name)))
            .Where(m => m.Item1 != (item.SectionId ?? SessionStore.NoSection)).Select(m => (m.Item1, BinderText.MoveTo(m.Item2))),
    ];
}
