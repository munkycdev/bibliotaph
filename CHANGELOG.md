# Changes

## Unreleased

- **Built on .NET 11 RC2**, the second release candidate.
- **Ask your library a question:** in Ask mode, **Answer from my books** (or Ctrl+Enter) answers from the pages of
  every book the Library shows, citing each claim's book and page as a link that opens it there, and saying plainly
  when your books don't cover it. With an embedding model, your whole library is read for meaning in the background,
  pausing while you use the AI; **Settings > AI** shows how far it has got and can switch it off.
- **Ask this book finds the right pages more consistently:** index and contents pages no longer crowd out the pages
  that answer, a question's rare words count for more than its common ones when deciding whether the book covers it,
  the AI answers without "thinking" first, and a book is prepared for finding by meaning as soon as it's opened.
- **Your tags can find themselves.** In **Settings > Vocabulary > Your tags**, give a tag the words that signal it
  ("Undead": undead, zombie, lich) and every book whose pages use them often enough gets it as a suggestion, without
  reading any file again. New books are checked as they're read, and a tag you reject on a book stays rejected.
- **Collapse the sidebar** to its icons with the button beside Settings or **Ctrl+B**. It stays the way you left it.
- **Setting**, **Themes**, **Environments**, **Publisher** and **Edition** filters under **More filters**: type a few
  letters, pick from the values in your library with their book counts, and each pick becomes a chip. Chips in one box
  find books with any of them, boxes narrow together, and saved searches keep them.
- **Ask this book finds pages by meaning with the built-in AI.** Settings > AI offers nomic-embed-text (about 150 MB)
  to download, so Ask no longer matches words only.
- **Double-click a book** in the Library to open it; a single click still shows its details.
- **Remove from library** is on the right-click menu of a book you own elsewhere, as well as in its details.
- **Lists are in name order**: the filter drop-downs, the search guide's values, collections and sub-collections, the
  Add to collection menu, and binders within each group. The sidebar's Pinned list keeps the order you dragged it into.
- **Collection** under More filters shows the books that aren't in any collection yet, or those that are.
- **Remove adventures** in the Library's Select mode takes away the cards of several adventures inside files at once,
  with one Undo for them all.
- **Packs hold PDFs too.** Tick PDFs, such as character sheets or pregens, with images or packs in Select mode and
  **Pack together** makes them one card, "Pack · 6 files". Opening it steps through its files, PDFs opening as PDFs;
  their pages are found under the pack; **Split into separate files** gives each its card back. Folders are still
  packed automatically only for their images.
- **Choose a cover image…** gives a book you own elsewhere a cover of your own, from the Add dialog or its details.
- **Use this page as the cover** in the reader makes any page of a PDF its cover in the Library.
- **Show** on Needs review's Metadata suggestions narrows the list to one kind of card, or to the suggestions for one
  field, with a count for each.
- **Collections from folders**: Needs review offers to make a folder of three or more books a collection, nested as
  the folders are. Books that land in the folder later join it, until you change its books by hand.
- **Books for collections**: Needs review suggests books that share a series, or a setting and game system, with most
  of a collection's books.
- **Scan whole books** in Settings > AI (on by default) has the AI read every page of each catalogued book, a few
  chapters at a time, when it has nothing else to do. A book's details show what it found under **Found inside the
  book**, the reader's contents show it beside each chapter, and `setting:`, `environment:`, `theme:` and the new
  `creature:` search find books by their chapters. The book's own details are never changed.
- **Adventures stay inside their book** while you browse the Library: the anthology's card says how many it holds and
  its details list them. A search still finds each adventure, shown with its book's cover and an **Adventure** ribbon,
  in place of the book when both match. **Adventures inside files** in More filters shows them as cards again.

## 1.0.0 · 12 October 2026

Bibliotaph's first release: a Windows app that catalogs the RPG books, maps and handouts you already have, where they
are, and finds the right book and the right page.

- **Your library, in place.** Add your folders and Bibliotaph reads every PDF, JPG, PNG and WebP in them, ZIPs
  included, on local, USB, network and OneDrive folders. It never moves, renames, edits or deletes a file, and notices
  files that are added, moved or changed. Scanned pages are read with Windows' own text recognition.
- **Search every page.** Books on the Documents tab, matching pages with snippets on Inside documents, fields such as
  `system:`, `type:`, `level:` and `players:`, filters shown as chips you clear one at a time, and saved searches.
- **A reader built for the table.** Contents, find in the book, zoom, thumbnails, page notes, index and contents pages
  whose page numbers are links, and as many pop-out windows as you like.
- **A catalog that fills itself in.** Title, game system, type, levels, players, duration, setting, themes and more,
  suggested from folder names, the file and, if you turn it on, AI, each with the evidence it came from. Your own
  corrections always win. Copies share one card, map folders become image packs, and adventures inside an anthology
  get cards of their own. Needs review and Files needing attention list what wants a look.
- **Optional, local AI.** Bibliotaph can download and run a model itself, or use Ollama, LM Studio or another model
  server. It catalogs books, turns a plain-English request into a search with Ask, and answers questions from a book's
  own pages with Ask this book, citing each page.
- **Books you own online.** D&D Beyond and Demiplane books are listed beside your files and read in Bibliotaph's online
  reader; Humble Bundle and DriveThruRPG books are listed under where to get them, and download into your library. You
  sign in to each site yourself; no password is stored.
- **Organized your way.** Favorites, collections, saved searches and pinned books and views in the sidebar.
- **Binders for your next session**, holding books, pages and online pages in order, with notes, and Next and
  Previous at the table.
- **Your work kept safe.** Weekly backups, restore on a new PC, export to a spreadsheet, and a library sync file to
  share your work with a second PC.
- **Installs per user** with no administrator password, and updates itself in the background. Not code-signed yet.
