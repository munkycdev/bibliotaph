using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>A button on a Needs review card: what it says, its tooltip, and whether it is the card's main answer.</summary>
public sealed record ReviewChoiceText(string Key, string Label, string Hint, bool Primary = false);

/// <summary>The words on Needs review's cards beyond fields and terms, in the Windows app and on the web.</summary>
public static class ReviewCardText
{
    /// <summary>An offer card's ×, which gives the card's own "don't ask again" answer.</summary>
    public const string DismissHint = "Close. Bibliotaph won't suggest this again, and Undo brings it back.";

    public static string N(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// "Looks like a new version of" a book: which of the two files the book opens from now on, or Separate book. Either
/// file can be the newer one, so the card names both and the user picks.
/// </summary>
public sealed record VersionCardText(VersionItem Item)
{
    public const string Chip = "New version?";

    // Neither file is the likelier current one, so no choice stands out (a dark first button read as already chosen),
    // and each names its file, since "this file" and "the book's file" left which was which to guess.
    public IReadOnlyList<ReviewChoiceText> Choices =>
    [
        new("current", MakeCurrentLabel, MakeCurrentHint),
        new("copy", KeepAsCopyLabel, KeepAsCopyHint),
        new("separate", "Separate book", SeparateHint),
    ];

    public string MakeCurrentLabel => $"{ThisFile} is current";

    public string MakeCurrentHint => $"One card for both files, opening {ThisFile} from now on. Your work on both cards is kept.";

    public string KeepAsCopyLabel => $"{BookFile} is current";

    public string KeepAsCopyHint => $"One card for both files, still opening {BookFile}. {ThisFile} is kept as another version.";

    public const string SeparateHint = "Two cards, as now. Bibliotaph won't ask about these two files again.";

    PendingVersion Version => Item.Version;

    public string Heading => $"Looks like a new version of {Item.BookTitle}";

    public string Evidence => Version.Evidence switch
    {
        VersionEvidence.SharedPages =>
            $"{ReviewCardText.N(Version.SharedPages)} of {ReviewCardText.N(Version.ComparedPages)} pages are the same as the book's; the rest differ.",
        VersionEvidence.SameIsbn => "It prints the book's ISBN as its own, but its pages differ.",
        _ => "It has the book's title and publisher.",
    };

    public string Detail =>
        $"This file: {ThisFile}, {Pages(Version.PageCount)}. The book's file: {BookFile}, {Pages(Version.MatchedPageCount)}.";

    public string MadeCurrent => $"{Item.BookTitle} now opens {ThisFile}.";

    public string KeptAsCopy => $"{Item.BookTitle} still opens {BookFile}, with {ThisFile} kept as another version.";

    string ThisFile => Names.This;

    string BookFile => Names.Book;

    /// <summary>
    /// The two files often share a title, so the card tells them apart by their names, or, when those match too, by the
    /// folder each is in.
    /// </summary>
    (string This, string Book) Names
    {
        get
        {
            var (mine, book) = (FileName(Item.Path, Item.Title), FileName(Item.BookPath, Item.BookTitle));
            return string.Equals(mine, book, StringComparison.OrdinalIgnoreCase) ? (InFolder(Item.Path, mine), InFolder(Item.BookPath, book)) : (mine, book);
        }
    }

    // Split by hand: the server runs on Linux, where Path.GetFileName leaves a Windows PC's path whole.
    static string FileName(string? path, string title) => Parts(path) is { Length: >= 1 } parts ? parts[^1] : title;

    static string InFolder(string? path, string name) => Parts(path) is { Length: >= 2 } parts ? $"{parts[^2]}/{parts[^1]}" : name;

    static string[] Parts(string? path) => path?.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries) ?? [];

    public string KeptSeparate => $"Kept {Item.Title} as a separate book.";

    static string Pages(int? count) => count switch
    {
        null => "pages unknown",
        1 => "1 page",
        _ => $"{ReviewCardText.N(count.Value)} pages",
    };
}

/// <summary>"Make these 12 images one card?", with the first few images to show what they are.</summary>
public sealed record PackCardText(PackProposal Proposal)
{
    public const string Chip = "Image pack?";

    /// <summary>The images shown on the card.</summary>
    public const int Tiles = 4;

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("pack", "Make a pack", "One card for these images. Anything set on an image is kept, and Split gives them back.", true),
        new("separate", "Keep separate", "Each image keeps its own card. Bibliotaph won't ask about this folder again."),
    ];

    public string Heading => $"Make these {CardText.Images(Proposal.Images.Count)} one card?";

    public string Detail => Proposal.BesidePdfs
        ? "They sit beside PDFs, so they may be a book's handouts rather than a set of their own."
        : "A pack shows as one card, with its images a click away. Nothing on disk changes.";

    public string Packed => $"{Proposal.Name} is now one card.";

    public string KeptSeparate => $"Kept the images in {Proposal.Name} as separate cards.";
}

/// <summary>"You own X elsewhere. Is this its file?": Same book or Separate book.</summary>
public sealed record ElsewhereCardText(ElsewhereItem Item)
{
    public const string Chip = "Owned elsewhere";

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("same", "Same book", "One card: the book you own elsewhere, now opening this file. What you set on it stays.", true),
        new("separate", "Separate book", "Two cards, as now. Bibliotaph won't ask about these two again."),
    ];

    public string Heading => Item.AlsoOwn is { } owned
        ? $"You own {Item.BookTitle} elsewhere ({owned}). Is this its file?"
        : $"You own {Item.BookTitle} elsewhere. Is this its file?";

    public string Detail => $"Same book puts this file on the card for {Item.BookTitle}, keeping everything you set on it. Nothing on disk changes.";

    public string SameBook => $"{Item.BookTitle} now opens {Item.FileTitle}.";

    public string KeptSeparate => $"Kept {Item.FileTitle} as a separate book.";
}

/// <summary>
/// "RPGGeek's record for its ISBN is of X, which isn't printed in it. Is it this book?": Same book or Not this book, with
/// what the record says.
/// </summary>
public sealed record LookupMatchCardText(LookupMatchItem Item)
{
    public const string Chip = "Same book?";

    /// <summary>What the record files the book under shown on the card, at most.</summary>
    const int FiledUnder = 4;

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("same", "Same book", "Fill in what this record gives, as the catalogue's suggestions. Anything you set yourself stays.", true),
        new("different", "Not this book", "Bibliotaph won't use this record for this book."),
    ];

    BookRecord Record => Item.Match.Record;

    public string Heading => $"{Record.Source}'s record for its ISBN is of {Record.Title}, which isn't printed in it. Is it this book?";

    /// <summary>The record as the catalogue gives it: "The Owlbear Mill · Ada Quill · Gull Rock Games · 2019", then what it files the book under.</summary>
    public string Evidence
    {
        get
        {
            var parts = new List<string> { Record.Title };
            if (Record.Authors.Count > 0) parts.Add(string.Join(", ", Record.Authors));
            if (Record.Publishers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) is { } publisher) parts.Add(publisher);
            if (BookRecords.Year(Record.Published) is { } year) parts.Add(year);
            var line = string.Join(" · ", parts);
            var filed = (Record.Values ?? []).Select(v => v.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (filed.Count == 0) return line;
            var shown = string.Join("; ", filed.Take(FiledUnder)) + (filed.Count > FiledUnder ? $" and {ReviewCardText.N(filed.Count - FiledUnder)} more" : "");
            return $"{line}. Filed under {shown}.";
        }
    }

    public string Detail =>
        $"Same book fills in this book's details from the record, shown as {Record.Source}'s rather than yours, so the AI's guesses give way to them and anything you set yourself stays.";

    public string SameBook => $"Filled in {Item.BookTitle} from {Record.Source}'s record.";

    public string NotThisBook => $"Won't use {Record.Source}'s record of {Record.Title} for {Item.BookTitle}.";
}

/// <summary>"You said this book was cataloged wrong": Fix it opens its details again, It's fine now takes the card away.</summary>
public sealed record CorrectionCardText(CorrectionItem Item)
{
    public const string Chip = "Cataloged wrong";

    public const string Heading = "You said this book was cataloged wrong.";

    public const string Detail = "Its details were closed without a change. Fix it opens them again, with the values Bibliotaph guessed marked.";

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("fix", "Fix it", "Open its details, with the values Bibliotaph guessed marked", true),
        new("fine", "It's fine now", "Leave it as it is, and take this card away"),
    ];

    public string LeftAsIs => $"Left {Item.Title} as it is.";
}

/// <summary>
/// "New version of X: 12 places found their page, 2 need a look": the places that need one, each with Use this page,
/// and those found by themselves.
/// </summary>
public static class RevisionCardText
{
    public const string Chip = "New version";

    public const string FoundHeading = "Found their page, nothing to do:";

    public const string UseHint = "This place opens at the page suggested from now on";

    /// <summary>Places found by themselves listed by name; the rest are counted.</summary>
    public const int FoundShown = 8;

    /// <summary><paramref name="look"/> is how many places still need a look, which goes down as they're checked.</summary>
    public static string Heading(string bookTitle, int found, int look)
    {
        var foundText = found == 1 ? "1 place found its page" : $"{ReviewCardText.N(found)} places found their page";
        var lookText = look == 1 ? "1 needs a look" : $"{ReviewCardText.N(look)} need a look";
        return $"New version of {bookTitle}: {foundText}, {lookText}";
    }

    public static string Detail(RevisionItem item) => item.NeedLook == 1
        ? "Until it's checked, the place that needs a look opens at the same page number in this version. Open it to look, or take that page with Use this page."
        : "Until they're checked, the places that need a look open at the same page number in this version. Open one to look, or take that page with Use this page.";

    /// <summary>The places to check: those that need a look, and those already checked.</summary>
    public static IReadOnlyList<RevisionPlace> Rows(RevisionItem item) => [.. item.Places.Where(p => p.Outcome != PageCheck.Found)];

    public static IReadOnlyList<string> Found(RevisionItem item) =>
        [.. item.Places.Where(p => p.Outcome == PageCheck.Found).Take(FoundShown).Select(Describe)];

    public static string FoundMore(RevisionItem item) => item.Found > FoundShown ? $"and {ReviewCardText.N(item.Found - FoundShown)} more" : "";

    public static string AllChecked(string bookTitle) => $"Every place in the new version of {bookTitle} is checked.";

    /// <summary>"Encounter map in Session 4", "Note: the lever opens the vault", as the card lists a place.</summary>
    public static string Describe(RevisionPlace place)
    {
        var placed = place.Place;
        if (placed.Kind == PlaceKind.PageNote)
        {
            var text = (placed.Text ?? "").ReplaceLineEndings(" ").Trim();
            return $"Note: {(text.Length > 60 ? text[..59].TrimEnd() + "…" : text)}";
        }
        var label = string.IsNullOrWhiteSpace(placed.Text) ? DetailsText.Pages(placed.Range) : placed.Text.Trim();
        return placed.PackTitle is { } pack ? $"{label} in {pack}" : label;
    }

    /// <summary>"Was p. 12 · opens at p. 12 here, to be checked", or once checked "Was p. 12 · now p. 12, checked".</summary>
    public static string Where(RevisionPlace place, bool isChecked)
    {
        var was = DetailsText.Pages(place.Place.Range);
        var here = place.PageLabel ?? (place.Check.FirstPdfPage + 1).ToString(CultureInfo.CurrentCulture);
        return isChecked ? $"Was {was} · now p. {here}, checked" : $"Was {was} · opens at p. {here} here, to be checked";
    }
}

/// <summary>How a folder rule reads, on its Needs review card and in Settings > Library.</summary>
public static class FolderRuleText
{
    public static string FolderName(FolderRuleInfo rule) =>
        rule.Folder.Name is { Length: > 0 } name ? name : Path.GetFileName(rule.RootPath.TrimEnd('\\', '/')) is { Length: > 0 } top ? top : rule.RootPath;

    /// <summary>"system Pathfinder 2e", "types Adventure": the field in lower case, then the value as it reads.</summary>
    public static string Describe(FolderRuleInfo rule, Vocabulary vocabulary) => MetadataFields.Find(rule.Field) is { } field
        ? $"{field.Label.ToLower(CultureInfo.CurrentCulture)} {FieldText.Display(field, rule.Value, vocabulary)}"
        : $"{rule.Field} {rule.Value}";

    public static string FullPath(FolderRuleInfo rule) => rule.Folder.Folder.Length == 0 ? rule.RootPath : Path.Combine(rule.RootPath, rule.Folder.Folder);
}

/// <summary>A proposed folder rule: Use this rule, or No thanks.</summary>
public sealed record FolderRuleCardText(FolderRuleInfo Rule, Vocabulary Vocabulary)
{
    public const string Chip = "Folder rule?";

    public const string Detail = "Files in this folder and its subfolders, new ones too, get it as a suggestion. Anything you set on a book yourself stays. "
        + "Settings > Library can switch the rule off.";

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("use", "Use this rule", "Suggest this for every file in the folder, new ones too", true),
        new("no", "No thanks", "No rule. Bibliotaph won't suggest this one again."),
    ];

    public string FolderName => FolderRuleText.FolderName(Rule);

    public string Title => $"A rule for “{FolderName}”";

    public string Heading => $"You set {FolderRuleText.Describe(Rule, Vocabulary)} on {ReviewCardText.N(Rule.Books)} books in this folder. "
        + "Suggest it for every file in it?";

    public string Used => $"Files in “{FolderName}” now get {FolderRuleText.Describe(Rule, Vocabulary)} as a suggestion.";

    public string NoRule => $"No rule for “{FolderName}”. Bibliotaph won't suggest this one again.";
}

/// <summary>"Make a collection “X” from these 12 books?": Accept, Not now or Never.</summary>
public sealed record FolderCollectionCardText(FolderCollectionProposal Proposal)
{
    public const string Chip = "Collection?";

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("accept", "Accept", "Make the collection, with this folder's books", true),
        new("notnow", "Not now", "Ask again the next time this list is made"),
        new("never", "Never", "No collection. Bibliotaph won't ask about this folder again."),
    ];

    string Books => ReviewCardText.N(Proposal.Books.Count);

    public string Heading => $"Make a collection “{Proposal.Name}” from these {Books} books?";

    public string Detail => "Books that land in this folder later join it too, until you add or remove a book by hand."
        + (Proposal.ParentName is { } parent ? $" It goes inside “{parent}”, made from the folder above." : "");

    public string Accepted => $"Made the collection “{Proposal.Name}” with {Books} books.";

    public string NotNow => $"“{Proposal.Name}” can wait. It's asked about again the next time this list is made.";

    public string Never => $"No collection from “{Proposal.Name}”. Bibliotaph won't ask about this folder again.";
}

/// <summary>"Add these 4 books to “X”?", because they share what most of its books share: Accept, Not now or Never.</summary>
public sealed record CollectionSuggestionCardText(CollectionSuggestion Suggestion, Vocabulary Vocabulary)
{
    public const string Chip = "Add to collection?";

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("accept", "Accept", "Add these books to the collection", true),
        new("notnow", "Not now", "Ask again the next time this list is made"),
        new("never", "Never", "Bibliotaph won't suggest these books for this collection again"),
    ];

    int Count => Suggestion.Books.Count;

    string What => Count == 1 ? "this book" : $"these {ReviewCardText.N(Count)} books";

    string Added => Count == 1 ? "1 book" : $"{ReviewCardText.N(Count)} books";

    public string Heading => $"Add {What} to “{Suggestion.Name}”?";

    /// <summary>What most of the collection's books share, which these share too.</summary>
    public string Detail
    {
        get
        {
            var reasons = new List<string>();
            if (Suggestion.Series is { } series) reasons.Add($"are in the series “{series}”");
            if (Suggestion.Setting is { } setting && Suggestion.System is { } system)
                reasons.Add($"are set in {Vocabulary.Label(MetadataFields.Settings.Vocabulary!, setting)} for {Vocabulary.Label(MetadataFields.System.Vocabulary!, system)}");
            return $"Most of the books in this collection {string.Join(", or ", reasons)}, and so {(Count == 1 ? "is this one" : "are these")}.";
        }
    }

    public string Accepted => $"Added {Added} to “{Suggestion.Name}”.";

    public string NotNow => $"“{Suggestion.Name}” can wait. It's asked about again the next time this list is made.";

    public string Never => $"Bibliotaph won't suggest {(Count == 1 ? "this book" : "these books")} for “{Suggestion.Name}” again.";
}

/// <summary>
/// "This file looks like it holds 5 adventures": where the book says it is a collection, and the adventures proposed,
/// each ticked, with its title and pages as the file numbers them.
/// </summary>
public sealed record PartProposalCardText(PartProposalItem Item, IReadOnlyList<string?> Labels)
{
    public const string AcceptHint = "Give each ticked adventure a card of its own. The file isn't changed.";

    public const string EditHint = "Change the adventures' titles and pages";

    public const string NotSplitHint = "It's one book. Bibliotaph won't ask about this file again.";

    public string Heading => $"This file looks like it holds {CardText.Adventures(Item.Parts.Count)}.";

    public string Chip => Item.Source == PartProposalSource.Ai ? "Adventures? Read with AI" : "Adventures?";

    public string Detail
    {
        get
        {
            var says = Item.Evidence is { } evidence ? $"The book says “{evidence.Quote}” (p. {PageNumbers.Display(evidence.PdfPage, Labels)}). " : "";
            var found = Item.Source == PartProposalSource.Ai
                ? "A model read its contents and the first page of each to find them."
                : "Its bookmarks and the blurbs on their first pages show where each starts.";
            return $"{says}{found} Accept gives each ticked adventure a card of its own that opens its pages; the file isn't changed.";
        }
    }

    /// <summary>"Make 3 cards", for the ticked adventures.</summary>
    public static string AcceptLabel(int ticked) => ticked == 1 ? "Make 1 card" : $"Make {ReviewCardText.N(ticked)} cards";

    public string Accepted(int count) => $"{Item.FileTitle}: {CardText.Adventures(count)} with cards of their own.";

    public string NotSplit => $"Kept {Item.FileTitle} as one book.";

    public static string Unreadable(string title) => $"The pages of {(title.Trim().Length > 0 ? title.Trim() : "an adventure")} don't read as pages of this file.";

    /// <summary>A proposed adventure's first and last pages as they're typed: the last is empty for one page.</summary>
    public (string From, string To) Typed(ProposedPart part) =>
        (PageNumbers.Display(part.Pages.First, Labels), part.Pages.Count == 1 ? "" : PageNumbers.Display(part.Pages.Last, Labels));

    /// <summary>"Levels 1–3", from its blurb; empty when it gave none.</summary>
    public static string Levels(ProposedPart part) => part.Levels is { } levels && LevelRange.TryParse(levels, out var range) ? range.Describe() : "";

    /// <summary>"4–6 players", from its blurb; empty when it gave none.</summary>
    public static string Players(ProposedPart part) => PartRules.Party(part.Quote)?.Describe() ?? "";

    /// <summary>The adventure as typed, or null when its title is empty or its pages don't read as the file's.</summary>
    public ProposedPart? Read(ProposedPart part, string title, string from, string to)
    {
        int? Find(string text) => PageNumbers.Find(text, Labels, Labels.Count);
        if (title.Trim().Length == 0 || Find(from) is not { } first) return null;
        var last = to.Trim().Length == 0 ? first : Find(to);
        return last is { } l ? part with { Title = title.Trim(), Pages = new PartPages(first, l) } : null;
    }
}

/// <summary>An adventure whose pages weren't found in the file it is in as that file is now: Set its pages, or Remove adventure.</summary>
public sealed record UnplacedPartCardText(UnplacedPart Part)
{
    public const string Chip = "Adventure in a file";

    public const string Detail = "It may be a new version of the file, with pages added or moved. Set its pages to say where it is now, or remove the adventure.";

    public static IReadOnlyList<ReviewChoiceText> Choices { get; } =
    [
        new("pages", "Set its pages", "Type its first and last pages as the file numbers them", true),
        new("remove", "Remove adventure", "Its card goes; its pages are only the file's again. Undo brings it back."),
    ];

    public string Heading => $"The pages of {Part.Title} weren't found in the file {Part.FileTitle} opens now.";

    public string EditorHeading => $"The pages of {Part.Title}";

    public string Moved(PartPages pages, IReadOnlyList<string?> labels) => $"{Part.Title} is on {pages.Describe(labels)} now.";

    public string Removed => $"Removed the adventure {Part.Title}. Its pages are only the file's again.";

    /// <summary>Where it was, to start typing from, while the file still has those pages.</summary>
    public (string From, string To) Start(IReadOnlyList<string?> labels) => Part.WasPages is { } p && p.Last < labels.Count
        ? (PageNumbers.Display(p.First, labels), p.Count == 1 ? "" : PageNumbers.Display(p.Last, labels))
        : ("", "");
}
