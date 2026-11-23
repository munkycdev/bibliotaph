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
  - The window has no minimize or zoom. It takes each pane's height, up to a limit, and the pane scrolls beyond it.
  - The toolbar names the panes, and the window's title follows the pane showing.
  - It opens at Library each time, by choice; Apple's guidelines suggest the last pane used. A link into Settings opens
    the pane it is for.
  - Each pane is a grouped form, built with `Form` in `Views/Form.cs`: rounded sections, each row its label (and a line
    of detail) on the leading side and its control on the trailing side, with a short note under the section. A choice
    of several is a pop-up, an on/off setting a switch, a list of things a row each with its buttons, and Add and the
    like go under the list at its trailing edge.

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
- Show at most two levels: the pages and Pinned. Library folders live in Settings › Library and the Library's folder
  filter, and connecting to the web library in Settings › Web Library, not the sidebar.
- Nothing important goes at the sidebar's foot, where a window moved low would hide it; only the status line sits there.

## Pages

- **A page's own actions** sit beside its heading.
  - Pin is a symbol: filled while pinned, with a tooltip saying what pressing it does.
  - Everything else goes in a More menu (`ellipsis.circle`).
  - What can't be done there is left out of the menu rather than dimmed.
- **Separate buttons.** Text buttons don't sit side by side where their text could run together. Saving a search and
  opening a saved one share one menu.
- **Context menus** list only what applies. They keep submenus to one level, and everything in them can also be
  reached in the page or its inspector.
- **Sorting isn't filtering.** The Library's order is a "Sort:" button with the sort symbol, after a divider beside
  the layout, not a pop-up among the filters. Its menu has a Sort By heading and ticks the order in use.
- **Needs review cards** follow the Windows card for each kind:
  - The title opens what the card is about; Option-click opens it in a window of its own.
  - The kind of question is a chip at the trailing edge. An offer also has × there, which gives its "don't ask again"
    answer.
  - The likeliest answer is the prominent button.
  - A kind the page doesn't know yet still shows its answers as plain buttons.
  - `--review-samples <png>` shows one made-up card of each kind and saves the whole list as a picture.
- **Search guide.** Under the toolbar's search field, a panel lists the fields that can be searched and, after a
  field's colon, the values in the library. It opens when the empty field takes the keyboard, and anywhere with ⌥Esc
  (the Mac's completion key) or ↓. It never takes the keyboard from the field.
- **Lists with their own controls on each line** (saved searches, Organize's Books panel) are popovers or panels, not
  menus, since a menu can't hold buttons on a line.
- **Tables.**
  - Columns can be resized.
  - Rows alternate in colour.
  - Clicking a heading that is one of the Library's sorts sorts by it.

## Menus

- The View menu has the Library's layouts, with a tick on the current one, and Sort By, as Finder's has. It also has the sidebar's and inspector's
  show and hide commands, which AppKit retitles as their state changes.
- The Edit menu has Find and Ask.
- The File menu has what Windows puts behind the Library's Add button: Add Folder, Add a Book I Own Elsewhere and
  Check a Download.
- The Go menu has Back (⌘[) and each page, as Finder's does. Back is also in the toolbar beside the page's name, and
  the mouse's back button and a swipe go back too. The Go menu also has First and Last Page (⌘Home, ⌘End) and the
  binder's next and previous item (⌥⌘→, ⌥⌘←).
- Reader commands are menu items that reach the reader through the responder chain, so they're dimmed with no reader
  in reach: Find (⌘F; with no book, it searches the library), Find Next and Previous (⌘G, ⇧⌘G), Zoom In and Out,
  Fit Width (⌘0) and Fit Page (⌥⌘0). Search Your Library is ⌘K from anywhere, as Ctrl+K is on Windows.
- The File menu has Add a Folder from Storage and Import from an Online Library, as Windows' Add menu does.
- The Help menu, last as on every Mac, has Keyboard Shortcuts (⌘/).

## Book details

- The inspector folds into sections in the Windows panel's order, each opening as it was left. A Windows heading in
  capitals is title case here.
- Fields line up as Get Info's do: names in a column, values beside them, a suggestion on the palette's soft green.
- What's done less often (pin, reprocess, correct, show in Finder, remove) is in the header's More menu.

## Cloud files and storage

- A library folder in iCloud Drive or a File Provider folder (OneDrive, Dropbox, Google Drive for Mac) is read like
  any other. The Mac's own cloud file state says which files are only in the cloud, and after indexing such a file goes
  back to online-only with the same eviction Finder's Remove Download uses, when Settings › Processing says so.
- Settings › Library's Storage connects online storage directly, as on Windows: the providers this build has an app
  for, and WebDAV by address and password. Add from Storage picks a folder in it.

## Dialogs and tips

- A dialog that belongs to a window is a sheet on it: Test with a Book, the pages of an adventure, Send to Nornis,
  Add a Book I Own Elsewhere.
- The first-run tour is a popover beside the control each step is about, and Esc skips it.

## Built-in AI

- The Mac runs llama.cpp's Apple silicon build, pinned beside the Windows build in build/engine.json; it draws on
  Metal. How big a model fits comes from Metal's recommended working set, as the GPU shares the Mac's memory.

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
