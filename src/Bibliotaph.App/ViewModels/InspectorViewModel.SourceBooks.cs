using Bibliotaph.App.Services;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// The details' Goes with: the book a map, handout, character sheet or pack goes with, how that was known, Keep, Not
/// from this book and Change… with a picker; the books proposed for it; and, on a book, the cards that go with it.
/// </summary>
public sealed partial class InspectorViewModel
{
    SourceBooksService? _sourceBooks;
    // Each search moves this on, so an older search's answer, arriving late, is dropped.
    int _bookSearch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGoesWith), nameof(GoesWithTitle), nameof(GoesWithHow), nameof(CanKeepGoesWith), nameof(ShowsGoesWith),
        nameof(HasProposedBooks), nameof(ProposedBooks), nameof(Companions), nameof(HasCompanions), nameof(CompanionsHeading), nameof(ChangeGoesWithLabel), nameof(ShowsGoesWithIntro))]
    public partial SourceBookDetails SourceBook { get; private set; } = SourceBookDetails.None;

    /// <summary>The section shows on a card that can go with a book, or already does.</summary>
    public bool ShowsGoesWith => SourceBook.CanGoWith || SourceBook.GoesWith is not null;

    public bool HasGoesWith => SourceBook.GoesWith is not null;

    public bool ShowsGoesWithIntro => !HasGoesWith && !HasProposedBooks;

    public string GoesWithTitle => SourceBook.GoesWith?.Title ?? "";

    public string GoesWithHow => SourceBook.GoesWith?.How ?? "";

    /// <summary>Keep shows while the book was only suggested.</summary>
    public bool CanKeepGoesWith => SourceBook.GoesWith is { Confirmed: false };

    public string ChangeGoesWithLabel => HasGoesWith ? "Change…" : "Choose a book…";

    public IReadOnlyList<SourceBookChoice> ProposedBooks => SourceBook.Proposed;

    public bool HasProposedBooks => SourceBook.GoesWith is null && SourceBook.Proposed.Count > 0;

    public IReadOnlyList<CompanionView> Companions => SourceBook.Companions;

    public bool HasCompanions => SourceBook.Companions.Count > 0;

    public string CompanionsHeading => SourceBookText.CompanionsHeading(SourceBook.Companions.Count);

    /// <summary>The book picker is open.</summary>
    [ObservableProperty]
    public partial bool IsPickingBook { get; private set; }

    /// <summary>What the picker looks for, as typed.</summary>
    [ObservableProperty]
    public partial string BookQuery { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<SourceBookChoice> BookChoices { get; private set; } = [];

    /// <summary>"It goes with “Drowned Abbey” now.", beside its Undo.</summary>
    public UndoSlot SourceUndo { get; }

    async Task LoadSourceBooksAsync(SourceBooksService sourceBooks)
    {
        _sourceBooks = sourceBooks;
        await ReloadSourceBookAsync();
    }

    async Task ReloadSourceBookAsync()
    {
        if (_sourceBooks is not { } books) return;
        var entryId = Item.EntryId;
        SourceBook = await Task.Run(() => books.GetDetailsAsync(entryId));
    }

    [RelayCommand]
    async Task ChooseSourceBook()
    {
        IsPickingBook = true;
        BookQuery = "";
        await FindBooksAsync("");
    }

    [RelayCommand]
    void CancelPickingBook()
    {
        _bookSearch++;
        IsPickingBook = false;
        BookChoices = [];
    }

    partial void OnBookQueryChanged(string value)
    {
        if (IsPickingBook) FindBooksAsync(value).FireAndForget();
    }

    async Task FindBooksAsync(string text)
    {
        if (_sourceBooks is not { } books) return;
        var search = ++_bookSearch;
        // Typing waits a moment, so each key doesn't search.
        if (text.Length > 0) await Task.Delay(200);
        if (search != _bookSearch) return;
        var excluding = new[] { Item.EntryId };
        var found = await Task.Run(() => books.FindBooksAsync(text, excluding));
        if (search == _bookSearch) BookChoices = found;
    }

    /// <summary>A book from the picker, or one proposed: the card goes with it.</summary>
    [RelayCommand]
    async Task PickSourceBook(SourceBookChoice choice)
    {
        if (_sourceBooks is not { } books) return;
        CancelPickingBook();
        var entryId = Item.EntryId;
        var change = await Task.Run(() => books.LinkAsync([entryId], choice.EntryId));
        await SourceBookChangedAsync(change.Message, change);
    }

    /// <summary>Keep: the suggested book becomes the user's choice.</summary>
    [RelayCommand]
    async Task KeepSourceBook()
    {
        if (_sourceBooks is not { } books) return;
        var entryId = Item.EntryId;
        var change = await Task.Run(() => books.ConfirmAsync([entryId]));
        await SourceBookChangedAsync(change.Message, change);
    }

    /// <summary>Not from this book: the link goes, and the rules won't suggest that book again.</summary>
    [RelayCommand]
    async Task RefuseSourceBook()
    {
        if (_sourceBooks is not { } books) return;
        var entryId = Item.EntryId;
        var change = await Task.Run(() => books.UnlinkAsync([entryId], refuse: true));
        await SourceBookChangedAsync(change.Message, change);
    }

    /// <summary>On a book, one of the cards that go with it: it no longer does.</summary>
    [RelayCommand]
    async Task RemoveCompanion(CompanionView companion)
    {
        if (_sourceBooks is not { } books) return;
        var change = await Task.Run(() => books.UnlinkAsync([companion.EntryId], refuse: true));
        await SourceBookChangedAsync($"“{companion.Title}” doesn't go with this book now.", change);
    }

    [RelayCommand]
    void ShowSourceBook()
    {
        if (SourceBook.GoesWith is { } link) ShowEntryRequested?.Invoke(this, link.Book);
    }

    [RelayCommand]
    void ShowCompanion(CompanionView companion) => ShowEntryRequested?.Invoke(this, companion.EntryId);

    [RelayCommand]
    void DismissSourceMessage() => SourceUndo.Clear();

    async Task SourceBookChangedAsync(string message, SourceBookChange change)
    {
        var books = _sourceBooks!;
        var undo = change.Undo;
        SourceUndo.Offer(message, change.Changed.Count == 0 ? null : async () =>
        {
            await Task.Run(() => books.UndoAsync(undo));
            await ReloadSourceBookAsync();
            await ReloadMetadataAsync(changed: true);
        });
        await ReloadSourceBookAsync();
        // The book's values reach the card, or leave it.
        await ReloadMetadataAsync(changed: true);
    }
}
