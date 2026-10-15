using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
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
/// file or one with DRM, so those offer opening it to unlock, or in another app.
/// </summary>
public sealed record AttentionRow(long? DocumentId, string Title, string Detail, TextAccess Access = TextAccess.Readable, string? Error = null)
{
    public bool CanRetry => DocumentId is not null && Access == TextAccess.Readable;

    public bool CanUnlock => DocumentId is not null && Access == TextAccess.Locked;

    public bool CanOpenElsewhere => DocumentId is not null && Access == TextAccess.Protected;

    /// <summary>A stage threw something nothing expected: a bug to report, with <see cref="Error"/> as what to paste.</summary>
    public bool IsBug => Error is not null;
}

/// <summary>
/// Files that need attention for the same reason, as one card: one bug can fail hundreds of books, and a card each would
/// bury everything else. A group of one reads as that file's own card; a bigger one lists its files when opened.
/// </summary>
public sealed partial class AttentionGroup(IReadOnlyList<AttentionRow> rows) : ObservableObject
{
    public IReadOnlyList<AttentionRow> Rows { get; } = rows;

    public AttentionRow First => Rows[0];

    public bool IsSingle => Rows.Count == 1;

    public bool IsMany => Rows.Count > 1;

    public string Heading => IsSingle ? First.Title : $"{Rows.Count:N0} files";

    public string Detail => First.Detail;

    /// <summary>Try again for the whole group: shown when any of its files can be tried again.</summary>
    public bool CanRetry => Rows.Any(r => r.CanRetry);

    public bool CanUnlock => IsSingle && First.CanUnlock;

    public bool CanOpenElsewhere => IsSingle && First.CanOpenElsewhere;

    public bool IsBug => First.IsBug;

    /// <summary>Copy details put this card's bug on the clipboard.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    public partial bool Copied { get; set; }

    public string CopyLabel => Copied ? "Copied" : "Copy details";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    public partial bool IsExpanded { get; set; }

    public string ToggleLabel => IsExpanded ? "Hide the files" : $"Show the {Rows.Count:N0} files";

    /// <summary>Rows with the same reason and access go together, in the order the first of each was listed.</summary>
    public static List<AttentionGroup> Of(IEnumerable<AttentionRow> rows) =>
        [.. rows.GroupBy(r => (r.Detail, r.Access)).Select(g => new AttentionGroup([.. g]))];

    /// <summary>The same files for the same reason, so a refresh that changes nothing leaves the cards, and what's open, alone.</summary>
    public bool SameAs(AttentionGroup other) => Rows.SequenceEqual(other.Rows);
}

/// <summary>An entry in Needs review's Show menu: everything, a kind of card, or the field suggestions are for.</summary>
public sealed partial class ReviewShowChoice(string key, string name) : ObservableObject
{
    public const string Everything = "";

    public string Key { get; } = key;

    public string Name { get; } = name;

    /// <summary>Cards of this kind still waiting, which goes down as they're decided.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int Count { get; set; }

    public string Label => $"{Name} · {Count:N0}";

    public override string ToString() => Label;
}

/// <summary>
/// Needs review: Metadata suggestions and Files needing attention, on two tabs. The suggestions are loaded when the
/// page first opens and stay put while the user works through them, so a decided card can still be undone; when the
/// library's count moves away from what the page shows, a link offers the new list.
/// </summary>
public sealed partial class NeedsReviewViewModel(
    ReviewService review,
    IndexQueries queries,
    LibraryStore library,
    IIndexingStatus indexing,
    IJobControl jobs,
    IStageCatalog stages,
    LibraryActivity activity,
    INavigationService navigation,
    SettingsLinks links,
    ReaderWindows readers,
    CoverImages covers,
    BookTextActions text,
    PartsService parts,
    PartProposalService proposals,
    LibraryPages pages,
    IClipboard clipboard,
    IDesktopShell shell,
    ILogger<NeedsReviewViewModel> log) : PageViewModel, IReviewActions
{
    /// <summary>Cards shown at a time; more on request, so thousands of suggestions don't build thousands of cards.</summary>
    const int PageSize = 60;

    /// <summary>The Show menu's kinds of card, in the order the page lists them; field suggestions follow, by field.</summary>
    static readonly (string Key, string Name)[] Kinds =
    [
        ("terms", "New terms"), ("versions", "New versions"), ("packs", "Image packs"), ("elsewhere", "Owned elsewhere"),
        ("revisions", "Places to check"), ("parts", "Adventures"), ("corrections", "Corrections"), ("rules", "Folder rules"),
        ("folders", "Collections from folders"), ("suggestions", "Books for collections"),
    ];

    /// <summary>Every card that isn't a field suggestion, made once per load so a decided one keeps its Undo whatever is shown.</summary>
    List<DecidedCardViewModel> _cards = [];

    /// <summary>Field suggestions, with their cards made as they're first shown and kept for the same reason.</summary>
    List<ReviewItem> _items = [];
    readonly Dictionary<ReviewItem, ReviewCardViewModel> _fieldCards = [with(ReferenceEqualityComparer.Instance)];

    /// <summary>How many of the field suggestions <see cref="Show"/> picks are on the page.</summary>
    int _shown;
    bool _choosing;
    Vocabulary _vocabulary = Vocabulary.Empty;
    bool _loaded;

    /// <summary>
    /// The reader every card opens its book in, beside the page: one window showing whichever book was opened last, so
    /// working down the cards with the book beside them doesn't leave a window per card.
    /// </summary>
    ViewerViewModel? _window;

    public override Route Route => Route.NeedsReview;
    public override string Title => "Needs review";

    /// <summary>
    /// New-term cards first, since each covers many books; then "new version?" cards, folders proposed as packs, files
    /// of books owned elsewhere and new versions with places to check; then a page of field cards.
    /// </summary>
    public ObservableCollection<DecidedCardViewModel> Cards { get; } = [];

    /// <summary>
    /// Files needing attention: a password to enter, a damaged download, a folder that can't be read, a file inside a ZIP
    /// that isn't read, a book the AI couldn't read. Files with the same reason share a card.
    /// </summary>
    public ObservableCollection<AttentionGroup> Files { get; } = [];

    /// <summary>Every file listed, across the cards.</summary>
    public IEnumerable<AttentionRow> FileRows => Files.SelectMany(g => g.Rows);

    public int FileCount => Files.Sum(g => g.Rows.Count);

    /// <summary>Try all again: at least one listed file can be tried again.</summary>
    public bool CanRetryAll => Files.Any(g => g.CanRetry);

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

    public string FilesTabLabel => $"Files needing attention · {FileCount:N0}";

    public string Subtitle => (Remaining, FileCount) switch
    {
        (0, 0) => "Nothing needs a second look. Everything is ready to use.",
        (1, _) => "One suggestion worth a second look. The rest is ready to use.",
        (0, _) => "No suggestions to look at, but some files need attention.",
        _ => $"{Remaining:N0} suggestions worth a second look. The rest is ready to use.",
    };

    public bool HasCards => Cards.Count > 0;

    /// <summary>Everything, each kind of card there is, and each field with suggestions, with how many are waiting.</summary>
    public ObservableCollection<ReviewShowChoice> ShowChoices { get; } = [];

    /// <summary>Only worth offering when there's more than one kind to choose between.</summary>
    public bool HasShowChoices => ShowChoices.Count > 2;

    [ObservableProperty]
    public partial ReviewShowChoice? Show { get; set; }

    partial void OnShowChanged(ReviewShowChoice? value)
    {
        if (!_choosing && value is not null) ShowCards();
    }

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
    public UndoSlot BulkUndo { get; } = new(log);

    public override async Task LoadAsync()
    {
        activity.Refreshed -= OnRefreshed;
        activity.Refreshed += OnRefreshed;
        if (links.TakeFilesRequest()) IsFilesTab = true;
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
            BulkUndo.Clear();
            _fieldCards.Clear();
            List<DecidedCardViewModel> cards = [];
            foreach (var term in list.Terms) cards.Add(new TermCardViewModel(term, _vocabulary, this));
            foreach (var version in list.Versions) cards.Add(new VersionCardViewModel(version, this));
            foreach (var proposal in list.Packs) cards.Add(new PackCardViewModel(proposal, await TilesAsync(proposal), this));
            foreach (var match in list.Elsewhere ?? []) cards.Add(new ElsewhereCardViewModel(match, this));
            foreach (var revision in list.Revisions ?? []) cards.Add(new RevisionCardViewModel(revision, this));
            foreach (var proposal in list.Proposals ?? []) cards.Add(new PartProposalCardViewModel(proposal, await GetPageLabelsAsync(proposal.DocumentId), this));
            foreach (var part in list.Parts ?? []) cards.Add(new UnplacedPartCardViewModel(part, await GetPageLabelsAsync(part.DocumentId), this));
            foreach (var correction in list.Corrections ?? []) cards.Add(new CorrectionCardViewModel(correction, this));
            foreach (var rule in list.Rules ?? []) cards.Add(new FolderRuleCardViewModel(rule, _vocabulary, this));
            foreach (var folder in list.Folders ?? []) cards.Add(new FolderCollectionCardViewModel(folder, this));
            foreach (var suggestion in list.Suggestions ?? []) cards.Add(new CollectionSuggestionCardViewModel(suggestion, _vocabulary, this));
            _cards = cards;
            Remaining = list.Count;
            ListChoices();
            ShowCards();
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
        var matching = _items.Where(i => IsShown(FieldKey(i))).ToList();
        foreach (var item in matching.Skip(_shown).Take(PageSize)) Cards.Add(CardFor(item));
        _shown = Math.Min(matching.Count, _shown + PageSize);
        Hidden = matching.Count - _shown;
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>The cards <see cref="Show"/> picks, with the first page of its field suggestions.</summary>
    void ShowCards()
    {
        Cards.Clear();
        foreach (var card in _cards.Where(c => IsShown(KindOf(c)))) Cards.Add(card);
        _shown = 0;
        ShowMore();
    }

    ReviewCardViewModel CardFor(ReviewItem item)
    {
        if (!_fieldCards.TryGetValue(item, out var card)) _fieldCards[item] = card = new ReviewCardViewModel(item, _vocabulary, this);
        return card;
    }

    bool IsShown(string kind) => Show is not { } show || show.Key == ReviewShowChoice.Everything || show.Key == kind;

    static string KindOf(DecidedCardViewModel card) => card switch
    {
        TermCardViewModel => "terms",
        VersionCardViewModel => "versions",
        PackCardViewModel => "packs",
        ElsewhereCardViewModel => "elsewhere",
        RevisionCardViewModel => "revisions",
        PartProposalCardViewModel or UnplacedPartCardViewModel => "parts",
        CorrectionCardViewModel => "corrections",
        FolderRuleCardViewModel => "rules",
        FolderCollectionCardViewModel => "folders",
        CollectionSuggestionCardViewModel => "suggestions",
        ReviewCardViewModel field => FieldKey(field.Item),
        _ => "",
    };

    static string FieldKey(ReviewItem item) => "field:" + item.Issue.Field.Key;

    /// <summary>The Show menu for what was loaded, keeping what was picked when it's still there.</summary>
    void ListChoices()
    {
        var picked = Show?.Key ?? ReviewShowChoice.Everything;
        _choosing = true;
        try
        {
            ShowChoices.Clear();
            ShowChoices.Add(new ReviewShowChoice(ReviewShowChoice.Everything, "Everything"));
            var kinds = _cards.Select(KindOf).ToHashSet();
            foreach (var (key, name) in Kinds.Where(k => kinds.Contains(k.Key))) ShowChoices.Add(new ReviewShowChoice(key, name));
            foreach (var item in _items.DistinctBy(i => i.Issue.Field.Key).OrderBy(i => i.Issue.Field.Label, StringComparer.CurrentCultureIgnoreCase))
                ShowChoices.Add(new ReviewShowChoice(FieldKey(item), item.Issue.Field.Label));
            Show = ShowChoices.FirstOrDefault(c => c.Key == picked) ?? ShowChoices[0];
        }
        finally
        {
            _choosing = false;
        }
        CountChoices();
        OnPropertyChanged(nameof(HasShowChoices));
    }

    /// <summary>Cards still waiting for a decision, per entry in the Show menu.</summary>
    void CountChoices()
    {
        var waiting = _cards.Where(c => !c.IsDone).Select(KindOf)
            .Concat(_items.Where(i => !(_fieldCards.TryGetValue(i, out var card) && card.IsDone)).Select(FieldKey))
            .CountBy(k => k)
            .ToDictionary();
        foreach (var choice in ShowChoices)
            choice.Count = choice.Key == ReviewShowChoice.Everything ? waiting.Values.Sum() : waiting.GetValueOrDefault(choice.Key);
    }

    void RefreshFiles()
    {
        RefreshFilesAsync().FireAndForget();
    }

    async Task RefreshFilesAsync()
    {
        try
        {
            var rows = (await queries.GetAttentionAsync())
                .Select(Row)
                .Concat(indexing.Unreadable.Select(u => new AttentionRow(null, Path.GetFileName(u.Path), $"Couldn't be read: {u.Reason}")))
                .Concat((await library.GetProblemsAsync()).Select(p => new AttentionRow(null, Path.GetFileName(p.FullPath), $"Not read: {p.Problem} ({p.FullPath})")))
                .ToList();
            var groups = AttentionGroup.Of(rows);
            if (groups.Count == Files.Count && groups.Zip(Files).All(p => p.First.SameAs(p.Second))) return;
            // A card opened to its files stays open when the list is made again.
            var open = Files.Where(g => g.IsExpanded).Select(g => (g.Detail, g.First.Access)).ToHashSet();
            Files.Clear();
            foreach (var group in groups)
            {
                group.IsExpanded = open.Contains((group.Detail, group.First.Access));
                Files.Add(group);
            }
            HasFiles = Files.Count > 0;
            OnPropertyChanged(nameof(FilesTabLabel));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(CanRetryAll));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not list files needing attention");
        }
    }

    /// <summary>A failed or blocked stage in words: a bug says so, and keeps what to paste into a report.</summary>
    AttentionRow Row(AttentionItem item)
    {
        var step = stages.Find(item.Stage)?.Names.Step ?? item.Stage.ToString();
        var reason = item.Reason ?? "something went wrong";
        if (reason.StartsWith(IndexingService.UnexpectedError, StringComparison.Ordinal))
            return new AttentionRow(item.DocumentId, item.Title, $"{step}: Bibliotaph hit a bug ({reason[IndexingService.UnexpectedError.Length..]})",
                Error: $"{item.Stage}: {reason}");
        return new AttentionRow(item.DocumentId, item.Title, $"{step}: {reason}", TextAccessReasons.Of(item.Status, item.Reason));
    }

    [RelayCommand]
    async Task Retry(AttentionRow row)
    {
        if (row.DocumentId is not { } id) return;
        await RetryAsync([id]);
    }

    [RelayCommand]
    static void ToggleFiles(AttentionGroup group) => group.IsExpanded = !group.IsExpanded;

    /// <summary>Try again on a card of many: each of its files that can be tried again.</summary>
    [RelayCommand]
    Task RetryGroup(AttentionGroup group) => RetryAsync(group.Rows);

    /// <summary>Try all again: every listed file that can be tried again, but not a locked or protected one.</summary>
    [RelayCommand]
    Task RetryAll() => RetryAsync(FileRows);

    Task RetryAsync(IEnumerable<AttentionRow> rows) =>
        RetryAsync([.. rows.Where(r => r.CanRetry).Select(r => r.DocumentId!.Value).Distinct()]);

    async Task RetryAsync(IReadOnlyCollection<long> documentIds)
    {
        await jobs.RetryAsync(documentIds);
        activity.Invalidate();
        await RefreshFilesAsync();
    }

    /// <summary>
    /// What to paste into a bug report: the version, the step and the error, and how many files it happened to. No titles
    /// or paths, since a report may be public.
    /// </summary>
    [RelayCommand]
    void CopyDetails(AttentionGroup group)
    {
        var details = string.Join(Environment.NewLine,
            $"Bibliotaph {AboutInfo.ForThisApp().VersionText}",
            group.First.Error,
            $"Files: {group.Rows.Count:N0}");
        try
        {
            clipboard.SetText(details);
            group.Copied = true;
        }
        catch (COMException ex)
        {
            log.LogWarning(ex, "The clipboard was busy");
        }
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

    // IReviewActions: each decision goes to catalog.db and the library at once; the sidebar count follows.

    public void Decided(int change)
    {
        Remaining = Math.Max(0, Remaining + change);
        CountChoices();
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
        var cards = _fieldCards.Values.Where(c => !c.IsDone && c.Item.Issue.GroupKey == key).ToList();
        var unshown = _items.Where(i => i.Issue.GroupKey == key && !_fieldCards.ContainsKey(i)).ToList();
        var group = cards.Select(c => c.Item).Concat(unshown).ToList();
        var undo = await review.AcceptAllAsync(group);

        // Cards not shown yet are decided, so they won't be shown.
        var decided = unshown.ToHashSet(ReferenceEqualityComparer.Instance);
        _items = [.. _items.Where(i => !decided.Contains(i))];
        Hidden = _items.Count(i => IsShown(FieldKey(i))) - _shown;
        Remaining = Math.Max(0, Remaining - unshown.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            var snapshot = undo[i];
            cards[i].MarkDone($"{cards[i].FieldLabel} is now {cards[i].SuggestedText}.", () => review.UndoAsync([snapshot]));
        }
        CountChoices();
        // Undo all lists the cards again, since some of them weren't shown.
        BulkUndo.Offer(group.Count == 1 ? "Accepted one suggestion." : $"Accepted {group.Count:N0} suggestions.", async () =>
        {
            await review.UndoAsync(undo);
            activity.Invalidate();
            await ReloadAsync();
        });
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

    public Task<IReadOnlyList<string?>> GetPageLabelsAsync(long documentId) => Task.Run(() => parts.GetPageLabelsAsync(documentId));

    public async Task<(string? Problem, Func<Task>? Undo)> SetPartPagesAsync(UnplacedPart part, PartPages pages)
    {
        var moved = await Task.Run(() => parts.SetPagesAsync(part.EntryId, part.DocumentId, pages));
        activity.Invalidate();
        if (moved.Undo is not { } undo) return (moved.Problem, null);
        async Task UndoAsync()
        {
            await Task.Run(undo);
            activity.Invalidate();
        }
        return (null, UndoAsync);
    }

    public async Task<(string? Problem, Func<Task>? Undo)> AcceptPartsAsync(PartProposalItem item, IReadOnlyList<ProposedPart> chosen)
    {
        var accepted = await Task.Run(() => proposals.AcceptAsync(item, chosen));
        activity.Invalidate();
        if (accepted.Undo is not { } undo) return (accepted.Problem, null);
        async Task UndoAsync()
        {
            await Task.Run(undo);
            activity.Invalidate();
        }
        return (null, UndoAsync);
    }

    public async Task<Func<Task>> NotSplitAsync(PartProposalItem item)
    {
        var undo = await Task.Run(() => proposals.NotSplitAsync(item));
        activity.Invalidate();
        return async () =>
        {
            await Task.Run(undo);
            activity.Invalidate();
        };
    }

    public async Task<Func<Task>> RemovePartAsync(UnplacedPart part)
    {
        var removed = await Task.Run(() => parts.RemoveAsync(part.EntryId));
        activity.Invalidate();
        return removed is null ? () => Task.CompletedTask : async () =>
        {
            await Task.Run(() => parts.RestoreAsync(removed));
            activity.Invalidate();
        };
    }

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

    public Task<Func<Task>> DismissCorrectionAsync(CorrectionItem item) => review.DismissCorrectionAsync(item);

    /// <summary>Fix it: the Library, with the book's details open for Cataloged wrong… again.</summary>
    public void FixCorrection(CorrectionItem item) => pages.ShowCorrection(item.EntryId);

    public Task<Func<Task>> AnswerRuleAsync(FolderRuleInfo rule, bool use) => review.AnswerRuleAsync(rule, use);

    /// <summary>A folder that isn't waiting any more (answered meanwhile, or emptied) is done, with nothing to undo.</summary>
    public async Task<Func<Task>> AnswerFolderAsync(FolderCollectionProposal proposal, CollectionAnswer answer) =>
        await review.AnswerFolderAsync(proposal, answer) ?? (() => Task.CompletedTask);

    public async Task<Func<Task>> AnswerSuggestionAsync(CollectionSuggestion suggestion, CollectionAnswer answer) =>
        await review.AnswerSuggestionAsync(suggestion, answer) ?? (() => Task.CompletedTask);

    /// <summary>
    /// A place on a "new version" card opens at the page suggested for it, as its session item or note would, so the
    /// reader offers Use this page there too.
    /// </summary>
    public void OpenPlace(RevisionItem item, RevisionPlace place) => readers.OpenInMainWindow(PlaceRequest(item, place));

    public Task OpenPlaceInWindowAsync(RevisionItem item, RevisionPlace place) => ShowInWindowAsync(PlaceRequest(item, place));

    static ViewerRequest PlaceRequest(RevisionItem item, RevisionPlace place)
    {
        var check = place.Check;
        var request = new ViewerRequest(check.DocumentId, item.BookTitle, check.FirstPdfPage);
        var needsLook = place.Outcome == PageCheck.NeedsLook;
        return place.Place.Kind == PlaceKind.SessionItem
            ? request with
            {
                SessionItem = new SessionItemOpen(place.Place.OwnerId, place.Place.PackTitle ?? "the binder",
                    needsLook ? SessionItemState.Changed : SessionItemState.Ready, needsLook ? PagePlaces.ChangedReason : null, check.FirstPdfPage, check.LastPdfPage),
            }
            : request with
            {
                PageNote = new PageNoteOpen(place.Place.OwnerId, needsLook ? PagePlaceState.Changed : PagePlaceState.Ready,
                    needsLook ? PagePlaces.ChangedReason : null, check.FirstPdfPage, check.LastPdfPage),
            };
    }

    public void ShowFolder(string path) => shell.ShowInFolder(path);

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

    public Task OpenDocumentInWindowAsync(long documentId, string title) => ShowInWindowAsync(new ViewerRequest(documentId, title, 0));

    public void Open(ReviewItem item) => readers.OpenInMainWindow(EvidenceRequest(item));

    public Task OpenInWindowAsync(ReviewItem item) => ShowInWindowAsync(EvidenceRequest(item));

    /// <summary>The book at the page its evidence was found on, so checking a suggestion starts where it came from.</summary>
    static ViewerRequest EvidenceRequest(ReviewItem item) =>
        new(item.DocumentId, item.Title, item.Issue.Evidence is { Pages.Count: > 0 } claim ? claim.Pages[0] : 0);

    async Task ShowInWindowAsync(ViewerRequest request) => _window = await readers.ShowInWindowAsync(_window, request);
}
