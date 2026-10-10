using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>One file in the Same book dialog: the card it is on now, its name and pages, and whether it is the one that opens.</summary>
public sealed partial class SameBookFile(long documentId, string card, string name, string detail, string? path, Action<SameBookFile> chosen) : ObservableObject
{
    public long DocumentId { get; } = documentId;

    /// <summary>The title of the card the file is on now.</summary>
    public string Card { get; } = card;

    /// <summary>The file's name, or the card's title when it has no file any more.</summary>
    public string Name { get; } = name;

    /// <summary>"120 pages · added 3 October 2026".</summary>
    public string Detail { get; } = detail;

    public string? Path { get; } = path;

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
/// Same book (multi-select plan, choices 2 to 5): the ticked cards' files, with which one opens, before they join as
/// one book. Values the cards disagree on go to Needs review, so the dialog doesn't ask about them.
/// </summary>
public sealed partial class SameBookDialogViewModel : ObservableObject
{
    readonly CopiesService _copies;
    readonly IReadOnlyList<EntryId> _cards;

    SameBookDialogViewModel(CopiesService copies, IReadOnlyList<LibraryItemViewModel> cards, IReadOnlyList<(CopyDetails Copy, string Card)> files)
    {
        _copies = copies;
        _cards = [.. cards.Select(c => c.EntryId)];
        Files = [.. files.Select(f => ToFile(f.Copy, f.Card))];
        // The file found most recently opens unless the user picks another: usually the newer version (choice 4).
        var newest = files.MaxBy(f => (f.Copy.Copy.AddedUtc, f.Copy.Copy.DocumentId)).Copy.Copy.DocumentId;
        Files.First(f => f.DocumentId == newest).Opens = true;
        var count = cards.Count.ToString("N0", CultureInfo.CurrentCulture);
        Heading = $"Join {count} cards as one book";
        Elsewhere = cards.FirstOrDefault(c => c.IsElsewhere) is { } elsewhere
            ? $"Their files join {elsewhere.Title}, the book you own elsewhere, which keeps what you've set on it."
            : null;
    }

    public static async Task<SameBookDialogViewModel> LoadAsync(CopiesService copies, IReadOnlyList<LibraryItemViewModel> cards)
    {
        var files = new List<(CopyDetails, string)>();
        foreach (var card in cards.Where(c => !c.IsElsewhere))
            foreach (var copy in await Task.Run(() => copies.GetAsync(card.EntryId)))
                files.Add((copy, card.Title));
        return new SameBookDialogViewModel(copies, cards, files);
    }

    /// <summary>Raised when the dialog closes: with what Same book did, or null when it was cancelled.</summary>
    public event EventHandler<SameBookResult?>? Closed;

    public string Heading { get; }

    /// <summary>Said when a book owned elsewhere is among the cards; null otherwise.</summary>
    public string? Elsewhere { get; }

    public IReadOnlyList<SameBookFile> Files { get; }

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
                Problem = "These cards can't be joined any more: one of them has changed. Close this and tick them again.";
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

    SameBookFile ToFile(CopyDetails details, string card)
    {
        var copy = details.Copy;
        var path = details.Locations.FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
        List<string> parts = [];
        if (copy.PageCount is { } pages) parts.Add($"{pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")}");
        parts.Add($"added {copy.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}");
        if (path is null) parts.Add("no file");
        return new SameBookFile(copy.DocumentId, card, path is null ? card : System.IO.Path.GetFileName(path), string.Join(" · ", parts), path, Chosen);
    }

    /// <summary>One file opens: choosing one unchooses the rest.</summary>
    void Chosen(SameBookFile file)
    {
        foreach (var other in Files)
            if (!ReferenceEquals(other, file)) other.Opens = false;
    }
}
