using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// What is being dragged in Organize mode: the cards, and the mode they came from, which every drop target asks, with
/// what letting go over the target under the pointer would do, for the words beside it.
/// </summary>
public sealed partial class OrganizeDrag(LibrarySelectionViewModel organize, IReadOnlyList<LibraryItemViewModel> cards) : ObservableObject
{
    [ObservableProperty]
    public partial DropPlan? Plan { get; set; }

    public LibrarySelectionViewModel Organize { get; } = organize;

    public IReadOnlyList<LibraryItemViewModel> Cards { get; } = cards;

    public IReadOnlyList<OrganizeCard> Facts { get; } = [.. cards.Select(c => c.Organize)];

    public IReadOnlyList<EntryId> EntryIds => [.. Cards.Select(c => c.EntryId).Distinct()];

    /// <summary>"3 cards", beside the pointer.</summary>
    public string Count => OrganizeRules.Dragging(EntryIds.Count);
}

/// <summary>Books dropped on a book: the menu at the pointer asks whether they go with it or are the same book.</summary>
public sealed record DropChoice(OrganizeDrag Drag, LibraryItemViewModel Target)
{
    public string GoesWithLabel => OrganizeRules.GoesWith(Target.Organize.Title);

    public string SameBookLabel => $"Same book as “{Target.Organize.Title}”…";
}

/// <summary>
/// Organize mode's drags: what dropping cards on a card, a book in the Books panel, a pinned collection or binder, or
/// "Not with a book" does; the bar's choice of cards and its Keep these; the Books panel; and Ctrl+Z, which takes back
/// the changes made in Organize mode one at a time, newest first.
/// </summary>
public sealed partial class LibrarySelectionViewModel
{
    // The changes made since Organize mode started, newest last, each undone at most once.
    readonly List<OrganizeUndo> _history = [];

    sealed class OrganizeUndo(string message, Func<Task> undo, string failed)
    {
        public string Message { get; } = message;

        public string Failed { get; } = failed;

        public bool Done { get; private set; }

        public async Task RunAsync()
        {
            if (Done) return;
            Done = true;
            await undo();
        }
    }

    /// <summary>The Books panel, beside the covers while Organize mode shows it.</summary>
    public OrganizePanelViewModel Panel { get; private set; } = default!;

    /// <summary>Organize's choice of cards: every card, the loose maps and handouts, or those only suggested for a book.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSuggested))]
    public partial LinkFilter Links { get; set; }

    partial void OnLinksChanged(LinkFilter value) => _page.RefreshAsync().FireAndForget();

    public bool ShowsSuggested => Links == LinkFilter.Suggested;

    public IReadOnlyList<Choice<LinkFilter>> LinkChoices { get; } =
        [new(LinkFilter.All, "Everything"), new(LinkFilter.Loose, "Loose maps and handouts"), new(LinkFilter.Suggested, "Suggested links")];

    public Choice<LinkFilter> LinkChoice
    {
        get => LinkChoices.First(c => c.Value == Links);
        set
        {
            if (value is not null) Links = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>"Keep these 12", while Organize shows the suggested links.</summary>
    public string KeepLabel => $"Keep these {_page.Shown.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)}";

    /// <summary>The menu that asks what books dropped on a book are, while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingDrop))]
    public partial DropChoice? DropChoice { get; private set; }

    /// <summary>The drop menu is open. A click elsewhere or Esc closes it, and nothing is done.</summary>
    public bool IsChoosingDrop
    {
        get => DropChoice is not null;
        set
        {
            if (!value) DropChoice = null;
        }
    }

    void StartOrganizing()
    {
        _history.Clear();
        Panel.Near = null;
    }

    void StopOrganizing()
    {
        _history.Clear();
        DropChoice = null;
        Panel.IsOpen = false;
        if (Links != LinkFilter.All) Links = LinkFilter.All;
    }

    /// <summary>The page's results changed, so Keep these counts what it shows.</summary>
    public void ShownChanged() => OnPropertyChanged(nameof(KeepLabel));

    /// <summary>A card picked up: a ticked one carries every ticked card with it, any other only itself.</summary>
    public OrganizeDrag? Pick(LibraryItemViewModel card)
    {
        if (!Books.IsActive) return null;
        Panel.Near = card.EntryId;
        return new OrganizeDrag(this, Books.Items.Any(i => i.EntryId == card.EntryId) ? Books.Items : [card]);
    }

    public static DropPlan PlanOn(OrganizeDrag drag, LibraryItemViewModel target) => OrganizeRules.OnCard(drag.Facts, target.Organize);

    public static DropPlan PlanOnBook(OrganizeDrag drag, SourceBookChoice book) => OrganizeRules.OnBook(drag.Facts, book.EntryId.Value, book.Title);

    public static DropPlan PlanNotWithABook(OrganizeDrag drag) => OrganizeRules.NotWithABook(drag.Facts);

    /// <summary>A pinned collection or binder takes any cards.</summary>
    public static DropPlan PlanOnPin(PinTarget pin, string label) => pin.Kind switch
    {
        PinKind.Collection => new DropPlan(DropAction.Add, OrganizeRules.OnCollection(label)),
        PinKind.Binder => new DropPlan(DropAction.Add, OrganizeRules.OnBinder(label)),
        _ => DropPlan.Nothing(""),
    };

    /// <summary>The cards dropped on <paramref name="target"/>: what <see cref="PlanOn"/> says, or the menu that asks.</summary>
    public async Task DropOnAsync(OrganizeDrag drag, LibraryItemViewModel target)
    {
        var plan = PlanOn(drag, target);
        if (plan.Action == DropAction.GoesWithOrSameBook) DropChoice = new DropChoice(drag, target);
        else await DoAsync(plan.Action, drag, target);
    }

    [RelayCommand]
    async Task ChooseGoesWith()
    {
        if (DropChoice is not { } choice) return;
        DropChoice = null;
        await DoAsync(DropAction.GoesWith, choice.Drag, choice.Target);
    }

    [RelayCommand]
    async Task ChooseSameBook()
    {
        if (DropChoice is not { } choice) return;
        DropChoice = null;
        await DoAsync(DropAction.SameBook, choice.Drag, choice.Target);
    }

    [RelayCommand]
    void CancelDropChoice() => DropChoice = null;

    async Task DoAsync(DropAction action, OrganizeDrag drag, LibraryItemViewModel target)
    {
        switch (action)
        {
            case DropAction.GoesWith:
                await GoWithAsync(drag.EntryIds, new SourceBookChoice(target.EntryId, target.Organize.Title, target.Byline));
                break;
            case DropAction.JoinPack:
                await ChangeAsync(() => _organize.JoinPackAsync(drag.EntryIds, target.EntryId), "Joining a pack");
                break;
            case DropAction.PackTogether:
                await OpenPackDialogAsync([.. drag.Cards, target]);
                break;
            case DropAction.SameBook:
                await OpenSameBookDialogAsync([.. drag.Cards, target]);
                break;
        }
    }

    /// <summary>Dropped on a book in the Books panel, or its Goes with: the cards go with it.</summary>
    public Task DropOnBookAsync(OrganizeDrag drag, SourceBookChoice book) =>
        PlanOnBook(drag, book).CanDrop ? GoWithAsync(drag.EntryIds, book) : Task.CompletedTask;

    /// <summary>Dropped on "Not with a book": the cards that go with a book no longer do.</summary>
    public Task DropNotWithABookAsync(OrganizeDrag drag) =>
        PlanNotWithABook(drag).CanDrop ? ChangeAsync(() => _organize.NotWithABookAsync(drag.EntryIds), "Taking cards from their books") : Task.CompletedTask;

    /// <summary>Dropped on a pinned collection or binder in the sidebar: the cards are added, and unticked once they are in.</summary>
    public void DropOnPin(OrganizeDrag drag, PinTarget pin, string label)
    {
        if (pin.Id is not { } id) return;
        var what = drag.Cards.Count == 1 ? drag.Cards[0].Title : SelectionRules.Books(drag.EntryIds.Count);
        if (pin.Kind == PinKind.Collection) _page.AddToCollection(drag.EntryIds, what, id, label, Books.Clear);
        else if (pin.Kind == PinKind.Binder) _page.AddToBinder(drag.EntryIds, what, id, label, Books.Clear);
    }

    /// <summary>Keep these: every card shown, whose book was only suggested, keeps it.</summary>
    [RelayCommand]
    Task KeepShown() => ChangeAsync(() => _organize.KeepAsync([.. _page.Shown.Select(i => i.EntryId).Distinct()]), "Keeping suggested books");

    async Task GoWithAsync(IReadOnlyList<EntryId> cards, SourceBookChoice book)
    {
        await ChangeAsync(() => _organize.GoWithAsync(cards, book.EntryId), "Linking cards to a book");
        Panel.Dropped(book);
    }

    /// <summary>A change from a drop or the bar: its note and Undo under the bar, no ticks, and the library as it is now.</summary>
    async Task ChangeAsync(Func<Task<OrganizeChange>> change, string what)
    {
        _page.CloseDetails();
        try
        {
            var done = await Task.Run(change);
            SayDone(done.Message, done.Undo is { } undo && _page.IsCurrent ? () => undo(CancellationToken.None) : null, OrganizeService.UndoFailed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{What} failed", what);
            Undo.Say("That didn't work. Try again, or change it in the card's details.");
        }
        Books.Clear();
        await _page.RefreshAsync();
    }

    /// <summary>Ctrl+Z in Organize mode: takes back the newest change made in it that is still standing.</summary>
    [RelayCommand]
    async Task UndoLast()
    {
        if (!Books.IsActive) return;
        var last = _history.LastOrDefault(h => !h.Done);
        if (last is null) return;
        Undo.Say("Undoing…");
        try
        {
            await Task.Run(last.RunAsync);
            var left = _history.Count(h => !h.Done);
            Undo.Say(left == 0 ? $"Undone: {last.Message}" : $"Undone: {last.Message} Ctrl+Z takes back {left} more.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Undoing an Organize change failed");
            Undo.Say(last.Failed);
        }
        finally
        {
            await _page.RefreshAsync();
        }
    }

    /// <summary>
    /// Keeps a change's undo for Ctrl+Z while Organize mode is on, and gives the note's Undo button the same one, so the
    /// change is taken back only once whichever is used.
    /// </summary>
    Func<Task> Remember(string message, Func<Task> undo, string failed)
    {
        if (!Books.IsActive) return undo;
        var entry = new OrganizeUndo(message, undo, failed);
        _history.Add(entry);
        return entry.RunAsync;
    }
}

/// <summary>
/// Organize's Books panel: books to drop maps and handouts on without scrolling to them. The books beside the card ticked
/// or picked up last, the books dropped on lately, and any found by their title; each book's Goes with gives it the
/// ticked cards, for the keyboard.
/// </summary>
public sealed partial class OrganizePanelViewModel(SourceBooksService sourceBooks, Func<IReadOnlyList<EntryId>> ticked, Func<SourceBookChoice, Task> link)
    : ObservableObject
{
    // Each search moves this on, so an older search's answer, arriving late, is dropped.
    int _search;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    partial void OnIsOpenChanged(bool value)
    {
        if (value) LoadNearAsync().FireAndForget();
    }

    /// <summary>The card whose folder's books "In this folder" shows.</summary>
    public EntryId? Near
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged(nameof(HasNear));
            OnPropertyChanged(nameof(ShowsHint));
            if (IsOpen) LoadNearAsync().FireAndForget();
            else NearBooks = [];
        }
    }

    /// <summary>A card was ticked or picked up, so "In this folder" shows, with its books or none.</summary>
    public bool HasNear => Near is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNearBooks))]
    public partial IReadOnlyList<SourceBookChoice> NearBooks { get; private set; } = [];

    public bool HasNearBooks => NearBooks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent), nameof(ShowsHint))]
    public partial IReadOnlyList<SourceBookChoice> Recent { get; private set; } = [];

    public bool HasRecent => Recent.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinding))]
    public partial string Query { get; set; } = "";

    public bool IsFinding => Query.Trim().Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoneFound))]
    public partial IReadOnlyList<SourceBookChoice> Found { get; private set; } = [];

    // Only once a search has answered, so the words don't flash while it is still looking.
    bool _answered;

    public bool NoneFound => _answered && Found.Count == 0;

    public bool ShowsHint => !HasNear && !HasRecent;

    partial void OnQueryChanged(string value) => FindAsync(value).FireAndForget();

    async Task FindAsync(string text)
    {
        var search = ++_search;
        _answered = false;
        if (text.Trim().Length == 0)
        {
            Found = [];
            return;
        }
        // Typing waits a moment, so each key doesn't search.
        await Task.Delay(200);
        if (search != _search) return;
        var excluding = Near is { } near ? new[] { near } : [];
        var found = await Task.Run(() => sourceBooks.FindBooksAsync(text, excluding));
        if (search != _search) return;
        _answered = true;
        Found = found;
    }

    async Task LoadNearAsync()
    {
        if (Near is not { } card)
        {
            NearBooks = [];
            return;
        }
        var near = await Task.Run(() => sourceBooks.FindNearAsync(card));
        if (Near == card) NearBooks = near;
    }

    /// <summary>A book dropped on goes first among those dropped on lately.</summary>
    public void Dropped(SourceBookChoice book) => Recent = [book, .. Recent.Where(r => r.EntryId != book.EntryId).Take(4)];

    /// <summary>A book's Goes with: the ticked cards go with it.</summary>
    [RelayCommand]
    Task Link(SourceBookChoice book) => ticked().Count == 0 ? Task.CompletedTask : link(book);

    [RelayCommand]
    void Close() => IsOpen = false;

    [RelayCommand]
    void Toggle() => IsOpen = !IsOpen;
}
