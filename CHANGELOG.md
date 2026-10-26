# Changes

## Unreleased

- **A new home page for bibliotaph.online.** It shows what Bibliotaph does, with examples of search, the catalog,
  binders and online libraries, the Windows app and the web side by side, and answers to common questions. See
  [The site](docs/guide/web.md#the-site).

- **Saved pages are no longer imported.** **Import a saved page…** and **Save page…** are gone from the Windows app,
  and **Import a saved page…** and **Choose pictures…** from the web: Bibliotaph's browser reads the page on Windows,
  and **Send to Bibliotaph** brings it, covers included, on the web. See
  [Online libraries](docs/guide/online-libraries.md#without-bibliotaphs-browser).
- **Choose which file is the current version.** A New version? card names both files with their pages, and asks
  which one the book opens: **This file is current** or **The book's file is current**. See
  [Needs review](docs/guide/cataloging.md#needs-review).

## 1.3 · 19 October 2026

New in 1.3.815 (26 October 2026):

- **Connect a Windows PC to your web library.** Settings > Web library in the Windows app opens bibliotaph.online in
  your browser, where you check the code the app shows and approve the PC; it gets a key of its own, kept in Windows
  Credential Manager. Settings › Windows PCs on the web lists connected PCs and disconnects them. See
  [Connecting a Windows PC](docs/guide/web.md#connecting-a-windows-pc).
- **Your work syncs between the Windows app and the web.** A connected PC and bibliotaph.online merge your corrections,
  hearts, notes, collections, binders, saved searches, pins and Needs review answers both ways, as two PCs do through
  a sync folder: a change goes up a few seconds after you make it and comes down within a minute or so. Settings > Web
  library says when it last merged. The app still works offline and catches up when it's back.
- **The AI reads each book once, for all your devices.** With a PC connected to your web library, what the AI found
  in a book is shared by the file's contents: the web library gets the PC's suggestions for the same file, ready in
  Needs review without an AI key of its own, and a book the AI has already read on one device isn't sent to the AI on
  another. Whole-book scans are shared the same way. See [Connecting a Windows PC](docs/guide/web.md#connecting-a-windows-pc).
- **Scanned pages are read once, for all your devices.** The text OCR read on a scanned book is shared by the file's
  contents too, so a book the PC read is searchable in your web library straight away, and the other way round, without
  reading its pages again. See [Connecting a Windows PC](docs/guide/web.md#connecting-a-windows-pc).
- **Cataloguing with AI starts sooner after OCR.** A book waiting for its scanned pages is catalogued as soon as they're
  read, instead of up to two minutes later.
- **Needs review forgets a removed folder's books.** Removing a library folder, on the web or in the Windows app, left
  its books' suggestions, new terms and failed files in Needs review and the sidebar's count; now only the books in the
  library's folders count.

And Bibliotaph on the web, at [bibliotaph.online](https://bibliotaph.online), which the app above connects to. It is
open by invitation while it settles:

- **A footer on the web.** Every page of the web app, except the readers, ends with links to the site's pages
  (Features, About, the Change log, Contact us, Terms, Privacy, Licences and the source code), the version that's
  running, and a tagline that changes each time you load the page. The site's own pages have the same. See
  [The site](docs/guide/web.md#the-site).
- **The site around the web app.** bibliotaph.online now opens, for anyone signed out, on a page saying what
  Bibliotaph is, and has pages of its own for Features, About, the Change log, Contact us, Terms, Privacy and Licences
  (Bibliotaph's licence and every component's). Signed in, it opens your library as before. See
  [The site](docs/guide/web.md#the-site).
- **A clearer tab icon on the web.** The "b." in the browser tab is bolder and bigger, and light on a dark tab bar.
- **Weekly backups and Restore, on the web.** Bibliotaph on the web makes a backup each week and keeps the last four,
  and Settings › Backup lists them to download or restore. Restore also reads a backup you choose, made on the web or
  by the Windows app, shows what it holds before anything changes, and keeps your current catalog first. See
  [Backup and export](docs/guide/web.md#backup-and-export).
- **Send to Bibliotaph, on the web.** A bookmark in Settings › Online libraries, dragged to the bookmarks bar and clicked
  on your D&D Beyond, Demiplane, Humble Bundle or DriveThruRPG library page, sends the page to Bibliotaph with its
  covers, so there's no saving and picking files. See [Online libraries](docs/guide/web.md#online-libraries).
- **Send to Bibliotaph extension for Chrome and Edge.** The same from a button in the browser's toolbar, for sites that
  keep the bookmark from reaching Bibliotaph, and with covers from the services' image hosts. See
  [Online libraries](docs/guide/web.md#online-libraries).
- **The start of Bibliotaph on the web.** bibliotaph.online gets its server and a browser app that looks like the
  Windows one. It is invite-only: a one-time link makes an account, signed in through Auth0 with Google, Microsoft or an email address, and admins make
  invitations in Settings. The privacy policy is published at bibliotaph.online/privacy. Your library comes to it in the
  next steps. See [Bibliotaph on the web](docs/guide/web.md).
- **OneDrive on the web.** Connect one or more OneDrives in Settings › Storage, then add library folders from them as in
  the Windows app. Bibliotaph looks through them, reads each new file once without keeping a copy, and notices changes
  in your OneDrive within minutes. The sidebar says what it's doing.
- **Dropbox on the web.** Connect one or more Dropbox accounts in Settings › Storage beside OneDrive, including a team's
  space, and add library folders from them. Changes in your Dropbox are noticed within minutes, and books open in the
  reader as they do from OneDrive.
- **Google Drive on the web.** Connect one or more Google Drives in Settings › Storage and add library folders from
  them. Google Docs and Sheets are left out, two files of one name are told apart, and changes are noticed within five
  minutes.
- **What your storage allowed, in Settings.** Each storage account in Settings › Storage now says what Bibliotaph was
  allowed to do, such as read your files and keep access while you're signed out. OneDrive no longer asks to write to a
  folder of Bibliotaph's own, as nothing on the web uses it yet.
- **Settings on the web laid out like the Windows app's.** *Make yourself at home.*, with a list of sections beside the
  one chosen: Storage, Library, Windows PCs, AI, Appearance, Needs review, Books in this browser and Account, plus
  Invitations and Accounts for admins. Appearance and Needs review are new on the web, with the Windows app's choices,
  and About Bibliotaph shows the version, the licence and links to the source. A new invitation's link has a Copy link
  button. See [Settings](docs/guide/web.md#settings).
- **Backup and export on the web.** Settings › Backup downloads a backup the Windows app can restore, and exports the
  whole library as CSV or JSON, as the Windows app's Back up now…, Export CSV and Export JSON do. The web server's
  sealed AI keys are left out of both, as the Windows app's keys always are. See
  [Backup and export](docs/guide/web.md#backup-and-export).
- **Start on, folder names and folder rules on the web.** Settings › Library has the Windows app's Start on (Home or
  Library), the folder names read as suggestions, each of which can be switched off, and the folder rules answered in
  Needs review, each with its switch. See [Library folders](docs/guide/web.md#library-folders).
- **Easier to find your way on the web.** A book in the Library has the Windows app's right-click menu. On a phone,
  Settings and what the library is doing sit under the navigation instead of being hidden. Home welcomes you back
  once your library has books, even ones that came from a connected PC before any folder was added here. The Library's
  Add folder sits beside Add, and on a phone a page's buttons go under its title.
- **Keyboard shortcuts on the web.** Ctrl+K, or /, goes to the search box from anywhere, Ctrl+/ lists every shortcut
  that works on the web (also under Settings), and Shift+Enter or a middle-click on a book opens it in a reader window
  of its own, as in the Windows app. Home's covers have the book's right-click menu too.
- **Install the web version as an app.** bibliotaph.online installs from Chrome, Edge or Safari with its own icon and a
  window without the address bar, handy on a tablet at the table. While a book is open on the web, the screen stays
  on.
- **Processing on the web.** Settings › Processing shows each step of reading your library with its bar and the time
  left, and what is being read now, as in the Windows app, with Pause indexing, Pause reading scans and Pause AI, each
  kept until you resume it. Settings' sections now follow the Windows app's order. See
  [Processing](docs/guide/web.md#processing).
- **Vocabulary on the web.** Settings › Vocabulary has the Windows app's tabs of terms, each renamed, given a short
  name for covers or other names in place, and Your tags, each found in your books' pages by its words. See
  [Vocabulary](docs/guide/web.md#vocabulary).
- **Your books read on the web.** bibliotaph.online reads each book in your library folders as the Windows app does:
  pages, outline, text for search, covers, and hints from file and folder names. A book is downloaded once to be read
  and its copy deleted when it's done. OCR and AI on the web come later.
- **Your library in the browser.** bibliotaph.online's Library shows your books with their covers or as a list, with the
  Windows app's filters, orders and Packs or Each file, and the search box finds books and the pages inside them. See
  [Your library and search](docs/guide/web.md#your-library-and-search).
- **Book details and Home on the web.** A click on a book opens its details as in the Windows app: its fields with
  where each value came from, its adventures or pack files, its file, where it is, its copies and how far reading it has
  got. Home shows the books opened and added last. Covers are kept with your account across server restarts. See
  [Your library and search](docs/guide/web.md#your-library-and-search).
- **Change a book's details on the web.** As in the Windows app: edit fields and choose between suggestions, Cataloged
  wrong… and the offer to correct look-alikes, favorites, notes, adventures inside a PDF, Reprocess, a pack's cover and
  split, and copies, with Undo. See [Changing a book's details](docs/guide/web.md#changing-a-books-details).
- **Type to filter on the web.** More filters has the Windows app's boxes for setting, themes, environments, publisher
  and edition: type, pick from the matching values with their counts, and each pick becomes a chip. See
  [Your library and search](docs/guide/web.md#your-library-and-search).
- **Collections on the web.** Make, open, rename, move and delete collections, and add books to them or take them out
  from a book's details, as in the Windows app, with Undo. See [Collections](docs/guide/web.md#collections).
- **Favorites, saved searches and pins on the web.** Favorites shows the books with a heart. Save search keeps a search,
  its filters and order as a page of its own, with Update saved search and Save as new once it's changed, and the
  Saved searches list has them all. Collections, saved searches, books and Favorites pin to the sidebar and Home, in the
  order you drag them to. They are the same ones the Windows app shows. See
  [Favorites, saved searches and pins](docs/guide/web.md#favorites-saved-searches-and-pins).
- **Select mode on the web.** Select in the Library ticks books, with Shift+click and Ctrl+A, and the Select bar adds
  them to a collection or takes them out, joins them as the Same book, packs them together, removes adventures and
  exports them as a CSV, as in the Windows app, with Undo. See [Select mode](docs/guide/web.md#select-mode).
- **Edit metadata on the web.** In Select mode, Edit metadata sets, adds or removes values on every ticked book at
  once, with Review changes first and Undo after, as in the Windows app. See [Select mode](docs/guide/web.md#select-mode).
- **Binders on the web.** The Binders page groups your binders as in the Windows app, Home has Continue preparing, and a
  binder's page arranges its items and sections, edits labels and notes, and duplicates or deletes it, with Undo.
  Show these books in the Library and pinned binders work too. See [Binders](docs/guide/web.md#binders).
- **Add to binder on the web.** A book's details, the Select bar and a matching page's + add to a binder, the one you
  worked on last, another or a new one, as in the Windows app, with Undo. See [Binders](docs/guide/web.md#binders).
- **Copies and new versions on the web.** Two files of one book on your drive become one card with two copies, and a
  file that shares most of a book's pages is offered as its new version, as in the Windows app.
- **Ask this book on the web.** The reader's Ask panel answers a question from the book's own pages, as in the Windows
  app, shown as the model writes it, with each citation opening its page, follow-ups and New conversation; an
  embedding model in Settings › AI finds pages by meaning too. See [Ask this book](docs/guide/web.md#ask-this-book).
- **Ask in the top bar on the web.** The ✦ button beside the search box reads a request in plain English as a search,
  shown as chips to change before searching, or answers a question from the books the Library shows, each citation
  opening its book at its page. Reading the whole library for meaning is a setting in Settings › AI, off until ticked.
  Search words now reach the reader on the web, so a matching page opens with them marked.
- **Scan whole books on the web.** Settings › AI's Scan whole books reads each book chapter by chapter, as in the
  Windows app, and a book's details show what it found under Found inside the book, with Scan again. It's off on the
  web until you tick it, since every chapter is a request on your key. See
  [Scanning whole books](docs/guide/web.md#scanning-whole-books).
- **AI on the web, with your own key.** Settings › AI catalogues your books as the Windows app's AI does, with a model
  service you choose on the internet, such as OpenAI or OpenRouter, and your own key, kept encrypted and never shown
  again. Use AI, Test with a book, Reclassify and the folders sent to AI work as on Windows. See
  [AI](docs/guide/web.md#ai).
- **Scanned pages on the web.** The server reads scanned pages by OCR, in English, so search finds them as on Windows,
  and the sidebar counts the pages left. See [Bibliotaph on the web](docs/guide/web.md).
- **Sign in to the web through Auth0.** bibliotaph.online's sign-in page has one Continue button, which offers Google,
  Microsoft or your email address, with Apple to come. Link another sign-in in Settings adds another way into the same
  account, and Sign out signs you out of the sign-in page too. The privacy policy names Auth0.
- **Discord sign-ins named in Settings.** A sign-in through Discord, or another of Auth0's custom social connections, is
  listed under its own name rather than as "Oauth2".
- **Books you own elsewhere on the web.** The Library's Add menu has Add a book I own elsewhere…, as in the Windows
  app, with a cover picture of your own; its details have Choose a cover image…, Remove the cover and Remove from
  library with Undo. See [Bibliotaph on the web](docs/guide/web.md#books-you-own-elsewhere).
- **Needs review on the web.** bibliotaph.online's Needs review has the Windows page's two tabs and its sidebar count.
  Field suggestions can be accepted, accepted for every book suggesting the same, edited or rejected, and new terms
  added, filed under another or rejected, each with Undo; Show narrows the list. Files needing attention can be tried
  again, and a fault's details copied for a bug report. The cards for new versions, image packs, books owned
  elsewhere, places to check, adventures inside files, books cataloged wrong, folder rules and collections are
  answered as in the Windows app, with Undo. See [Needs review](docs/guide/web.md#needs-review).

- **Read your books on the web.** bibliotaph.online opens a PDF in a reader laid out as the Windows one: the page box
  with printed page numbers, zoom, contents, page thumbnails, links with Back, Find with F3 and Whole file for an
  adventure, the heart and pin, and Pop out. A book opens where you left it, an adventure at its own pages, and a
  matching page with the search's words marked. Pages come straight from your OneDrive and are kept in your browser,
  never on the server. See [Reading a book](docs/guide/web.md#reading-a-book).
- **Keep books offline in your browser.** The reader's Keep offline button downloads a whole book to the browser, so
  it opens without waiting on the network, and Settings › Books in this browser shows the books kept there, sets how
  much room the others may take, and lets them go. See [Books in this browser](docs/guide/web.md#books-in-this-browser).
- **Pictures and packs in the web reader.** A picture opens fitted to the window, with the Windows reader's zoom, and a
  pack steps through its pictures and PDFs from the toolbar or with the arrow keys. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **E-books in the web reader.** EPUB and Kindle books open in the browser as in the Windows reader, in the same pages
  search found, with Find, zoom, links and fixed-layout books; the server never sees the book. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **Notes in the web reader.** Note adds a note on the page in view or the selected text's page, and the Notes panel
  lists the book's page notes to go to, change or delete with Undo, as in the Windows reader; thumbnails badge pages
  with notes. See [Reading a book](docs/guide/web.md#reading-a-book).
- **Admins can delete accounts on the web.** Settings › Accounts lists everyone who has joined; deleting one is the
  same as its owner closing it, and signs them out everywhere. See [Inviting others](docs/guide/web.md#inviting-others).
- **Close your account on the web.** Settings › Account › Close account… deletes your account, library, covers, storage
  connections and the books kept in that browser, and its backups within 30 days. Nothing in your storage is touched.
  See [Signing in and out](docs/guide/web.md#signing-in-and-out).
- **The web reader's own actions.** Add page and Add pages… add pages, or a picture, to a binder with Undo; Mark as
  adventure and the contents' Index as its own adventure… give pages a card of their own; Use this page as the cover
  and Use this image as the pack's cover change a card's cover, as in the Windows reader. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **Index pages link to their pages on the web.** The page numbers on an index or contents page that has no links of
  its own become links in the web reader, found by the same rules as in the Windows reader. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **The binder reader on the web.** Opening a binder shows its items down the left, the item in hand in the reader and
  the binder's notes on the right, with Next and Previous (Ctrl+Right and Ctrl+Left), Add this page, renaming in the
  list, Back to this item's pages, the page notes on the item's pages, and Use this page for an item whose pages
  changed. Edit binder opens the list, which has Done and opens each item at its pages. See
  [Binders](docs/guide/web.md#binders).
- **At hand and Pop out in the web binder reader.** At hand lists the binders kept at hand with their pages, those for
  the binder's game systems first, and opens a page in a window of its own; Pop out moves the binder into its own
  window and Return to main window brings it back. See [Binders](docs/guide/web.md#binders).
- **WebDAV and Nextcloud on the web.** Settings › Storage connects a WebDAV server, such as Nextcloud, ownCloud or a
  NAS on the internet, with its folder's address, your user name and an app password, kept encrypted and used only to
  read. Its books open in the reader through the server, and Nextcloud's changes are noticed within five minutes. See
  [Connecting your storage](docs/guide/web.md#connecting-your-storage).
- **Box on the web.** Settings › Storage connects a Box account, read-only, once the server's Box app is set up. Its
  books open in the reader through the server, and changes are noticed within five minutes. See
  [Connecting your storage](docs/guide/web.md#connecting-your-storage).
- **The sidebar folds on the web, and the search guide is there.** The button beside Settings, or Ctrl+B, folds the
  sidebar to its icons as in the Windows app. The search box's guide lists the fields to search by and, after a field's
  colon, its values in your library with their counts; the Windows app and the web now list them from the same code.
- **The details say which storage service a file is in.** Where it is, and each copy, start with the service, such as
  OneDrive, Dropbox or Box, with a cloud mark, before the account and folders.
- **Online libraries on the web.** Settings › Online libraries imports the books you own on D&D Beyond, Demiplane,
  Humble Bundle and DriveThruRPG from a library page saved with Save as, with the Windows app's preview, Import with
  Undo, covers from the pictures saved beside the page, and the books no longer listed. See
  [Online libraries](docs/guide/web.md#online-libraries).
- **More of the Windows app's book actions on the web.** A book's menu and details have Open on the online library you
  own it on, Ask this book, Forget its text and Read it again, and Download the file for one whose protection Bibliotaph
  can't open. Reprocess has Reprocess with OCR on every page…, a book owned only online opens its page there, and Home
  offers to set up AI as the Windows app does.
- **Locked PDFs on the web, as in the Windows app.** The reader's password dialog has Make its text searchable, so the
  book is read for search with it, and Remember it, which keeps the password sealed in your web library so the book
  opens without asking. Settings › Passwords lists those books and forgets their passwords, and Needs review's locked
  files have Open to unlock, and protected ones Download the file. See [Reading a book](docs/guide/web.md#reading-a-book).
- **See what the AI is reading, on the web.** Settings › AI and Settings › Processing open the Windows app's AI reading
  page: the book the AI is on, its answer as it writes it, and what the evidence check kept from the last books. See
  [AI](docs/guide/web.md#ai).
- **Check a download on the web.** The Library's Add button has Check a download…, which checks a folder or ZIP on your
  computer against your library as the Windows app does. Your browser reads the files and sends only their fingerprints,
  never the files. See [Check a download](docs/guide/web.md#check-a-download).

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
