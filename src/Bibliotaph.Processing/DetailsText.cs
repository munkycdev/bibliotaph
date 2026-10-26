using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What a book's details say about its file, its copies and its processing, kept here so the Windows app's inspector
/// and the web client's details describe them in the same words.
/// </summary>
public static class DetailsText
{
    /// <summary>
    /// The question Reprocess with OCR on every page asks first. Plain Reprocess doesn't ask: nothing of the user's is
    /// at risk. OCR is slow enough (about a quarter of a second a page) that a long book merits a word first.
    /// </summary>
    public static string OcrEveryPageQuestion(string title, int pages) =>
        $"Bibliotaph will read all {pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")} of “{title}” with OCR, " +
        $"including pages whose text came from the PDF itself. That takes {Core.Progress.TimeLeft.Describe(OcrPerPage * pages)}.";

    /// <summary>What the question says after <see cref="OcrEveryPageQuestion"/>.</summary>
    public const string OcrEveryPageNote = "Until each page has been read, its current text stays searchable. Nothing you added to the book changes.";

    static readonly TimeSpan OcrPerPage = TimeSpan.FromMilliseconds(250);

    /// <summary>The fields shown even when nothing is known about them.</summary>
    static readonly HashSet<MetadataField> PrimaryFields =
        [MetadataFields.Title, MetadataFields.System, MetadataFields.Edition, MetadataFields.Types, MetadataFields.Levels, MetadataFields.Publisher];

    /// <summary>
    /// A field shown even before anyone knows it: the ones that matter most, where a book owned elsewhere is owned, and
    /// for an adventure who it is written for, which matters as much as its levels.
    /// </summary>
    public static bool IsPrimary(MetadataField field, bool elsewhere, bool adventure) =>
        PrimaryFields.Contains(field) || elsewhere && field == MetadataFields.AlsoOwn || adventure && field == MetadataFields.Players;

    /// <summary>An adventure, marked in a file or typed as one.</summary>
    public static bool IsAdventure(LibraryEntry entry, EffectiveMetadata metadata) =>
        entry.IsPart || metadata[MetadataFields.Types].Values.Any(v => v.Value == PartStore.AdventureType);

    /// <summary>The line over the details: "PACK", "OWNED ELSEWHERE", "ADVENTURE IN A FILE", the format, or "IMAGE".</summary>
    public static string Eyebrow(LibraryEntry entry, bool packHasPdfs) =>
        entry.IsPack ? packHasPdfs ? "PACK" : "IMAGE PACK" : entry.IsElsewhere ? "OWNED ELSEWHERE" : entry.IsPart ? "ADVENTURE IN A FILE"
        : SourceFormats.HasPages(entry.Format) ? CardText.FormatName(entry.Format) : "IMAGE";

    /// <summary>A book owned elsewhere has no file to say anything about.</summary>
    public static string FactsHeading(LibraryEntry entry) => entry.IsElsewhere ? "IN YOUR LIBRARY" : "ABOUT THE FILE";

    /// <summary>Why a chapter shows no values, or null when it has some.</summary>
    public static string? ChapterNote(ScannedChapter chapter, int lines) => chapter.Reading switch
    {
        ChapterReading.Waiting => "Not read yet.",
        ChapterReading.Failed => "The AI couldn't read this chapter.",
        _ when lines == 0 => "Nothing found.",
        _ => null,
    };

    /// <summary>What Scan again says it will do.</summary>
    public static string ScanAgain(bool scanWholeBooks) => scanWholeBooks
        ? "The AI reads this book again, chapter by chapter, when it has nothing else to do."
        : "Turn on Scan whole books in Settings > AI for the AI to read this book again.";

    /// <summary>"IMAGES (12)", or "FILES (12)" once PDFs are among them.</summary>
    public static string PackFilesHeading(int count, bool hasPdfs) => $"{(hasPdfs ? "FILES" : "IMAGES")} ({count.ToString("N0", CultureInfo.CurrentCulture)})";

    public static string CopiesHeading(int count) => $"COPIES ({count.ToString("N0", CultureInfo.CurrentCulture)})";

    public static string PartsHeading(int count) =>
        count > 0 ? $"ADVENTURES IN THIS FILE ({count.ToString("N0", CultureInfo.CurrentCulture)})" : "ADVENTURES IN THIS FILE";

    /// <summary>"p. 42", "p. 42–44", with the copy's printed page numbers where it has them, or "Whole book".</summary>
    public static string Pages(PageRange? range)
    {
        if (range is null) return "Whole book";
        var first = range.FirstLabel ?? (range.FirstPdfPage + 1).ToString(CultureInfo.CurrentCulture);
        if (range.IsSinglePage) return $"p. {first}";
        var last = range.LastLabel ?? (range.LastPdfPage + 1).ToString(CultureInfo.CurrentCulture);
        return $"p. {first}–{last}";
    }

    /// <summary>"“The Crypt” has a card of its own now."</summary>
    public static string PartAdded(string title) => $"“{title}” has a card of its own now.";

    public static string PartMoved(string title, string pages) => $"“{title}” is on {pages} now.";

    public static string PartRemoved(string title) => $"Removed “{title}”. Its pages are only the file's again.";

    /// <summary>Why an adventure's pages can't be read as typed.</summary>
    public const string PagesHint = "Type pages as the file numbers them, or PDF page numbers.";

    /// <summary>An adventure's pages as its file numbers them, or that they weren't found in the copy the file opens.</summary>
    public static string PartPages(PartPages? pages, IReadOnlyList<string?> labels) => pages?.Describe(labels) ?? "Its pages weren't found in this copy";

    /// <summary>"Change 14 other books from “Fey Court” too?", after a correction that books from the same folder could use.</summary>
    public static string LookAlikeQuestion(LookAlikeOffer offer) =>
        $"Change {offer.Books.Count.ToString("N0", CultureInfo.CurrentCulture)} other {(offer.Books.Count == 1 ? "book" : "books")} from “{offer.FolderName}” too?";

    public static string LookAlikeDetail(LookAlikeOffer offer) =>
        (offer.Books.Count == 1 ? "It" : "They") + (offer.Before.Count == 0
            ? $" had no {offer.Field.Label.ToLower(CultureInfo.CurrentCulture)} either"
            : $" show the {offer.Field.Label.ToLower(CultureInfo.CurrentCulture)} this book did")
        + $", and you haven't set it on {(offer.Books.Count == 1 ? "it" : "them")}. Undo takes it all back.";

    /// <summary>What Change them did: "Changed 14 books from “Fey Court”.", or that nothing needed changing.</summary>
    public static string LookAlikesChanged(int books, string folder) => books == 0
        ? "Those books already had these values, so nothing changed."
        : $"Changed {(books == 1 ? "1 book" : $"{books.ToString("N0", CultureInfo.CurrentCulture)} books")} from “{folder}”.";

    /// <summary>"Notes", or "Notes (3)" when the book has a note of its own or notes on its pages.</summary>
    public static string NotesLabel(int count) => count > 0 ? $"Notes ({count.ToString(CultureInfo.CurrentCulture)})" : "Notes";

    public static string SplitPackLabel(bool hasPdfs) => hasPdfs ? "Split into separate files" : "Split into separate images";

    public static string SplitPackTip(bool hasPdfs) => hasPdfs
        ? "Give every file its own card again. Bibliotaph won't pack this folder again."
        : "Give every image its own card again. Bibliotaph won't pack this folder again.";

    /// <summary>
    /// A stage that reads the file is waiting or running, as after Reprocess. AI cataloguing isn't part of Reprocess and
    /// can wait for hours (AI off, a model server down), so it doesn't count; a pack's processing is its files'.
    /// </summary>
    public static bool IsReprocessing(LibraryEntry entry, DocumentDetails? details) =>
        !entry.IsPack && details?.Stages.Any(s => Pipeline.FileStages.Contains(s.Stage) && s.Status is StageStatus.Pending or StageStatus.Running) == true;

    /// <summary>"3 of its images are here", for a folder or ZIP holding some of a pack's files.</summary>
    public static string PackFolderState(PackFolder folder, bool hasPdfs) =>
        $"{folder.Files.ToString("N0", CultureInfo.CurrentCulture)} of its {(hasPdfs ? "files" : "images")} {(folder.IsArchive ? "are in this ZIP" : "are here")}";

    /// <summary>The place a pack was made from.</summary>
    public static string PackPlaceState(PackPlace place) => place.IsArchive ? "The pack's ZIP" : "The pack's folder";

    /// <summary>What Bibliotaph knows from the file: its format, when it was added, its folders, and how it's searched.</summary>
    public static IReadOnlyList<(string Label, string Value)> Facts(LibraryEntry entry, DocumentDetails? details)
    {
        if (entry.IsElsewhere)
            return [("File", "No file in your library"), ("Added", entry.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture))];
        var facts = new List<(string, string)>
        {
            ("Format", details is { WidthPx: { } w, HeightPx: { } h } && !entry.IsPack
                ? $"{CardText.Describe(entry)} · {w.ToString("N0", CultureInfo.CurrentCulture)} × {h.ToString("N0", CultureInfo.CurrentCulture)} px"
                : CardText.Describe(entry)),
            ("Added", entry.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)),
        };
        if (!string.IsNullOrEmpty(entry.FolderHint)) facts.Add(("Folders", entry.FolderHint));
        if (details is null) return facts;
        // A pack's card shows its first file: what that file says of itself isn't the pack's.
        if (entry.IsPack)
        {
            facts.Add(("Search", entry.PdfMembers > 0 ? "Found by its files' names, and by the text of its PDFs" : "Found by its name"));
            return facts;
        }

        facts.Add(("Search", Search(entry, details)));
        // From how its stages stopped, not the file's encryption flag: that is set on any PDF with a security handler,
        // most of which open without a password (only restricting copying or printing), and never on a locked one,
        // which can't be opened to read it.
        switch (entry.TextAccess)
        {
            case TextAccess.Locked:
                facts.Add(("Password", "Needs a password to open. Open it to enter it; its text becomes searchable once it's unlocked."));
                break;
            case TextAccess.Protected:
                facts.Add(("Protection", "A scheme Bibliotaph can't open, such as DRM. Its details stay editable, and Open in another app uses your default app for the file."));
                break;
        }
        // A file that forbids copying is still read for search; only copying text out stays off.
        if (entry.Format == SourceFormats.Pdf && entry.TextAccess is TextAccess.Readable or TextAccess.Forgotten)
            facts.Add(("Copying text", details.CanCopy ? "Allowed"
                : "Not allowed by the file. Search still finds its pages; copying text out of the reader stays off, as the file asks."));
        // The PDF's own information is often wrong ("Microsoft Word - final2.doc"), so it is labelled as the file's.
        if (!string.IsNullOrWhiteSpace(details.MetaTitle)) facts.Add(("Title in the file", details.MetaTitle));
        if (!string.IsNullOrWhiteSpace(details.MetaAuthor)) facts.Add(("Author in the file", details.MetaAuthor));
        if (!string.IsNullOrWhiteSpace(details.MetaSubject)) facts.Add(("Subject in the file", details.MetaSubject));
        return facts;
    }

    static string Search(LibraryEntry entry, DocumentDetails details)
    {
        if (entry.IsPack || SourceFormats.IsImage(entry.Format)) return "Found by its name";
        switch (entry.TextAccess)
        {
            case TextAccess.Locked: return "Locked: open it to unlock, and its text becomes searchable";
            case TextAccess.Protected: return "Can't be read here, so it's found by its name and details only";
            case TextAccess.Forgotten: return "Its text was forgotten. Read it again, from its card's menu, to search it";
        }
        if (!entry.Searchable) return "Its text is still being read";
        var text = details.OcrPages > 0
            ? $"Text searchable, {details.OcrPages.ToString("N0", CultureInfo.CurrentCulture)} scanned {(details.OcrPages == 1 ? "page" : "pages")} read"
            : "Text searchable";
        return details.PagesAwaitingOcr > 0
            ? $"{text}; {details.PagesAwaitingOcr.ToString("N0", CultureInfo.CurrentCulture)} scanned {(details.PagesAwaitingOcr == 1 ? "page" : "pages")} still to read"
            : text;
    }

    /// <summary>"Folder can't be reached right now", "No longer here", "Inside a ZIP"; empty while the file is just there.</summary>
    public static string LocationState(DocumentLocation location) => (location.State, location.RootAvailability) switch
    {
        (_, SourceRootAvailability.Offline) => "Folder can't be reached right now",
        (FileLocationState.Missing, _) => "No longer here",
        (FileLocationState.OnlineOnly, _) when location.ArchivePath is not null => "Inside an online-only ZIP",
        (FileLocationState.OnlineOnly, _) => "Online-only",
        _ when location.ArchivePath is not null => "Inside a ZIP",
        _ => "",
    };

    /// <summary>How far one stage of the book's processing has got: "Done", "Waiting", "Working on it…", or why it stopped.</summary>
    public static string Progress(StageState stage) => stage.Status switch
    {
        StageStatus.Complete => "Done",
        StageStatus.Partial => stage.Reason is null ? "Partly done" : $"Partly done: {stage.Reason}",
        StageStatus.Skipped => stage.Stage is Stage.Classify or Stage.ChapterScan && stage.Reason is { } why ? why : "Not needed",
        // A whole-book scan waits a long time, and says how far it has got or what it waits for.
        StageStatus.Pending when stage.Stage == Stage.ChapterScan && stage.Reason is { } waiting => waiting,
        StageStatus.Pending => "Waiting",
        StageStatus.Running => "Working on it…",
        _ => stage.Reason ?? stage.Status.ToString(),
    };

    /// <summary>"Current copy", "Copy" or "Earlier version, no file", for a book in more than one file.</summary>
    public static string CopyHeading(EntryCopy copy, bool hasFile) => copy.IsCurrent ? "Current copy" : hasFile ? "Copy" : "Earlier version, no file";

    /// <summary>"96 pages · added 3 October 2026 · joined from a card of its own".</summary>
    public static string CopyDetail(EntryCopy copy)
    {
        var parts = new List<string>();
        if (copy.PageCount is { } pages) parts.Add($"{pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")}");
        parts.Add($"added {copy.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}");
        if (copy.Joined) parts.Add("joined from a card of its own");
        if (copy.IsCurrent && !copy.IsShown) parts.Add("its file is missing, so another copy opens");
        return string.Join(" · ", parts);
    }
}
