# Mac app design notes

The Mac app follows Apple's Human Interface Guidelines and the macOS 27 refinements to Liquid Glass. These notes record
what that means for Bibliotaph, so new Mac views fit in. The sources are listed at the end.

## What macOS 27 does for us

macOS 27 refines Liquid Glass. Sidebars reach the window's edges, a selected sidebar row's text is semibold, and windows
share one corner radius. The toolbar is uniform and frosted, so its items read clearly over content. People can set
the glass anywhere from clear to tinted.

An AppKit app gets all of this from the standard pieces. Keep using them:

- `NSSplitViewController`, with a sidebar item and an inspector item
- a unified `NSToolbar`
- source-list tables
- standard buttons, pop-up buttons and search fields

Don't add custom backgrounds, materials or visual-effect views behind bars and sidebars. They block the glass.

## Windows

- **Title.** Title a window after what it shows: the page, the book, the service, the settings pane. Never use the app's
  name.
- **Quitting.** Sheets and alerts that can wait set `PreventsApplicationTerminationWhenModal = false`, so they don't
  stop the app quitting. A dialog left open shouldn't block a restart or an overnight update.
- **Keyboard.** Every window sets `AutorecalculatesKeyViewLoop`, so Tab moves through the controls a page shows now.
- **Settings.**
  - The window has a fixed size, with no minimize or zoom, and its panes scroll.
  - The toolbar names the panes, and the window's title follows the pane showing.
  - It reopens at the pane last used. A link into Settings opens the pane it is for.

## Toolbars

- **Leading edge:** the sidebar toggle, then navigation. Back and forward are navigational items, which sit before
  the title, as in Safari.
- **Trailing edge:**
  - search
  - the inspector's toggle, over the inspector's tracking separator
  - the window's one prominent action, if it has one (Read this page)
- **Symbols.** Use SF Symbols without borders. Give each item a label and a tooltip.
- **Menu bar.** Every toolbar item also has a command in the menu bar, because people can hide the toolbar.

## Sidebar

- Use SF Symbols, and let the system tint them with the accent colour.
- Show at most two levels: the pages, Pinned, Library folders and Web library.
- Nothing important goes at the sidebar's foot, where a window moved low would hide it. Connect is a row in the list,
  and only the status line sits at the bottom.

## Pages

- **A page's own actions** sit beside its heading.
  - Pin is a symbol: filled while pinned, with a tooltip saying what pressing it does.
  - Everything else goes in a More menu (`ellipsis.circle`).
  - What can't be done there is left out of the menu rather than dimmed.
- **Separate buttons.** Text buttons don't sit side by side where their text could run together. Saving a search and
  opening a saved one share one menu.
- **Context menus** list only what applies. They keep submenus to one level, and everything in them can also be
  reached in the page or its inspector.
- **Tables.**
  - Columns can be resized.
  - Rows alternate in colour.
  - Clicking a heading that is one of the Library's sorts sorts by it.

## Menus

- The View menu has the Library's layouts, with a tick on the current one. It also has the sidebar's and inspector's
  show and hide commands, which AppKit retitles as their state changes.
- The Edit menu has Find and Ask.

## Sources

- Apple Human Interface Guidelines:
  [Sidebars](https://developer.apple.com/design/human-interface-guidelines/sidebars),
  [Toolbars](https://developer.apple.com/design/human-interface-guidelines/toolbars),
  [Settings](https://developer.apple.com/design/human-interface-guidelines/settings),
  [Split views](https://developer.apple.com/design/human-interface-guidelines/split-views),
  [Context menus](https://developer.apple.com/design/human-interface-guidelines/context-menus),
  [Lists and tables](https://developer.apple.com/design/human-interface-guidelines/lists-and-tables),
  [Sheets](https://developer.apple.com/design/human-interface-guidelines/sheets),
  [The menu bar](https://developer.apple.com/design/human-interface-guidelines/the-menu-bar),
  [Layout](https://developer.apple.com/design/human-interface-guidelines/layout)
- WWDC26: [Modernize your AppKit app](https://developer.apple.com/videos/play/wwdc2026/289/) covers Liquid Glass in
  macOS 27, concentricity, graceful termination and state restoration.
- WWDC25: [Build an AppKit app with the new design](https://developer.apple.com/videos/play/wwdc2025/310/) covers
  toolbars, sidebars and inspectors in Liquid Glass.
