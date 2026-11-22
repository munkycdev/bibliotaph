using AppKit;
using ObjCRuntime;

namespace Bibliotaph.Mac.Views;

/// <summary>The menu bar. Standard items use AppKit's own actions, so the responder chain does the work.</summary>
static class Menus
{
    public static NSMenu Build(MainWindowController main)
    {
        var bar = new NSMenu();

        bar.AddItem(Top("Bibliotaph",
            Item("About Bibliotaph", "orderFrontStandardAboutPanel:"),
            NSMenuItem.SeparatorItem,
            new NSMenuItem("Your Web Library…", (_, _) => main.ShowDevices()),
            NSMenuItem.SeparatorItem,
            Item("Hide Bibliotaph", "hide:", "h"),
            Item("Hide Others", "hideOtherApplications:", "h", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask),
            Item("Show All", "unhideAllApplications:"),
            NSMenuItem.SeparatorItem,
            Item("Quit Bibliotaph", "terminate:", "q")));

        bar.AddItem(Top("File",
            new NSMenuItem("Add Folder…", "o", (_, _) => main.AddFolder()),
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
            new NSMenuItem("Find", "f", (_, _) => main.FocusSearch())));

        bar.AddItem(Top("View",
            Item("Show Sidebar", "toggleSidebar:", "s", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask),
            NSMenuItem.SeparatorItem,
            Item("Enter Full Screen", "toggleFullScreen:", "f", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask)));

        var window = Top("Window",
            Item("Minimize", "performMiniaturize:", "m"),
            Item("Zoom", "performZoom:"),
            NSMenuItem.SeparatorItem,
            Item("Bring All to Front", "arrangeInFront:"));
        bar.AddItem(window);
        NSApplication.SharedApplication.WindowsMenu = window.Submenu;

        return bar;
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
