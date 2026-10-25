using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>One file in the Same book dialog: the card it is on now, its name and pages, and whether it is the one that opens.</summary>
public sealed partial class SameBookFileViewModel(SameBookFile file, Action<SameBookFileViewModel> chosen) : ObservableObject
{
    public long DocumentId => file.DocumentId;

    /// <summary>The title of the card the file is on now.</summary>
    public string Card => file.Card;

    /// <summary>The file's name, or the card's title when it has no file any more.</summary>
    public string Name => file.Name;

    /// <summary>"120 pages · added 3 October 2026".</summary>
    public string Detail => file.Detail;

    public string? Path => file.Path;

    [ObservableProperty]
    public partial bool Opens { get; set; }

    partial void OnOpensChanged(bool value)
    {
        if (value) chosen(this);
    }
}

/// <summary>What Same book did, for the Library's message and its Undo.</summary>
public sealed record SameBookResult(BookJoin Join, int Cards);

/// <summary>
/// Same book: the ticked cards' files, with which one opens, before they join as one book. Values the cards disagree
/// on go to Needs review, so the dialog doesn't ask about them.
/// </summary>
public sealed partial class SameBookDialogViewModel : ObservableObject
{
    readonly CopiesService _copies;
    readonly IReadOnlyList<EntryId> _cards;

    SameBookDialogViewModel(CopiesService copies, IReadOnlyList<LibraryItemViewModel> cards, SameBookPlan plan)
    {
        _copies = copies;
        _cards = [.. cards.Select(c => c.EntryId)];
        Files = [.. plan.Files.Select(f => new SameBookFileViewModel(f, Chosen))];
        Files.First(f => f.DocumentId == plan.Opens).Opens = true;
        Heading = plan.Heading;
        Elsewhere = plan.Elsewhere;
    }

    public static async Task<SameBookDialogViewModel> LoadAsync(CopiesService copies, IReadOnlyList<LibraryItemViewModel> cards) =>
        new(copies, cards, await Task.Run(() => SameBookPlan.LoadAsync(copies, [.. cards.Select(c => c.Entry)])));

    /// <summary>Raised when the dialog closes: with what Same book did, or null when it was cancelled.</summary>
    public event EventHandler<SameBookResult?>? Closed;

    public string Heading { get; }

    /// <summary>Said when a book owned elsewhere is among the cards; null otherwise.</summary>
    public string? Elsewhere { get; }

    public IReadOnlyList<SameBookFileViewModel> Files { get; }

    /// <summary>Why the cards couldn't be joined, such as one having changed since they were ticked.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    public partial bool IsJoining { get; private set; }

    bool CanJoin => !IsJoining;

    [RelayCommand(CanExecute = nameof(CanJoin))]
    async Task Join()
    {
        IsJoining = true;
        Problem = null;
        try
        {
            var opens = Files.First(f => f.Opens);
            if (await Task.Run(() => _copies.JoinAsOneBookAsync(_cards, opens.DocumentId)) is not { } join)
            {
                Problem = SameBookPlan.Gone;
                return;
            }
            Closed?.Invoke(this, new SameBookResult(join, _cards.Count));
        }
        finally
        {
            IsJoining = false;
        }
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);

    /// <summary>One file opens: choosing one unchooses the rest.</summary>
    void Chosen(SameBookFileViewModel file)
    {
        foreach (var other in Files)
            if (!ReferenceEquals(other, file)) other.Opens = false;
    }
}
