using System.Globalization;

namespace Bibliotaph.Core;

/// <summary>
/// What Organize mode needs to know about a card to say what dropping cards on it does, the same in the Windows app
/// and on the web. <see cref="StandsForPack"/> is a pack's card or one of its files' own, which drags and drops as the
/// pack; <see cref="Loose"/> is something that goes with a book rather than being one: a picture, a video, a track, a
/// pack, or a file typed as a map pack, handout or character sheet.
/// </summary>
public sealed record OrganizeCard(
    long EntryId, string Title, string Format, bool StandsForPack = false, bool Elsewhere = false, bool Part = false, int Parts = 0,
    bool Loose = false, bool GoesWithBook = false, int Companions = 0)
{
    public bool IsPicture => !StandsForPack && SourceFormats.IsPicture(Format);

    public bool IsAudio => !StandsForPack && SourceFormats.IsAudio(Format);

    /// <summary>A single file with no pages, which packs with others like it and is never a book: a picture or a track.</summary>
    public bool IsMedia => IsPicture || IsAudio;

    /// <summary>A single file that Pack together takes: an image, a video, a track or a PDF.</summary>
    public bool Packable => StandsForPack || (!Elsewhere && !Part && Parts == 0 && (SourceFormats.IsPackable(Format) || Format == SourceFormats.Pdf));

    /// <summary>The types that say a file goes with a book rather than being one.</summary>
    public static IReadOnlyList<string> LooseTypes { get; } = ["map-pack", "handout", "character-sheet"];
}

/// <summary>What dropping cards on a card does.</summary>
public enum DropAction
{
    /// <summary>Nothing: the label says why.</summary>
    None,

    /// <summary>The dragged cards go with the book.</summary>
    GoesWith,

    /// <summary>The dragged files join the pack dropped on, which keeps its name.</summary>
    JoinPack,

    /// <summary>The dragged pictures and the one dropped on become a new pack, named in the Pack together dialog.</summary>
    PackTogether,

    /// <summary>The dragged books and the one dropped on are one book: the Same book dialog says how first.</summary>
    SameBook,

    /// <summary>Books dropped on a book: goes with it, or the same book; a menu asks which.</summary>
    GoesWithOrSameBook,

    /// <summary>Dropped on the bar's "Not with a book": the cards no longer go with the book they did.</summary>
    NotWithABook,

    /// <summary>Dropped on a collection or a binder in the sidebar: the cards are added to it.</summary>
    Add,
}

/// <summary>What a drop will do, and the words beside the cursor that say so, or why nothing will happen.</summary>
public sealed record DropPlan(DropAction Action, string Label)
{
    public bool CanDrop => Action != DropAction.None;

    public static DropPlan Nothing(string why) => new(DropAction.None, why);
}

/// <summary>
/// Organize mode's rules for dragging cards onto other cards, the sidebar's collections and binders, the Books panel and
/// the bar's "Not with a book": what each drop does and the words for it. The stores check every change again, so a
/// card that changed meanwhile is refused there rather than here.
/// </summary>
public static class OrganizeRules
{
    /// <summary>"3 cards", beside the cursor while they are dragged.</summary>
    public static string Dragging(int count) => count == 1 ? "1 card" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} cards";

    /// <summary>
    /// Same book fits two or more cards for books or single images, with at most one book owned elsewhere among them.
    /// Packs aren't offered: a pack is many files, not a copy of a book.
    /// </summary>
    public static bool CanJoin(IReadOnlyCollection<OrganizeCard> cards) =>
        cards.Count >= 2 && !cards.Any(c => c.StandsForPack) && cards.Count(c => c.Elsewhere) <= 1;

    /// <summary>
    /// Pack together fits two or more cards for single files, images or PDFs (a party's character sheets), or packs:
    /// no books owned elsewhere, no adventures inside files, and no file with adventures marked in it, whose cards
    /// name it.
    /// </summary>
    public static bool CanPack(IReadOnlyCollection<OrganizeCard> cards) => cards.Count >= 2 && cards.All(c => c.Packable);

    /// <summary>A card that can go with a book: a whole file's or a pack's, not an adventure's, with no cards going with it.</summary>
    public static bool CanGoWithBook(OrganizeCard card) => !card.Part && !card.Elsewhere && card.Companions == 0;

    /// <summary>Why <paramref name="card"/> can't be the book others go with, or null when it can.</summary>
    public static string? WhyNotABook(OrganizeCard card) =>
        card.StandsForPack ? "A pack can't be the book. Drop them on the book itself."
        : card.IsPicture ? "A picture can't be the book. Drop them on the book itself."
        : card.IsAudio ? "A track can't be the book. Drop them on the book itself."
        : card.GoesWithBook ? $"“{card.Title}” goes with a book itself."
        : null;

    /// <summary>What dropping <paramref name="dragged"/> on <paramref name="target"/> does.</summary>
    public static DropPlan OnCard(IReadOnlyList<OrganizeCard> dragged, OrganizeCard target)
    {
        if (dragged.Count == 0 || dragged.Any(d => d.EntryId == target.EntryId)) return DropPlan.Nothing("");
        var together = (IReadOnlyCollection<OrganizeCard>)[.. dragged, target];
        if (target.StandsForPack)
            return CanPack(together) ? new DropPlan(DropAction.JoinPack, $"Join “{target.Title}”")
                : DropPlan.Nothing("Only images, videos, audio, PDFs and packs can join a pack.");
        if (target.IsMedia)
            return dragged.All(d => d.IsMedia || d.StandsForPack) && CanPack(together)
                ? new DropPlan(DropAction.PackTogether, $"Pack together with “{target.Title}”")
                : DropPlan.Nothing(WhyNotABook(target)!);
        if (WhyNotABook(target) is { } notABook) return DropPlan.Nothing(notABook);
        if (dragged.FirstOrDefault(d => !CanGoWithBook(d)) is { } cannot)
            return CanJoin(together) ? new DropPlan(DropAction.SameBook, $"Same book as “{target.Title}”…") : DropPlan.Nothing(WhyCant(cannot));
        if (dragged.All(d => d.Loose) || !CanJoin(together)) return new DropPlan(DropAction.GoesWith, GoesWith(target.Title));
        return new DropPlan(DropAction.GoesWithOrSameBook, $"Goes with “{target.Title}”, or the same book…");
    }

    /// <summary>Dropping on a book in the Books panel, which only ever links.</summary>
    public static DropPlan OnBook(IReadOnlyList<OrganizeCard> dragged, long book, string title)
    {
        if (dragged.Count == 0 || dragged.Any(d => d.EntryId == book)) return DropPlan.Nothing("");
        return dragged.FirstOrDefault(d => !CanGoWithBook(d)) is { } cannot ? DropPlan.Nothing(WhyCant(cannot)) : new DropPlan(DropAction.GoesWith, GoesWith(title));
    }

    /// <summary>"Goes with “Drowned Abbey”", for a drop that links.</summary>
    public static string GoesWith(string title) => $"Goes with “{title}”";

    /// <summary>Dropping on a collection in the sidebar.</summary>
    public static string OnCollection(string name) => $"Add to “{name}”";

    /// <summary>Dropping on a binder in the sidebar.</summary>
    public static string OnBinder(string name) => $"Add to the binder “{name}”";

    /// <summary>Dropping on the bar's "Not with a book": the cards that go with a book no longer do.</summary>
    public static DropPlan NotWithABook(IReadOnlyList<OrganizeCard> dragged) =>
        dragged.Any(d => d.GoesWithBook) ? new DropPlan(DropAction.NotWithABook, "Not with a book") : DropPlan.Nothing("None of these go with a book.");

    /// <summary>The bar's place to drop cards that shouldn't go with a book.</summary>
    public const string NotWithABookZone = "Not with a book";

    static string WhyCant(OrganizeCard card) =>
        card.Elsewhere ? $"“{card.Title}” is owned elsewhere, so it can't go with a book."
        : card.Part ? $"“{card.Title}” is an adventure inside a file, so it can't go with a book."
        : $"Other cards go with “{card.Title}”, so it can't go with a book itself.";
}
