namespace Bibliotaph.App.Services;

/// <summary>One key (or key and mouse) gesture and what it does.</summary>
public sealed record KeyboardShortcut(string Keys, string Action);

/// <summary>The shortcuts that work in one part of the app.</summary>
public sealed record KeyboardShortcutArea(string Name, IReadOnlyList<KeyboardShortcut> Shortcuts);

/// <summary>
/// Every keyboard shortcut in the app, by where it works, for the Keyboard shortcuts popup (Ctrl+/). This is a list
/// written by hand from the views' InputBindings, CommandBindings and key handlers, so a new shortcut belongs here
/// too. Tab, the arrow keys, Space and the Menu key work as they do everywhere in Windows and aren't listed.
/// </summary>
public static class KeyboardShortcuts
{
    public static IReadOnlyList<KeyboardShortcutArea> All { get; } =
    [
        new("Everywhere",
        [
            new("Ctrl+K", "Search the library"),
            new("Ctrl+/", "Show these keyboard shortcuts"),
            new("Alt+Left, or the mouse's back button", "Go back"),
            new("Alt+Space", "The window menu, with About Bibliotaph"),
        ]),
        new("Search box",
        [
            new("Enter", "Search now"),
            new("Esc", "Close the search guide, then clear the search"),
            new("Ctrl+Space", "Show the search guide"),
            new("Down, Up", "Open the search guide and move through it"),
            new("Enter or Tab", "Use the highlighted suggestion"),
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
            new("Esc", "In the find box, clear find; on the page, clear the selection"),
            new("Enter", "In the page box, go to that page"),
            new("Ctrl+Home, Ctrl+End", "The first or last page"),
            new("Ctrl+Plus, Ctrl+Minus, or Ctrl+mouse wheel", "Zoom in or out"),
            new("Ctrl+0", "Fit the page to the width again"),
            new("Ctrl+C", "Copy the selected text"),
            new("Ctrl+A", "Select the text on the page"),
            new("Left, Right", "The previous or next image in a pack"),
        ]),
        new("Pop-out reader",
        [
            new("Ctrl+W", "Close the window"),
            new("Esc", "Clear find"),
            new("Ctrl+K", "Search in the main window"),
        ]),
        new("A session's page",
        [
            new("Alt+Up, Alt+Down", "Move the item in focus up or down"),
            new("Enter", "Open the item in focus; in its label or note, save it"),
            new("Shift+Enter", "Open the item in focus in a new window"),
            new("Delete", "Remove the item in focus"),
        ]),
        new("Run mode",
        [
            new("Ctrl+Right, Ctrl+Left", "The next or previous item"),
            new("F11", "Full screen, or back"),
            new("Esc", "Leave full screen, then end the session"),
        ]),
        new("Dialogs and fields",
        [
            new("Enter", "Save, in a dialog's or a field's text box"),
            new("Ctrl+Enter", "Save a page note"),
            new("Esc", "Cancel"),
        ]),
    ];
}
