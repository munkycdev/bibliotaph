using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// Select mode's rules and words, in the Windows app and on the web: what the Select bar says, and which of Same book,
/// Pack together and Remove adventures fit the ticked cards.
/// </summary>
public static class SelectionRules
{
    /// <summary>"12 selected", "12 selected, 3 not shown", or what to do when nothing is ticked.</summary>
    public static string Summary(int count, int notShown) => count == 0
        ? "Click books to select them. Shift+click selects a range, Ctrl+A everything shown."
        : notShown == 0
            ? $"{count.ToString("N0", CultureInfo.CurrentCulture)} selected"
            : $"{count.ToString("N0", CultureInfo.CurrentCulture)} selected, {notShown.ToString("N0", CultureInfo.CurrentCulture)} not shown";

    public static string Books(int count) => count == 1 ? "1 book" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} books";

    /// <summary>A pack's card, or one of its files' own: either way what Select mode ticks, joins or packs is the pack.</summary>
    public static bool StandsForPack(LibraryEntry card) => card.IsPack || card.IsPackFile;

    /// <summary>
    /// Same book fits two or more cards for books or single images, with at most one book owned elsewhere among them.
    /// Packs aren't offered: a pack is many files, not a copy of a book.
    /// </summary>
    public static bool CanJoin(IReadOnlyCollection<LibraryEntry> cards) =>
        cards.Count >= 2 && !cards.Any(StandsForPack) && cards.Count(c => c.IsElsewhere) <= 1;

    /// <summary>
    /// Pack together fits two or more cards for single files, images or PDFs (a party's character sheets), or packs:
    /// no books owned elsewhere, no adventures inside files, and no file with adventures marked in it, whose cards
    /// name it.
    /// </summary>
    public static bool CanPack(IReadOnlyCollection<LibraryEntry> cards) =>
        cards.Count >= 2 && cards.All(c => StandsForPack(c) || c is { IsElsewhere: false, IsPart: false, Parts: 0 }
            && (SourceFormats.IsImage(c.Format) || c.Format == SourceFormats.Pdf));

    /// <summary>Remove adventures fits ticked cards that are all adventures inside files.</summary>
    public static bool CanRemoveAdventures(IReadOnlyCollection<LibraryEntry> cards) => cards.Count > 0 && cards.All(c => c.IsPart);

    public static string Removed(int count) => $"Removed {CardText.Adventures(count)}. Their pages are only their files' again.";

    /// <summary>When removing failed after <paramref name="removed"/> of them were.</summary>
    public static string RemoveFailed(int removed) =>
        removed == 0 ? "The adventures couldn't be removed." : $"Only {CardText.Adventures(removed)} could be removed.";

    public const string RemoveUndoFailed = "The adventures couldn't all be brought back. Mark their pages again from their files' details.";

    public static string Edited(int books) => books == 0 ? "Those books already had these values, so nothing changed." : $"Edited {Books(books)}.";

    public static string Exported(int books, string file) => $"Exported {Books(books)} to {file}.";

    /// <summary>The CSV's name, offered when saving it.</summary>
    public const string ExportName = "Bibliotaph books.csv";
}

/// <summary>One file in the Same book dialog: the card it is on now, its name and pages.</summary>
public sealed record SameBookFile(long DocumentId, string Card, string Name, string Detail, string? Path);

/// <summary>
/// Same book: the ticked cards' files, with the one that opens unless another is picked, before they join as one book.
/// Values the cards disagree on go to Needs review, so it doesn't ask about them.
/// </summary>
public sealed record SameBookPlan(string Heading, string? Elsewhere, IReadOnlyList<SameBookFile> Files, long Opens)
{
    public const string Gone = "These cards can't be joined any more: one of them has changed. Close this and tick them again.";

    public const string UndoFailed = "The cards couldn't be taken apart again. Use Not the same book in the card's Copies list.";

    public static string Joined(int cards, string title) => $"Joined {cards.ToString("N0", CultureInfo.CurrentCulture)} cards as {title}.";

    public static async Task<SameBookPlan> LoadAsync(CopiesService copies, IReadOnlyList<LibraryEntry> cards, CancellationToken ct = default)
    {
        var files = new List<(CopyDetails Copy, string Card)>();
        foreach (var card in cards.Where(c => !c.IsElsewhere))
            foreach (var copy in await copies.GetAsync(card.EntryId, ct))
                files.Add((copy, card.Title));
        // The file found most recently opens unless the user picks another: usually the newer version.
        var newest = files.MaxBy(f => (f.Copy.Copy.AddedUtc, f.Copy.Copy.DocumentId)).Copy.Copy.DocumentId;
        return new SameBookPlan(
            $"Join {cards.Count.ToString("N0", CultureInfo.CurrentCulture)} cards as one book",
            cards.FirstOrDefault(c => c.IsElsewhere) is { } elsewhere
                ? $"Their files join {elsewhere.Title}, the book you own elsewhere, which keeps what you've set on it."
                : null,
            [.. files.Select(f => ToFile(f.Copy, f.Card))],
            newest);
    }

    static SameBookFile ToFile(CopyDetails details, string card)
    {
        var copy = details.Copy;
        var path = details.Locations.FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
        List<string> parts = [];
        if (copy.PageCount is { } pages) parts.Add($"{pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")}");
        parts.Add($"added {copy.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}");
        if (path is null) parts.Add("no file");
        return new SameBookFile(copy.DocumentId, card, path is null ? card : System.IO.Path.GetFileName(path), string.Join(" · ", parts), path);
    }
}

/// <summary>
/// Pack together: one dialog whether the ticked cards make a new pack, add files to a pack, or merge packs, with the
/// pack's name ready to edit. Images and PDFs alike go in, such as a party's character sheets and their portraits; with
/// a PDF among them it speaks of files rather than images.
/// </summary>
public sealed class PackTogetherPlan
{
    readonly int _files;
    readonly bool _pdfsTicked;
    readonly int _packCount;

    PackTogetherPlan(IReadOnlyList<LibraryEntry> cards, string name)
    {
        var packs = cards.Where(SelectionRules.StandsForPack).OrderBy(c => c.EntryId.Value).ToList();
        _packCount = packs.Count;
        _files = cards.Count - _packCount;
        _pdfsTicked = cards.Any(c => !SelectionRules.StandsForPack(c) && c.Format == SourceFormats.Pdf);
        var withPdfs = _pdfsTicked || packs.Any(p => p.IsPack && p.PdfMembers > 0);
        Eyebrow = withPdfs ? "PACK" : "IMAGE PACK";
        Name = name;
        var files = Count(_files);
        var split = withPdfs ? "Split into separate files" : "Split into separate images";
        (Heading, Explanation) = _packCount switch
        {
            0 => ($"Make a pack of {files}",
                $"They show as one card. A pack you make doesn't grow by itself, and {split} gives each its card back."),
            1 => ($"Add {files} to {PackName(packs[0])}", "They show inside the pack. New images in its folder still join it."),
            _ => ($"Merge {_packCount.ToString("N0", CultureInfo.CurrentCulture)} packs{(_files > 0 ? $" and {files}" : "")}",
                $"The newer packs join {PackName(packs[0])} with their {(withPdfs ? "files" : "images")}, hearts, collections, binder items and notes. New images in their folders join it too."),
        };
    }

    public const string Gone = "These cards can't be packed any more: one of them has changed. Close this and tick them again.";

    public const string UndoFailed = "The pack couldn't be put back as it was. Use Split in its details.";

    /// <summary>"IMAGE PACK", or "PACK" when PDFs go in it too.</summary>
    public string Eyebrow { get; }

    public string Heading { get; }

    public string Explanation { get; }

    /// <summary>The pack's name to start from.</summary>
    public string Name { get; }

    /// <summary>
    /// The plan for <paramref name="cards"/>, named after the oldest pack among them, or for files only after the folder
    /// or ZIP they share (the first file's when they don't).
    /// </summary>
    public static async Task<PackTogetherPlan> LoadAsync(LibraryStore library, IReadOnlyList<LibraryEntry> cards, CancellationToken ct = default)
    {
        if (cards.Where(SelectionRules.StandsForPack).MinBy(c => c.EntryId.Value) is { } oldest) return new PackTogetherPlan(cards, PackName(oldest));
        var folders = new List<string>();
        foreach (var file in cards)
            if ((await library.GetLocationsAsync(file.DocumentId, ct)).FirstOrDefault(l => l.State != FileLocationState.Missing) is { } place)
                folders.Add(place.ArchivePath is { } zip ? DisplayTitle.FromFileName(System.IO.Path.GetFileName(zip))
                    : DisplayTitle.FromFolderName(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(place.FullPath)) ?? ""));
        var name = folders.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == 1 ? folders[0] : folders.FirstOrDefault() ?? "";
        return new PackTogetherPlan(cards, name);
    }

    /// <summary>What the Library's note says once the cards are packed as <paramref name="name"/>.</summary>
    public string Done(string name) => _packCount switch
    {
        0 => $"Made a pack of {Count(_files)}: {name}.",
        1 => $"Added {Count(_files)} to {name}.",
        _ => $"Merged {_packCount.ToString("N0", CultureInfo.CurrentCulture)} packs into {name}.",
    };

    static string PackName(LibraryEntry card) => card.PackTitle ?? card.Title;

    /// <summary>"2 images", or "2 files" when a PDF is among the ticked cards.</summary>
    string Count(int count) => _pdfsTicked ? CardText.Files(count) : CardText.Images(count);
}
