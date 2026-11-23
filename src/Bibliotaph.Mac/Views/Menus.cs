using AppKit;
using Bibliotaph.Presentation.Services;
using ObjCRuntime;

namespace Bibliotaph.Mac.Views;

/// <summary>The menu bar. Standard items use AppKit's own actions, so the responder chain does the work.</summary>
static class Menus
{
    public static NSMenu Build(MainWindowController main)
    {
        var bar = new NSMenu();

        bar.AddItem(Top("Bibliotaph",
            new NSMenuItem("About Bibliotaph", (_, _) => AboutPanel.Show()),
            NSMenuItem.SeparatorItem,
            new NSMenuItem("Settings…", ",", (_, _) => main.ShowSettings()),
            new NSMenuItem("Your Web Library…", (_, _) => main.ShowDevices()),
            NSMenuItem.SeparatorItem,
            Item("Hide Bibliotaph", "hide:", "h"),
            Item("Hide Others", "hideOtherApplications:", "h", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask),
            Item("Show All", "unhideAllApplications:"),
            NSMenuItem.SeparatorItem,
            Item("Quit Bibliotaph", "terminate:", "q")));

        bar.AddItem(Top("File",
            new NSMenuItem("Add Folder…", "o", (_, _) => main.AddFolder()),
            new NSMenuItem("Add a Folder from Storage…", (_, _) => main.AddFromStorage()),
            new NSMenuItem("Add a Book I Own Elsewhere…", (_, _) => main.AddElsewhereAsync().FireAndForget()),
            new NSMenuItem("Import from an Online Library…", (_, _) => main.ShowSettings(Presentation.Services.SettingsSection.OnlineLibraries)),
            new NSMenuItem("Check a Download…", (_, _) => main.Go(Presentation.Services.Route.DownloadCheck)),
            NSMenuItem.SeparatorItem,
            Item("Close Window", "performClose:", "w")));

        bar.AddItem(Top("Edit",
            Item("Undo", "undo:", "z"),
            Item("Redo", "redo:", "Z"),
            NSMenuItem.SeparatorItem,
            Item("Cut", "cut:", "x"),
            Item("Copy", "copy:", "c"),
            Item("Paste", "paste:", "v"),
            Item("Select All", "selectAll:", "a"),
            NSMenuItem.SeparatorItem,
            // In a book, Find looks in it, as Ctrl+F does on Windows; elsewhere it searches the library.
            Item("Find", "readerFind:", "f"),
            Item("Find Next", "readerFindNext:", "g"),
            Item("Find Previous", "readerFindPrevious:", "G"),
            new NSMenuItem("Search Your Library", "k", (_, _) => main.FocusSearch()),
            Shortcut(new NSMenuItem("Ask Your Library", "f", (_, _) => main.ToggleAsk()), NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask)));

        bar.AddItem(Top("Go",
            new NSMenuItem("Back", "[", (_, _) => main.GoBack()),
            new NSMenuItem("Home", "H", (_, _) => main.Go(Route.Home)),
            new NSMenuItem("Library", "L", (_, _) => main.Go(Route.Library)),
            new NSMenuItem("Collections", "", (_, _) => main.Go(Route.Collections)),
            new NSMenuItem("Binders", "", (_, _) => main.Go(Route.Sessions)),
            new NSMenuItem("Needs Review", "", (_, _) => main.Go(Route.NeedsReview)),
            new NSMenuItem("Summary", "", (_, _) => main.Go(Route.Summary)),
            NSMenuItem.SeparatorItem,
            Item("First Page", "readerFirstPage:", "\uF729", NSEventModifierMask.CommandKeyMask),
            Item("Last Page", "readerLastPage:", "\uF72B", NSEventModifierMask.CommandKeyMask),
            Item("Next Item in Binder", "binderNextItem:", "\uF703", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask),
            Item("Previous Item in Binder", "binderPreviousItem:", "\uF702", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask)));

        // The Library's layouts, as Finder's View menu has its own; ticked for the one showing, and off elsewhere.
        var covers = new NSMenuItem("as Covers", "1", (_, _) => { if (main.Library is { } library) library.Layout = Index.LibraryLayout.Grid; });
        var list = new NSMenuItem("as List", "2", (_, _) => { if (main.Library is { } library) library.Layout = Index.LibraryLayout.List; });
        // Sort By, as Finder's View menu has it: the Library's orders, the one in use ticked.
        var sortBy = new NSMenuItem("Sort By") { Submenu = new NSMenu("Sort By") };
        var view = Top("View",
            covers,
            list,
            NSMenuItem.SeparatorItem,
            Item("Zoom In", "readerZoomIn:", "+"),
            Item("Zoom Out", "readerZoomOut:", "-"),
            Item("Fit Width", "readerFitWidth:", "0"),
            Item("Fit Page", "readerFitPage:", "0", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask),
            NSMenuItem.SeparatorItem,
            sortBy,
            NSMenuItem.SeparatorItem,
            Item("Show Sidebar", "toggleSidebar:", "s", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask),
            Item("Show Inspector", "toggleInspector:", "i", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask),
            NSMenuItem.SeparatorItem,
            Item("Enter Full Screen", "toggleFullScreen:", "f", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask));
        covers.ValidateMenuItem = list.ValidateMenuItem = _ => main.Library is not null;
        view.Submenu!.Delegate = new Layouts(main, covers, list, sortBy);
        bar.AddItem(view);

        var window = Top("Window",
            Item("Minimize", "performMiniaturize:", "m"),
            Item("Zoom", "performZoom:"),
            NSMenuItem.SeparatorItem,
            Item("Bring All to Front", "arrangeInFront:"));
        bar.AddItem(window);
        NSApplication.SharedApplication.WindowsMenu = window.Submenu;

        // Last, as on every Mac.
        var help = Top("Help",
            Shortcut(new NSMenuItem("Keyboard Shortcuts", "/", (_, _) => ShortcutsWindow.Show()), NSEventModifierMask.CommandKeyMask));
        bar.AddItem(help);
        NSApplication.SharedApplication.HelpMenu = help.Submenu;

        return bar;
    }

    static NSMenuItem Shortcut(NSMenuItem item, NSEventModifierMask modifiers)
    {
        item.KeyEquivalentModifierMask = modifiers;
        return item;
    }

    /// <summary>Ticks the Library's layout as the View menu opens.</summary>
    sealed class Layouts(MainWindowController main, NSMenuItem covers, NSMenuItem list, NSMenuItem sortBy) : NSMenuDelegate
    {
        public override void MenuWillOpen(NSMenu menu)
        {
            var library = main.Library;
            covers.State = library?.Layout == Index.LibraryLayout.Grid ? NSCellStateValue.On : NSCellStateValue.Off;
            list.State = library?.Layout == Index.LibraryLayout.List ? NSCellStateValue.On : NSCellStateValue.Off;
            var orders = sortBy.Submenu!;
            orders.RemoveAllItems();
            if (library is not null)
                foreach (var item in LibraryController.SortMenu(library).Skip(1)) orders.AddItem(item);
            sortBy.Enabled = library is not null;
        }

        public override void MenuWillHighlightItem(NSMenu menu, NSMenuItem? item)
        {
        }
    }

    static NSMenuItem Top(string title, params NSMenuItem[] items)
    {
        var menu = new NSMenu(title);
        foreach (var item in items) menu.AddItem(item);
        return new NSMenuItem(title) { Submenu = menu };
    }

    static NSMenuItem Item(string title, string action, string key = "", NSEventModifierMask? modifiers = null)
    {
        var item = new NSMenuItem(title, new Selector(action), key);
        if (modifiers is { } mask) item.KeyEquivalentModifierMask = mask;
        return item;
    }
}
