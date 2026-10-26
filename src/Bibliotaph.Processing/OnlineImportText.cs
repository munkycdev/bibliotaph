using System.Globalization;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// What Online libraries says about an import, in the Windows app and on the web alike: the preview's summary, its
/// groups and rows, the Import button and what an import did.
/// </summary>
public static class OnlineImportText
{
    public const string Unreadable = "That page isn't a library page Bibliotaph can read. Open your library on the service, sign in if asked, "
        + "and save the page with Save as, choosing Webpage, complete.";

    public const string MissingHeading = "No longer in this library";

    public const string MissingExplanation = "Books imported before that this page doesn't list. Nothing is removed unless you remove it.";

    /// <summary>"Your D&amp;D Beyond library: 30 books. 2 books shared with you were left out."</summary>
    public static string Summary(IOnlineLibraryReader reader, int books, int shared)
    {
        var left = shared switch
        {
            0 => "",
            1 => " 1 book shared with you was left out.",
            var n => $" {Count(n, "book")} shared with you were left out.",
        };
        return $"Your {reader.Name} library: {Count(books, "book")}.{left}";
    }

    public static string GroupLabel(OnlineOutcome outcome) => outcome switch
    {
        OnlineOutcome.New => "New",
        OnlineOutcome.AddToCard => "Already in your library",
        OnlineOutcome.Update => "Imported before, with more to fill in",
        _ => "Imported before",
    };

    public static string GroupExplanation(OnlineOutcome outcome, IOnlineLibraryReader reader) => outcome switch
    {
        OnlineOutcome.New => reader.LinkField == MetadataFields.AlsoOwn
            ? $"Each becomes a book owned elsewhere, opening on {reader.Name}."
            : $"Each becomes a book to get from {reader.Name}, until you download its file.",
        OnlineOutcome.AddToCard => $"A card with its title is here already: it gets {reader.Name} and the link, rather than a second card.",
        OnlineOutcome.Update => "Only fields that are empty are filled. Nothing you have set changes.",
        _ => "Nothing to change.",
    };

    /// <summary>"New · 12".</summary>
    public static string GroupHeading(OnlineOutcome outcome, int count) => $"{GroupLabel(outcome)} · {count.ToString("N0", CultureInfo.CurrentCulture)}";

    /// <summary>"Gilded Press · adventure · 5e · 2019", "Goes on your card The Ember Vault.", "Fills Edition, Type."</summary>
    public static string Detail(OnlineImportRow row) => row.Outcome switch
    {
        OnlineOutcome.AddToCard when row.CardTitle != row.Book.Title => $"Goes on your card {row.CardTitle}.",
        OnlineOutcome.AddToCard => "Goes on its card.",
        OnlineOutcome.Update => $"Fills {string.Join(", ", row.Fills.Select(f => f.Label))}.",
        _ => string.Join(" · ", new object?[] { row.Book.Publisher, row.Book.Type, row.Book.Edition, row.Book.Year }.OfType<object>()),
    };

    /// <summary>"Import 12 books", for the books ticked.</summary>
    public static string ImportLabel(int chosen) => chosen switch
    {
        0 => "Import",
        1 => "Import 1 book",
        var n => $"Import {n.ToString("N0", CultureInfo.CurrentCulture)} books",
    };

    /// <summary>What an import did, beside its Undo.</summary>
    public static string Imported(IOnlineLibraryReader reader, int added, int changed) => (added, changed) switch
    {
        (0, 0) => "Nothing needed changing.",
        (_, 0) => $"Added {Count(added, "book")} owned on {reader.Name}.",
        (0, _) => $"Updated {Count(changed, "card")} already in your library.",
        _ => $"Added {Count(added, "book")} owned on {reader.Name} and updated {Count(changed, "card")} already in your library.",
    };

    public static string Removed(string title) => $"Removed {title} from your library.";

    static string Count(int n, string noun) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(n == 1 ? "" : "s")}";
}
