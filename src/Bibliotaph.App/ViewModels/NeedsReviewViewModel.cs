using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A file Bibliotaph couldn't fully process, and why, in words the user can act on. Trying again can't help a locked
/// file or one with DRM (slice 4i plan, choices 3 and 4), so those offer opening it to unlock, or in another app.
/// </summary>
public sealed record AttentionRow(long? DocumentId, string Title, string Detail, TextAccess Access = TextAccess.Readable)
{
    public bool CanRetry => DocumentId is not null && Access == TextAccess.Readable;

    public bool CanUnlock => DocumentId is not null && Access == TextAccess.Locked;

    public bool CanOpenElsewhere => DocumentId is not null && Access == TextAccess.Protected;
}

/// <summary>
/// Needs review (spec 5.5): Metadata suggestions and Files needing attention, on two tabs. The suggestions are loaded
/// when the page first opens and stay put while the user works through them, so a decided card can still be undone;
/// when the library's count moves away from what the page shows, a link offers the new list.
/// </summary>
public sealed partial class NeedsReviewViewModel(
    ReviewService review,
    IndexQueries queries,
    LibraryStore library,
    IndexingService indexing,
    LibraryActivity activity,
    INavigationService navigation,
    ReaderWindows readers,
    CoverImages covers,
    BookTextActions text,
    ILogger<NeedsReviewViewModel> log) : PageViewModel, IReviewActions
{
    /// <summary>Cards shown at a time; more on request, so thousands of suggestions don't build thousands of cards.</summary>
    const int PageSize = 60;

    List<ReviewItem> _items = [];
    Vocabulary _vocabulary = Vocabulary.Empty;
    IReadOnlyList<FieldSnapshot>? _bulkUndo;
    bool _loaded;

    public override Route Route => Route.NeedsReview;
    public override string Title => "Needs review";

    /// <summary>
    /// New-term cards first, since each covers many books; then "new version?" cards, folders proposed as packs, files
    /// of books owned elsewhere and new versions with places to check; then a page of field cards.
    /// </summary>
    public ObservableCollection<DecidedCardViewModel> Cards { get; } = [];

    /// <summary>Files needing attention: a password to enter, a damaged download, a folder that can't be read, a file inside a ZIP that isn't read.</summary>
    public ObservableCollection<AttentionRow> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesTab))]
    public partial bool IsSuggestionsTab { get; set; } = true;

    public bool IsFilesTab
    {
        get => !IsSuggestionsTab;
        set => IsSuggestionsTab = !value;
    }

    /// <summary>Cards still waiting for a decision, here and beyond the page shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuggestionsTabLabel), nameof(Subtitle), nameof(HasCards))]
    public partial int Remaining { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesTabLabel), nameof(Subtitle))]
    public partial bool HasFiles { get; set; }

    public string SuggestionsTabLabel => $"Metadata suggestions  {Remaining:N0}";

    public string FilesTabLabel => $"Files needing attention · {Files.Count:N0}";

    public string Subtitle => (Remaining, Files.Count) switch
    {
        (0, 0) => "Nothing needs a second look. Everything is ready to use.",
        (1, _) => "One suggestion worth a second look. The rest is ready to use.",
        (0, _) => "No suggestions to look at, but some files need attention.",
        _ => $"{Remaining:N0} suggestions worth a second look. The rest is ready to use.",
    };

    public bool HasCards => Cards.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreLabel), nameof(HasHidden))]
    public partial int Hidden { get; private set; }

    public bool HasHidden => Hidden > 0;

    public string MoreLabel => $"Show {Math.Min(PageSize, Hidden):N0} more ({Hidden:N0} not shown)";

    /// <summary>The library's suggestions changed since the page loaded them (new books, a reindex).</summary>
    [ObservableProperty]
    public partial bool HasUpdates { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>"Everything in its place": loaded, and no card waiting or decided.</summary>
    public bool ShowEmpty => !IsLoading && Cards.Count == 0;

    /// <summary>"Accepted 40 suggestions." while that Accept all can be undone in one go.</summary>
    [ObservableProperty]
    public partial string? BulkMessage { get; private set; }

    public override async Task LoadAsync()
    {
        activity.Refreshed -= OnRefreshed;
        activity.Refreshed += OnRefreshed;
        if (!_loaded) await ReloadAsync();
        else CheckForUpdates();
        RefreshFiles();
    }

    public override void Unload() => activity.Refreshed -= OnRefreshed;

    void OnRefreshed(object? sender, EventArgs e)
    {
        RefreshFiles();
        CheckForUpdates();
    }

    void CheckForUpdates() => HasUpdates = _loaded && !IsLoading && activity.SuggestionCount != Remaining;

    [RelayCommand]
    async Task ReloadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var list = await review.GetQueueAsync();
            _items = [.. list.Items];
            _vocabulary = list.Vocabulary;
            _bulkUndo = null;
            BulkMessage = null;
            Cards.Clear();
            foreach (var term in list.Terms) Cards.Add(new TermCardViewModel(term, _vocabulary, this));
            foreach (var version in list.Versions) Cards.Add(new VersionCardViewModel(version, this));
            foreach (var proposal in list.Packs) Cards.Add(new PackCardViewModel(proposal, await TilesAsync(proposal), this));
            foreach (var match in list.Elsewhere ?? []) Cards.Add(new ElsewhereCardViewModel(match, this));
            foreach (var revision in list.Revisions ?? []) Cards.Add(new RevisionCardViewModel(revision, this));
            Hidden = _items.Count;
            ShowMore();
            Remaining = list.Count;
            _loaded = true;
            HasUpdates = false;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading Needs review failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    void ShowMore()
    {
        var shown = _items.Count - Hidden;
        foreach (var item in _items.Skip(shown).Take(PageSize)) Cards.Add(new ReviewCardViewModel(item, _vocabulary, this));
        Hidden = Math.Max(0, Hidden - PageSize);
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    void RefreshFiles()
    {
        _ = RefreshFilesAsync();
    }

    async Task RefreshFilesAsync()
    {
        try
        {
            var rows = (await queries.GetAttentionAsync())
                .Select(a => new AttentionRow(a.DocumentId, a.Title, $"{StageName(a.Stage)}: {a.Reason ?? "something went wrong"}",
                    TextAccessReasons.Of(a.Status, a.Reason)))
                .Concat(indexing.Unreadable.Select(u => new AttentionRow(null, Path.GetFileName(u.Path), $"Couldn't be read: {u.Reason}")))
                .Concat((await library.GetProblemsAsync()).Select(p => new AttentionRow(null, Path.GetFileName(p.FullPath), $"Not read: {p.Problem} ({p.FullPath})")))
                .ToList();
            if (rows.SequenceEqual(Files)) return;
            Files.Clear();
            foreach (var row in rows) Files.Add(row);
            HasFiles = Files.Count > 0;
            OnPropertyChanged(nameof(FilesTabLabel));
            OnPropertyChanged(nameof(Subtitle));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not list files needing attention");
        }
    }

    static string StageName(Stage stage) => stage switch
    {
        Stage.Probe => "Opening",
        Stage.Text => "Reading text",
        Stage.Covers => "Making a cover",
        Stage.Ocr => "Reading scanned pages",
        _ => stage.ToString(),
    };

    [RelayCommand]
    async Task Retry(AttentionRow row)
    {
        if (row.DocumentId is not { } id) return;
        await indexing.RetryAsync(id);
        activity.Invalidate();
        await RefreshFilesAsync();
    }

    /// <summary>A locked file: the reader asks for its password, and unlocking it makes its text searchable.</summary>
    [RelayCommand]
    void Unlock(AttentionRow row)
    {
        if (row.DocumentId is { } id) OpenDocument(id, row.Title);
    }

    /// <summary>A file with DRM Bibliotaph can't open: the user's default PDF app.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task OpenElsewhere(AttentionRow row)
    {
        if (row.DocumentId is { } id) await text.OpenElsewhereAsync(id);
    }

    [RelayCommand]
    void ReturnToLibrary() => navigation.NavigateTo(Route.Library);

    /// <summary>Undoes the last Accept all, then lists the cards again, since some of them weren't shown.</summary>
    [RelayCommand]
    async Task UndoBulk()
    {
        if (_bulkUndo is not { } undo) return;
        _bulkUndo = null;
        BulkMessage = null;
        await review.UndoAsync(undo);
        activity.Invalidate();
        await ReloadAsync();
    }

    // IReviewActions: each decision goes to catalog.db and the library at once; the sidebar count follows.

    public void Decided(int change)
    {
        Remaining = Math.Max(0, Remaining + change);
        activity.Invalidate();
    }

    public async Task<Func<Task>> AcceptAsync(ReviewItem item)
    {
        var undo = await review.AcceptAsync(item);
        return () => review.UndoAsync([undo]);
    }

    /// <summary>
    /// Accepts every waiting card that proposes the same as <paramref name="item"/>, shown or not. Each shown card can
    /// be undone on its own, and the banner undoes them all.
    /// </summary>
    public async Task AcceptAllAsync(ReviewItem item)
    {
        var key = item.Issue.GroupKey;
        var cards = Cards.OfType<ReviewCardViewModel>().Where(c => !c.IsDone && c.Item.Issue.GroupKey == key).ToList();
        var unshown = _items.Skip(_items.Count - Hidden).Where(i => i.Issue.GroupKey == key).ToList();
        var group = cards.Select(c => c.Item).Concat(unshown).ToList();
        var undo = await review.AcceptAllAsync(group);

        // Cards not shown yet are decided, so they won't be shown.
        _items = [.. _items.Where(i => !unshown.Contains(i))];
        Hidden -= unshown.Count;
        Remaining = Math.Max(0, Remaining - unshown.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            var snapshot = undo[i];
            cards[i].MarkDone($"{cards[i].FieldLabel} is now {cards[i].SuggestedText}.", () => review.UndoAsync([snapshot]));
        }
        _bulkUndo = undo;
        BulkMessage = group.Count == 1 ? "Accepted one suggestion." : $"Accepted {group.Count:N0} suggestions.";
        activity.Invalidate();
    }

    public async Task<Func<Task>> RejectAsync(ReviewItem item)
    {
        var undo = await review.RejectAsync(item);
        return () => review.UndoAsync([undo]);
    }

    public async Task<(string? Problem, Func<Task>? Undo)> EditAsync(ReviewItem item, string typed)
    {
        var (problem, undo) = await review.EditAsync(item, typed);
        return (problem?.Message, undo is null ? null : () => review.UndoAsync([undo]));
    }

    public async Task<Func<Task>> AddTermAsync(PendingTerm term)
    {
        var decision = await review.AddTermAsync(term);
        return () => review.UndoTermAsync(decision);
    }

    public async Task<Func<Task>> MapTermAsync(PendingTerm term, Term target)
    {
        var decision = await review.MapTermAsync(term, target.Key);
        return () => review.UndoTermAsync(decision);
    }

    public async Task<Func<Task>> RejectTermAsync(PendingTerm term)
    {
        var decision = await review.RejectTermAsync(term);
        return () => review.UndoTermAsync(decision);
    }

    /// <summary>A card that isn't waiting any more (its files joined meanwhile) is done, with nothing to undo.</summary>
    public async Task<Func<Task>> AnswerVersionAsync(VersionItem item, VersionAnswer answer)
    {
        var decision = await review.AnswerVersionAsync(item.Version, answer);
        return decision is null ? () => Task.CompletedTask : () => review.UndoVersionAsync(decision);
    }

    public async Task<Func<Task>> AnswerPackAsync(PackProposal proposal, PackAnswer answer) =>
        await review.AnswerPackAsync(proposal, answer) ?? (() => Task.CompletedTask);

    public async Task<Func<Task>> AnswerElsewhereAsync(ElsewhereItem item, ElsewhereAnswer answer) =>
        await review.AnswerElsewhereAsync(item.Match, answer) ?? (() => Task.CompletedTask);

    public async Task<Func<Task>?> UsePlaceAsync(RevisionPlace place)
    {
        var undo = await review.UsePlaceAsync(place);
        activity.Invalidate();
        return undo is null ? null : async () =>
        {
            await undo();
            activity.Invalidate();
        };
    }

    /// <summary>
    /// A place on a "new version" card opens at the page suggested for it, as its session item or note would, so the
    /// reader offers Use this page there too.
    /// </summary>
    public void OpenPlace(RevisionItem item, RevisionPlace place)
    {
        var check = place.Check;
        var request = new ViewerRequest(check.DocumentId, item.BookTitle, check.FirstPdfPage);
        var needsLook = place.Outcome == PageCheck.NeedsLook;
        readers.OpenInMainWindow(place.Place.Kind == PlaceKind.SessionItem
            ? request with
            {
                SessionItem = new SessionItemOpen(place.Place.OwnerId, place.Place.PackTitle ?? "the session",
                    needsLook ? SessionItemState.Changed : SessionItemState.Ready, needsLook ? PagePlaces.ChangedReason : null, check.FirstPdfPage, check.LastPdfPage),
            }
            : request with
            {
                PageNote = new PageNoteOpen(place.Place.OwnerId, needsLook ? PagePlaceState.Changed : PagePlaceState.Ready,
                    needsLook ? PagePlaces.ChangedReason : null, check.FirstPdfPage, check.LastPdfPage),
            });
    }

    public void ShowFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();

    /// <summary>The covers of a proposal's first four images that have one.</summary>
    async Task<IReadOnlyList<CoverTile>> TilesAsync(PackProposal proposal)
    {
        var tiles = new List<CoverTile>();
        foreach (var documentId in proposal.Images)
        {
            if (tiles.Count == 4) break;
            if (await queries.GetCoverAsync(documentId) is { } cover) tiles.Add(new CoverTile(cover, covers));
        }
        return tiles;
    }

    public void OpenDocument(long documentId, string title) => readers.OpenInMainWindow(new ViewerRequest(documentId, title, 0));

    public void Open(ReviewItem item) =>
        readers.OpenInMainWindow(new ViewerRequest(item.DocumentId, item.Title, item.Issue.Evidence is { Pages.Count: > 0 } claim ? claim.Pages[0] : 0));
}
