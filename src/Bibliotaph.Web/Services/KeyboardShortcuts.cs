namespace Bibliotaph.Web.Services;

/// <summary>One key (or key and mouse) gesture and what it does.</summary>
public sealed record KeyboardShortcut(string Keys, string Action);

/// <summary>The shortcuts that work in one part of the app.</summary>
public sealed record KeyboardShortcutArea(string Name, IReadOnlyList<KeyboardShortcut> Shortcuts);

/// <summary>
/// The Windows app's Keyboard shortcuts list (Ctrl+/), with only what works in the browser: written by hand from the
/// pages' key handlers and the scripts', so a new shortcut belongs here too. The Windows app's Alt+Space has no web page
/// to act on, and the browser keeps Ctrl+mouse wheel for its own zoom.
/// </summary>
public static class KeyboardShortcuts
{
    public static IReadOnlyList<KeyboardShortcutArea> All { get; } =
    [
        new("Everywhere",
        [
            new("Ctrl+K, or /", "Search the library"),
            new("Ctrl+B", "Collapse or expand the sidebar"),
            new("Ctrl+/", "Show these keyboard shortcuts"),
            new("Alt+Left, or the mouse's back button", "Go back"),
        ]),
        new("Search box",
        [
            new("Enter", "Search now"),
            new("Esc", "Close the search guide, then clear the search"),
            new("Ctrl+Space", "Show the search guide"),
            new("Down, Up", "Open the search guide and move through it"),
            new("Enter or Tab", "Use the highlighted suggestion"),
            new("Enter, in Ask", "Read what you typed as a search"),
            new("Ctrl+Enter, in Ask", "Answer it from your library's pages"),
            new("Esc, in Ask", "Leave Ask, keeping what you typed"),
        ]),
        new("Library",
        [
            new("Enter", "Open the details of the book in focus; in Select mode, tick or untick it"),
            new("Shift+Enter, or middle-click", "Open a book in a new window"),
            new("Menu key or Shift+F10", "A book's menu"),
            new("Ctrl+A", "In Select mode, select every book shown"),
            new("Shift+click", "In Select mode, select every book from the last one clicked"),
            new("Esc", "Close a dialog, the bulk editor or the details, then leave Select mode"),
        ]),
        new("Reader",
        [
            new("Ctrl+F", "Find in this book"),
            new("Enter, Shift+Enter", "In the find box, the next or previous match"),
            new("F3, Shift+F3", "The next or previous match"),
            new("Esc", "In the find box, clear find"),
            new("Enter", "In the page box, go to that page"),
            new("Ctrl+Plus, Ctrl+Minus", "Zoom in or out"),
            new("Ctrl+0", "Fit the page to the width again"),
            new("Ctrl+C", "Copy the selected text"),
            new("Left, Right", "The previous or next image in a pack"),
        ]),
        new("Pop-out reader",
        [
            new("Ctrl+W", "Close the window"),
            new("Esc", "Clear find"),
            new("Ctrl+K", "Search in the main window"),
        ]),
        new("A binder",
        [
            new("Ctrl+Right, Ctrl+Left", "The next or previous page in the binder"),
            new("F2", "Rename the page in focus in the list; Enter saves, Esc cancels"),
        ]),
        new("Editing a binder",
        [
            new("Alt+Up, Alt+Down", "Move the item in focus up or down"),
            new("Enter", "Open the item in focus"),
            new("Shift+Enter", "Open the item in focus in a new tab"),
            new("Delete", "Remove the item in focus"),
        ]),
        new("Dialogs and fields",
        [
            new("Enter", "Save, in a dialog's or a field's text box"),
            new("Ctrl+Enter", "Save a page note"),
            new("Esc", "Cancel"),
        ]),
    ];
}
