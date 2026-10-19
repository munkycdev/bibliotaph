# Changes

## 1.3 · 19 October 2026

New in 1.3.590 (19 October 2026):

- **See each file in a pack.** Two new buttons beside covers and list switch the Library between showing each pack as
  one card (as before) and showing every image and PDF inside packs on a card of its own. A file's card says which
  pack it is in, opens in its pack so the arrows still step through the rest, and finds its pages under its own card.
  Filters, saved searches and its details are its pack's. The choice is remembered, for browsing and searching alike.
- **One Add button in the Library.** **Owned elsewhere** and **Check a download** are now one **Add** button, styled
  like **New collection**, beside **Add folder**. Its menu has **Add a book I own elsewhere…**, **Import from an
  online library…** and **Check a download…**, which work as before.
- **Back up now starts in the backups folder.** The save dialog opens in the backups folder in Bibliotaph's data
  folder, beside the weekly backups, instead of wherever Windows last saved a file. You can still pick anywhere else.
- The Library's **Select** button shows a stack of ticked boxes instead of a plain tick, so it reads as choosing
  several books.
- **The sidebar's status stays inside the sidebar.** A long status, such as indexing a big library, wraps to a second
  line instead of running past the sidebar's edge and being cut off. Pointing at it shows all of it, also with the
  sidebar folded.

## 1.2 · 13 October 2026

New in 1.2.573 (15 October 2026):

- **EPUB books are read.** EPUB files in your library folders, and inside ZIPs, are indexed in place like PDFs: their
  text is searchable page by page, the AI can catalogue them, and their own title, authors, publisher, description
  and cover fill in the card. A book that marks its printed pages keeps those page numbers; otherwise Bibliotaph cuts
  it into pages of about a printed page each, the same way every time. `format:epub` finds them. A DRM-protected
  EPUB still gets a card with its title and author, and is listed in **Files needing attention**.
- **Kindle books are read.** MOBI, AZW and AZW3 files are indexed in place and read in Bibliotaph's reader like
  EPUBs, with their contents, pictures and links, and their title, authors, publisher, description and cover fill in
  the card. Their pages are cut by length, as Kindle keeps printed page numbers in a separate file. `format:kindle`
  finds them. A Kindle book with DRM, or in Amazon's newer KFX format, gets a card but its text isn't read.
- **Read e-books in Bibliotaph.** An EPUB opens in the reader as flowing text in the app's colours, with its
  pictures. The page box, contents, Find, notes, binders and **Ask this book** work as in a PDF, search opens it at
  the page it found with the words marked, its links go to their place with **Back**, and zoom makes the text bigger.
  A comic or picture book shows its pages as drawn. Nothing in a book can run a program or reach the internet.
- **Pick a pack's cover.** A map or image pack can show one of its own images on its card instead of the mosaic of
  its first four. Right-click an image in the pack's details and choose **Use as the pack's cover**, or click the
  picture button in the reader while stepping through the pack. **Use the mosaic** in the details goes back, and the
  card goes back by itself if that image is deleted or leaves the pack.
- **Close a suggestion with its ×.** The Needs review cards that offer to make a collection, add books to one, make
  an image pack, start a folder rule or give a file's adventures cards of their own now have a × in the corner. It
  gives the card's "don't ask again" answer, so that suggestion stays closed after a restart or a rescan, while a new
  one can still come. **Undo** brings it back.
- **Fixed: a book with only names on its pages could fail the AI every time with "The model's answer wasn't valid
  JSON".** On a map, with nothing but place names, the built-in AI quoted the whole page over and over until its
  answer ran out of room and stopped part way. Each value and quote the AI writes now has a length limit, and an
  answer that still runs out keeps what the model finished, checked like any other. A book cut off before the model
  finished anything says so: "The model's answer was cut off". Books that failed this way are read again when they're
  next tried, or press **Try all again** in **Files needing attention**.

New in 1.2.544 (15 October 2026):

- **Check a book beside Needs review.** Each card has a button beside its title that opens the book in a window of
  its own, at the page the suggestion's evidence is on, while Needs review stays put so you can read and decide side
  by side. Middle-click or **Shift+Enter** on the title does the same. The cards share the window, so going down the
  list shows each book in it instead of opening another.
- **Adventures show who they're for.** The Library's list shows each book's players ("4–6 players") beside its levels,
  and the details header shows both. An adventure's details show **Players** even before anyone has set it, so you
  can fill it in straight away.
- **Adventures pick up their party size from their blurb.** When you accept a file's proposed adventures, a blurb
  such as "for four characters of 3rd level" now suggests the players as well as the levels, and the Needs review
  card lists both. An adventure you mark by hand, from the reader, a bookmark or the details, gets the same
  suggestions from the blurb on its first pages, quoted as evidence. Values you set are never replaced.

New in 1.2.530 (15 October 2026):

- **Adventures are proposed only when the book says it holds them.** A campaign whose chapters each start with a
  level blurb no longer looks like a collection. The rules and the AI now look for the book's own words, before its
  first adventure or on its back cover, saying it is a collection of separate adventures or one-shots, and the Needs
  review card shows those words and their page. Proposals still waiting are cleared and the rules look at the library
  again; files you already accepted or kept as one book stay as they are. The AI asks the stricter question for books
  it reads from now on. Blurbs such as "four 1st- to 2nd-level PCs" are now read, and bookmarks for covers and ads,
  or a contents page listing levels, no longer make adventures.
- **Fixed: the question about a file's adventures could send watermark lines to the AI.** The lines naming the buyer
  and their order are now left out, as they already were when the AI catalogues a book.
- **The Library remembers its order.** The order picked in the toolbar, for the books and for the matching pages,
  stays the next time the Library opens, after a restart too. Browsing and searching each keep their own, so
  **Best match** still comes with a search. A saved search still opens in the order it was saved with.
- **Fixed: Undo after removing an adventure sent it to the AI again.** It now comes back still read by the AI. Undo
  after removing a book owned elsewhere also brings back its page notes, Ask conversation and reading place, which
  it could have kept from a time when a file was joined to it.
- **Fixed: a reader could stay blank.** Closing or switching a book at the moment a page was being drawn could stop
  the reader drawing pages until it was closed. A page that can't be drawn now says why and the rest carry on.
- **Fixed: Ask this book failed on some pages.** A page whose PDF text holds a broken character made the question
  fail; those pages are now searched like any other.
- **index.db is kept when it is only busy.** If another program holds the search index or the disk can't be read at
  start, Bibliotaph now says so instead of deleting the index and reading the whole library again. A damaged index is
  still rebuilt.
- **An AI answer with nothing in it is treated as the AI's hiccup, not the book's.** The AI is checked before a book
  is marked as one it couldn't read, so a server that has stopped answering pauses the AI work instead.
- **An unexpected error now closes Bibliotaph cleanly** instead of leaving it running in a half-finished state. A
  problem outside the app, such as a file that went away, a busy clipboard or the AI server not answering, is still
  just shown, and the app carries on.
- **Problems that used to pass silently are now in the log:** a library sync change that keeps failing to apply, and
  background work such as loading a cover or saving a note that fails.

New in 1.2.487 (14 October 2026):

- **Choose from more built-in AI models.** **Settings > AI > Built in** now has one list, with the models that fit
  your graphics card first: Qwen3 4B and 8B, Phi-4 mini, **Llama 3.1 8B** and Granite 3.3 8B for most cards, and
  Qwen3 14B, Mistral Small 3.2 24B and Qwen3 30B-A3B for bigger ones. Each shows its download size, the graphics
  memory it needs and its licence, and every download is checked against a pinned hash. **Use a model file…** runs a
  `.gguf` model already on your PC, such as one LM Studio downloaded, where it is.
- **Fixed: AI stopped with "request exceeds the available context size".** Pages dense with stat blocks or tables
  could make a whole-book scan ask more of the built-in AI than it has room for, and the AI then waited on those
  chapters for ever. They're now cut to fit and read, and if even that fails only those chapters are skipped.

In 1.2:

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
- **Fixed:** importing from Demiplane found no books, because its Sources page now links each book relative to the
  site. Both kinds of link are read.
- **Fixed:** a pop-out window opened from a drawer or menu could stay behind the main window; it now comes to the
  front. The online reader's header matches the PDF reader's, and the details drawer's headings are easier to read.

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
