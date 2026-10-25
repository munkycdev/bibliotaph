namespace Bibliotaph.Processing;

/// <summary>How saved searches and Favorites read wherever they show, in the Windows app and on the web.</summary>
public static class SavedSearchText
{
    /// <summary>A saved search's subtitle on its page.</summary>
    public const string Subtitle = "A saved search: its search, filters and order, run again each time.";

    /// <summary>Why a saved search shows more than it was saved with.</summary>
    public const string ScopeGone = "The collection or binder this search was saved in is gone, so it looks through your whole library.";

    public const string Changed = "You've changed this saved search.";

    public const string Failed = "That didn't work. The log has the details.";

    public static string Saved(string name) => $"Saved {name}. It's pinned in the sidebar.";

    public static string Updated(string name) => $"Updated {name}.";

    public static string Deleted(string name) => $"Deleted the saved search {name}. Your books are untouched.";

    /// <summary>Save as new's name to start from.</summary>
    public static string SecondName(string name) => $"{name} (2)";

    public const string FavoritesName = "Favorites";

    public const string FavoritesSubtitle = "The books you marked with a heart.";

    public const string FavoritesEmptyTitle = "No favorites yet.";

    public const string FavoritesEmptyMessage = "Mark a book with the heart on its cover, in its details or in the reader, and it shows here.";

    /// <summary>The sidebar's Pinned group while it has none.</summary>
    public const string NoPins = "Right-click a book, collection or binder and choose Pin.";
}
