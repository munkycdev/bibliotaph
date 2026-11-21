namespace Bibliotaph.App.ViewModels;

// What a page's view model gives the Add to collection, Add to session and Pin items of its menus; the menus themselves
// are controls that ask the nearest page for these.

public enum CollectionMenuKind
{
    /// <summary>"Add to Maps": one of the recent collections.</summary>
    Collection,

    /// <summary>"Add to a new collection…": names one, then adds to it.</summary>
    New,

    /// <summary>"Add to another collection…": picks one from all of them.</summary>
    Choose,
}

/// <summary>One of the Add to collection items in a menu.</summary>
public sealed record CollectionMenuChoice(CollectionMenuKind Kind, long? CollectionId, string Label, string? Path);

/// <summary>What an Add to collection item runs with: the menu's context (a book, the details, the page) and the item.</summary>
public sealed record CollectionRequest(object? Target, CollectionMenuChoice Choice);

/// <summary>The Add to collection items a menu shows, in order: the collection directory's, through the page.</summary>
public interface ICollectionChoices
{
    IReadOnlyList<CollectionMenuChoice> MenuChoices { get; }
}

public enum SessionMenuKind
{
    /// <summary>"Add to The midnight bell": the current session pack, or another listed one.</summary>
    Session,

    /// <summary>"Add to a new binder…": names one, then adds to it.</summary>
    New,

    /// <summary>"Add to another binder…": picks one from all of them.</summary>
    Choose,
}

/// <summary>One of the Add to session items in a menu.</summary>
public sealed record SessionMenuChoice(SessionMenuKind Kind, long? PackId, string Label);

/// <summary>What an Add to session item runs with: the menu's context (a book, the details, the page) and the item.</summary>
public sealed record SessionRequest(object? Target, SessionMenuChoice Choice);

/// <summary>The Add to session items a menu shows, in order: the session directory's, through the page.</summary>
public interface ISessionChoices
{
    IReadOnlyList<SessionMenuChoice> MenuChoices { get; }
}

/// <summary>Whether a book menu's context (a book's card) is pinned: the pin directory knows, through the page.</summary>
public interface IPinState
{
    bool IsPinned(object? context);
}
