namespace Bibliotaph.Core;

/// <summary>A control the tour points at. Each client marks its own control with the same name.</summary>
public enum TourAnchor
{
    None,
    /// <summary>Home's Add a folder button, before any folder is in.</summary>
    AddFolder,
    /// <summary>On the web, Settings > Library's buttons that connect a storage, before any is.</summary>
    Storage,
    /// <summary>On the web, Settings > Library's folder picker, once a storage is connected.</summary>
    FolderPicker,
    /// <summary>The sidebar's line about what the library is doing.</summary>
    Status,
    /// <summary>The first books on Home, or the Library's cards.</summary>
    FirstBooks,
    Library,
    Search,
    NeedsReview,
    Collections,
    Binders,
    Settings,
}

/// <summary>
/// The tour comes in two chapters, so it never points at pages with nothing on them: adding the first folder, and
/// finding the way around once books are in.
/// </summary>
public enum TourChapter
{
    Folders,
    Books,
}

public enum TourClient
{
    Windows,
    Web,
}

/// <summary>
/// One callout: the controls it points at (the first found on screen is used; several make one spotlight), what it
/// says, and the primary button's text when it does more than go on to the next step.
/// </summary>
public sealed record TourStep(TourChapter Chapter, IReadOnlyList<TourAnchor> Anchors, string Title, string Text, string? Action = null);

/// <summary>
/// The first-run tour's steps, worded once for both clients so they can't drift apart. The Windows app and the web
/// draw them over their own controls.
/// </summary>
public static class Tour
{
    /// <summary>The <c>tour.stage</c> setting's value once the Folders chapter has been seen.</summary>
    public const string StageFolders = "folders";

    /// <summary>The <c>tour.stage</c> setting's value once the whole tour has been seen or skipped.</summary>
    public const string StageBooks = "books";

    public static IReadOnlyList<TourStep> Steps(TourChapter chapter, TourClient client) => chapter switch
    {
        TourChapter.Folders when client == TourClient.Windows =>
        [
            new(chapter, [TourAnchor.AddFolder], "Start with a folder",
                "Pick the top folder that holds your RPG files; everything under it is included. Your files stay exactly where they are: nothing is moved, renamed or changed.",
                Action: "Add a folder"),
            new(chapter, [TourAnchor.Status], "While your books are read",
                "Bibliotaph finds your files, reads their text and makes covers, but only while it's open. This line says what it's doing; click it for the details. Books appear in the Library as they're found, and you can search and read straight away."),
        ],
        // The web has nothing to pick a folder from until a storage is connected, so its chapter has two more steps.
        TourChapter.Folders =>
        [
            new(chapter, [TourAnchor.AddFolder], "Start with your books",
                "Bibliotaph reads the RPG files already in your cloud storage. Your files stay exactly where they are: nothing is moved, renamed or changed.",
                Action: "Add a folder"),
            new(chapter, [TourAnchor.Storage], "Connect where your books are",
                "Connect the storage that holds your RPG files: OneDrive, Dropbox, Google Drive, Box or a WebDAV server. Bibliotaph reads the folders you choose and nothing else."),
            new(chapter, [TourAnchor.FolderPicker], "Pick a folder",
                "Pick the top folder that holds your RPG files; everything under it is included. You can add more folders, and more storage, later."),
            new(chapter, [TourAnchor.Status], "While your books are read",
                "Bibliotaph finds your files, reads their text and makes covers. This line says what it's doing, and Settings > Processing has the details. Books appear in the Library as they're found, and you can search and read straight away."),
        ],
        TourChapter.Books =>
        [
            new(chapter, [TourAnchor.FirstBooks], "Your first books",
                "Click a cover for its details and everything you can do with the book. Double-click to open it where you left off, and right-click for the rest."),
            new(chapter, [TourAnchor.Library], "Every book, map and handout",
                "The Library holds everything, as covers or a list. Narrow it by system, type, level or where a book is owned, and your place is kept when you come back."),
            new(chapter, [TourAnchor.Library], "Books you own online",
                "Own books on D&D Beyond, Demiplane, Humble Bundle or DriveThruRPG? The Library's Add button imports them, so they're found and opened alongside your files, and Settings > Online libraries has every service."),
            new(chapter, [TourAnchor.Search], "Any word on any page",
                "Search titles and systems, or any word printed on any page of any book. Results list books and the pages the words were found on. Ctrl+K gets here from anywhere, and the guide under the box shows what you can search by."),
            new(chapter, [TourAnchor.NeedsReview], "Check, don't catalog",
                "Bibliotaph catalogs your books itself and asks only when it's unsure. Suggestions to check land here, each with the page it came from, and the number is how many are waiting."),
            new(chapter, [TourAnchor.Collections, TourAnchor.Binders], "For campaigns and sessions",
                "Collections gather books around a campaign or a setting. Binders hold the pages for one session, in order, ready to run."),
            new(chapter, [TourAnchor.Settings], "Folders, AI and more",
                "Add or remove folders, set up AI to fill in the catalog, follow processing and back up your work. You can take this tour again from Settings > Preferences."),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(chapter)),
    };
}
