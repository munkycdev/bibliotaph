# Bibliotaph features

What Bibliotaph does and why. For how it's built, see [architecture.md](architecture.md); for how to use it, see the [user guide](guide/getting-started.md).

## What it's for

Bibliotaph helps a game master find and use the RPG material they already own, without cataloging every file by hand or reorganizing their folders. The question it answers is: **"What do I own that would help me prepare or run this session?"**

It is a Windows desktop library that indexes PDFs, e-books, maps and handouts where they already are, fills in useful RPG details by itself, and finds both books and the pages inside them. It works only while it is open, needs no server, and is fully usable without AI.

It is made for a game master with a large, mixed collection across several game systems and editions, who wants to prepare a game, track down a half-remembered passage, browse what they own, add a new bundle, run a session from prepared pages, and fix the odd mistake without becoming a full-time librarian.

### Principles

1. **Find useful content quickly.** Browsing, searching and opening the right page come first.
2. **Leave the files where they are.** Bibliotaph reads files and never renames, moves, edits or deletes them.
3. **Automate routine cataloging.** The user reviews exceptions, not every book.
4. **Show the evidence.** Suggested, confirmed and unknown values look different, and every suggestion says where it came from.
5. **Preserve the user's work.** Reading a book again never erases corrections, notes, collections or binders.
6. **Make partial success useful.** A book is searchable as soon as its text is in.
7. **Keep operation simple.** Closing the app stops its work; opening it again carries on.

## Core concepts

- **Library folder**: a folder of original files the user has added, with everything under it.
- **Book**, or card: one thing the library lists. Identical files are one book with several **Copies**; a changed file is a new version of the same book.
- **Collection**: a hand-picked, nestable group of books. A book can be in any number.
- **Saved search**: a search with its filters and order, run again each time it opens.
- **Binder**: the books and page ranges for a session, in order, with notes.
- **Pin**: a sidebar shortcut to a book, Favorites, a collection, a binder or a saved search.
- **Suggestion**: a value with its source and evidence, until the user keeps or rejects it.

"Adventures" and "Maps" are filters, not shelves: a sourcebook with maps is still a sourcebook, and turns up when looking for maps.

## Main journeys

- **First run**: add folders and start browsing at once; books appear as they are read, and Home asks **Fill in the catalog with AI?** Nothing has to be organized first.
- **Find an adventure**: filter by system, type, level and players, add a word, open the right page, add it to a binder; or **Ask** in plain English.
- **Look something up**: ask the library a question and get an answer citing the book and page, one click from the page itself.
- **Find something inside a book**: search a phrase and open the matching page, with the words highlighted.
- **Add a bundle**: copy it into a library folder; files already owned are recognized. **Check a download** says what's new before anything is copied. It, **Add a book I own elsewhere…** and **Import from an online library…** share the Library's **Add** button, beside **Add folder**.
- **Prepare and run a session**: gather books and page ranges in a binder and step through them at the table.

## Screens

The sidebar has **Home**, **Library**, **Collections**, **Binders**, **Needs review** (with a count), the **Pinned** group and **Settings**. Its button beside Settings, or **Ctrl+B**, folds it to its icons, and it stays as it was left. The search box is always at the top (**Ctrl+K**).

**Home** shows **Continue preparing** (the next binder), **Recently opened**, **Recently added** and the pins. **Settings > Library > Start on** can open the Library instead.

**Library** shows every book, map and handout as covers or a list. A click shows a book's details on the right, and a double-click opens it. A cover shows its title and only the status that matters: a heart, a spark once AI has read it, a count of adventures inside, a badge saying where a book owned elsewhere lives. A list row shows the game system, type, levels and players ("4–6 players"), and the details panel's header shows levels and players beside the system. Lists are in name order: filter choices, collections, binders within each group and the **Add to collection** menu, while the sidebar's pins keep the order they were dragged into. The toolbar's order is remembered across restarts, one for browsing and one for searching. **Packs** and **Each file** beside covers and list switch between a card per pack and a card per file inside packs, remembered too and the same while browsing and searching: a file's card has its own title, cover and format, says "in" its pack, opens in its pack, and its pages are found under it, while it is filtered and found as its pack (and by its own name), and its details, heart, collections and Select mode are the pack's; **Add to binder** adds the file alone. **Select** mode edits, groups, joins or exports many books at once; bulk editing shows mixed values as mixed and previews before saving. Almost every change in the app has **Undo**.

**Collections** gathers books around a campaign, setting or idea. Collections nest, a book in an inner one counts in the outer one (**Only books added here** hides those), and a collection opens in the Library, ready to search and filter. Deleting one never removes a book. Needs review offers to make a folder of three or more books a collection; one made that way gains the books that land in its folder later, until the user adds or removes a book by hand. It also suggests books for a collection that share its series, or its setting and system, with most of its books (at least three, and 60%).

## Search

The box takes words, quoted phrases, `OR`, `-word` and `word*`, mixed with fields such as `type:adventure level:3`. There is a field for every detail Bibliotaph keeps, plus `favorite:`, `collection:`, `folder:`, `format:` and `where:`; any field takes `unknown`. The **search guide** under the box explains each field and lists its values with book counts.

Results have two tabs, **Documents** and **Inside documents**. Snippets are always the page's own text, and while books are still being read, results say how much is covered ("Searching text in 70 of 84 documents.").

**Filters** offers **Game system**, **Type**, **Character level** and **Players**, with the rest under **More filters**. **Setting**, **Themes**, **Environments**, **Publisher** and **Edition** are boxes to type in: they list the matching values with how many books have each, and a picked value becomes a chip. **Collection** shows the books in a collection, or those in none yet. Choices within a filter widen it; different filters narrow together. Active filters show as **filter chips** above the results, each with ×. A level matches books whose range contains it; books with unknown levels match only with **Include unknown levels**. Filters are never quietly relaxed.

**Save search** keeps the search, scope, filters, order and layout, not a list of books, and pins it.

**Ask** (the sparkle button, with AI set up) turns a request in plain English, such as "something creepy for four level-3 characters", into editable chips with notes on anything it changed and why. Nothing runs until **Search**, which puts the chips in the box as an ordinary search. Ask uses only the search box's own fields and the user's own words: it never adds a system, level or tag that wasn't asked for, and never fills in what isn't known.

**Answer from my books** (Ctrl+Enter in Ask) answers a question from the pages of the whole library, or of the books the Library's filters and the search box's fields leave showing. Bibliotaph finds the best pages across those books by their words and, as the library is read for meaning, by meaning, takes no more than three from any one book, and the AI answers from them only. Each claim cites its book and page as a link that opens the book there; quotations are checked, and when the books don't cover the question it says so and names the closest pages. With an embedding model set, the whole library is read for meaning slowly in the background, pausing while the AI is in use and carrying on after a restart; Settings > AI shows how far it has got and can switch it off.

## Reading

Bibliotaph's reader opens a book where it was left, or at the matching page from a search. It has bookmarks, thumbnails, find, and text selection and copy (scanned pages included), and shows printed and PDF page numbers where they differ. E-books are read in the same reader, page by page (see Files and processing). PDF links work, and on an index or contents page without links, printed page numbers become links when the book's numbering can be worked out reliably. **Use this page as the cover** makes the page in view the book's cover in the Library, and reading the file again keeps it.

**Note** puts a note on a page or range; notes follow their pages into other copies and versions, and search finds books by their notes. **Pop out** and **Open in new window** give a book a window of its own, as many as wanted. Images open with zoom and pan, and the arrow keys step through an image pack; **Previous** and **Next** step through a pack with PDFs in it.

**Ask this book** (with AI set up, for a PDF with text or an e-book) answers questions from the open book. Bibliotaph finds the pages most likely to answer, by their words and, with an embedding model (built in or on a model server), by meaning, and the AI answers from those pages only. Each claim cites its page as a link, quotations are checked against the pages, anything unsupported is marked, and when the pages don't cover the question the answer says so and lists the closest pages. Follow-up questions work, and each book keeps its conversation.

## Binders

A binder holds a session's books and pages in order, with a title, an optional date and notes. Items are whole books, single pages or labelled page ranges ("Warehouse ambush"), optionally under sections such as **Adventure**, **Maps**, **Encounters** and **Rules**. A book can appear several times at different pages. Pages are added from search results or the reader's **Add page**, into the binder worked on last unless another is picked.

Opening a binder shows its items on the left, the reader in the middle and its notes on the right. **Next** and **Previous** open each item at its first page, with the next book opened ahead so it shows at once; **Back to this item's pages** returns after wandering, and the binder can **Pop out** to a second screen.

A binder marked **Keep at hand during games** holds rules and references; from any other binder, **At hand** opens its pages in a separate window. Binders are for the game master's own preparation, not a display for players.

## Cataloging

### What Bibliotaph records

| Field | Notes |
|---|---|
| Title, publisher, authors, series, year | From the file, its name or AI |
| Game system, edition | Editions stay distinct; an edition implies its system |
| Type | Adventure, rulebook, bestiary, setting, supplement, map pack, handout…; several allowed |
| Levels | A range within the game system; unknown and "no levels" differ |
| Players | A stated party size, such as 4–6; shown in the details of an adventure even before anyone has said |
| Duration | One-shot, short (2–3 sessions), long (4 or more) or campaign, only when stated |
| Style | The kind of adventure: heist, dungeon crawl, hexcrawl, investigation… |
| Themes, environments, setting | Tone and subject; where it happens; named settings |
| Your tags | The user's own labels, never filled by AI; a tag defined with its words is suggested for books whose pages use them |
| Also own | Where else it's owned: print, a virtual tabletop, an online library, with an optional link |
| Get it from | Where to get it again: a store, with an optional link, shared by every copy |

File facts (format, pages, files, copies) are shown, not edited. Unknown stays unknown: nothing is inferred from what a sampled page doesn't mention.

### Where values come from

Suggestions come from folder and file names that match a vocabulary term, the PDF's own information, a store named in a folder or watermark (only the store is kept, never a buyer's name), folder rules the user agreed to, the words of the user's own tags, and AI. The user's own values always win and are never replaced. A **Suggested** mark opens to show a value's source, page and quote, with **Keep this**, **Use this**, **Not right** and **Reset to suggestion**.

**Cataloged wrong…** shows every field with each guess marked **Guessed** and its evidence open. After a fix, Bibliotaph offers to fix the same guess on look-alike books in that folder; after the same value is set on three books in a folder, it offers a folder rule.

**Settings > Vocabulary** holds the terms books are filed under, each with other names ("5e" for an edition, say), which folder names, searches, AI and typing all go through.

**Your tags** there are the user's own, each with the words that signal it ("Undead": undead, zombie, lich). Saving one looks for the words in the pages already indexed, without reading any file again, and suggests the tag for each book whose pages use them on at least 3 pages and at least one page in 20, so a passing mention or a few pages of a thick rulebook don't count. New books are checked once their text is in. Changing a tag looks again, deleting one takes its suggestions away, and the user's own decisions win: a kept tag stays, and a rejected one never comes back to that book.

### Needs review

Review never blocks browsing. **Metadata suggestions** holds disagreements, missing titles, new terms, possible new versions and image packs, possible files for books owned elsewhere, moved pages, folder rules on offer, proposed adventures, folders that could be collections and books that could join one. **Files needing attention** holds files that couldn't be read, grouped by reason, with a way forward for each.

A card shows the current value, the suggestion and its evidence. **Show** narrows the list to one kind of card, or to the suggestions for one field, with a count for each. Every decision has **Undo**, and a rejected value is never suggested again. A card that offers to make something (a collection from a folder, books for a collection, an image pack, a folder rule, a file's adventures) has a **×** that gives its "don't ask again" answer, so it closes like any other decision and that same offer isn't made again. **Settings > Needs review** chooses between **When unsure** and **All suggestions**.

Each card's book can open in a window beside the page (its window button, or middle-click or **Shift+Enter** on the title), at the evidence's page, so it can be checked before deciding. The cards share that one window: the next book opened shows in it, and a new one opens only once it is closed.

### Copies, versions and packs

Files with the same text on every page, such as copies stamped for different buyers, become one book; **Make current** chooses which opens and **Not the same book** splits one off. A file sharing most of its pages, or its title and publisher, with a book is asked about as a new version: nothing is joined without asking unless the text is identical. **Same book** joins cards by hand.

A folder or ZIP of 20 or more images and no PDFs becomes one image pack; a smaller set is offered in Needs review. **Split into separate images** undoes it, and **Pack together** makes one by hand. Packs made by hand can hold PDFs too, such as character sheets and pregens: the card reads "Pack · 6 files", opening it steps through its files (PDFs open as PDFs), their pages are found under the pack, and **Split into separate files** gives each its card back. Only images are ever packed automatically. A pack's cover is a mosaic of its first four files, or one file the user picked with **Use as the pack's cover** (from the file's menu in the details, or from the reader while stepping through the pack); **Use the mosaic** goes back, and so does the card by itself if that file leaves the pack.

### Adventures inside a file

An anthology can hold several adventures, each an entry of its own: its own details, its first page as cover when shown as a card of its own, and the file and pages it is in. It takes the file's system, edition, publisher, series, year and setting unless it has its own. The file is never split or changed. The user marks adventures from the reader, a bookmark or the details; the blurb on an adventure's first two pages ("for four to six characters of 3rd level") then suggests its levels and players, quoting it, and never over values the user set.

Bibliotaph also proposes adventures, but only when the file says in its own words, before its first adventure or on its back cover, that it is a collection of separate adventures ("a collection of five adventures", "each can be run on its own", "six one-shots"). A campaign whose chapters each carry a level blurb isn't proposed. The bookmarks and the blurbs on their first pages ("for four characters of 3rd level") show where each adventure starts, and with AI on the model helps find them, quoting where the book calls itself a collection and each adventure's own pages. A proposal never makes cards by itself: Needs review shows "This file looks like it holds 5 adventures." with the book's words and their page, a ticked list with each adventure's pages, levels and players as its blurb gives them, **Accept** and **Not split**, and remembers the answer. Accepting suggests those levels and players on each new card, with the blurb as evidence. With AI on, each adventure is then read on its own pages.

An adventure opens its file at its first page, says "page 3 of 19 of this adventure", and limits Find to it unless **Whole file** is ticked. Browsing keeps adventures inside their book: the file's card counts them and its details list them. A search shows each adventure that matches as a card with the file's cover, an Adventure ribbon and its own title, in place of the file when both match; page hits are listed under the adventure they fall in. **Adventures inside files** in More filters shows them as cards of their own instead, or hides them, searches included; a saved search that hid them still does. **Remove adventure** in its details, or **Remove adventures** for several ticked in Select mode, takes the cards away with Undo.

### Books owned elsewhere

**Add a book I own elsewhere…** makes a card with no file, for a book in print or on a virtual tabletop, so it turns up in searches, collections and binders. **Choose a cover image…** gives it a cover from a picture of the user's own, kept as a copy on this PC. **Remove from library** is in its details and on its right-click menu. When a file with its title arrives, Needs review asks "Is this its file?".

## Online libraries

Books owned online join the library as books owned elsewhere:

- **D&D Beyond** and **Demiplane**: books read on the service's site, recorded under **Also own** with their link, and opened in Bibliotaph's online reader.
- **Humble Bundle** and **DriveThruRPG** (DMs Guild included): books bought to download, listed under **Get it from** with their link until the file is downloaded.

**Import from an online library…** opens each service in Bibliotaph's own browser (WebView2), where the user signs in on the service's own page; Bibliotaph never sees the password. The browser opens the library page and then only does what the user does. **Read this page** shows a preview (**New**, **Already in your library**, **Imported before**), and nothing is imported until **Import**, which has **Undo**. Only owned books are imported, only empty fields are filled, and covers are only pictures the page already showed. **Import a saved page…** reads a page saved from the everyday browser instead. Nothing is stored, fetched or scraped automatically, and **Sign out of all services** forgets every sign-in.

The **online reader** shows the service's own page inside Bibliotaph, signed in through the same browser, with **Add this page** for binders and **Reading mode** to hide D&D Beyond's menus and ads. Links off the book's site open in the everyday browser. It copies no text from the page, so it has no find, page notes or Ask. **Read books owned online in Bibliotaph** can send these books to the everyday browser instead.

A **Downloads** folder inside a library folder receives files the user downloads with a store's own **Download** button. Each is read like any new file, and Needs review offers it to the matching book owned elsewhere.

## Files and processing

Bibliotaph reads PDF, EPUB, Kindle (MOBI, AZW, AZW3), JPG, PNG and WebP files, including inside ZIPs, which are never unpacked. Files are known by their content: a moved or renamed file keeps its card and everything attached to it, and a deleted one stays, marked missing. A folder on an unplugged drive shows as offline and its books stay searchable; opening one names the drive, with **Try again**. Network folders are best effort.

An EPUB is cut into pages so that search hits, AI evidence, notes and binders can point at a page: the book's printed pages when it marks them (its page list or page-break markers), else a new page at each chapter and about 2,500 characters a page, ended between paragraphs or before a heading. A fixed-layout EPUB has a page per spine item. The cut depends only on the file, so the same file always has the same pages. Its title, authors, publisher, subjects and description become suggestions as a PDF's own information does, its cover is the image the book names, and its contents are its bookmarks. A DRM-protected EPUB keeps a card with what its package says, with **Open in another app**; its text isn't read. The reader shows an e-book as one flowing page in the app's colours, with the book's pictures; the page box, contents, Find, notes, binders, Ask this book, search's marked words, links with Back and zoom work as in a PDF, and a fixed-layout book shows each page as drawn. The book's scripts, forms and web addresses never run or load.

Kindle books (MOBI and AZW, including ones holding a KF8 version, and AZW3) are read the same way: their text is rebuilt from the file's records into chapters, with their contents, pictures, stylesheets and links, and cut into pages by length, since Kindle keeps printed page numbers in a separate file. A Kindle book with DRM keeps a card with what its header says, and a KFX book (Amazon's newer format) is listed in Files needing attention; neither's text is read. `format:kindle` finds them all, as do `format:mobi`, `format:azw`, `format:azw3` and `format:kfx`. `format:epub` and `format:ebook` find them, and the Books filter counts them as books.

When a file's content changes, it becomes the new version of its book. Binder items and page notes follow their pages into it by the pages' text; one that can't be found opens at the same printed page marked "This page changed: check it", with **Use this page**. Bibliotaph never silently jumps to an unrelated page.

Processing runs in steps (finding files, text, covers, hints from names, text recognition on scanned pages, matching copies, AI), each with its own status. The bottom of the sidebar shows the current step in at most two lines, with the whole of it in its tooltip, and opens Settings > Processing. Indexing, text recognition and AI pause separately. A damaged file fails alone, without stopping the queue. **Reprocess** reads a file again, keeping the user's work, and can recognize text on every page of a bad scan.

OneDrive's online-only files are indexed too, local files first, pausing while a download would leave less than 2 GB free. A file Bibliotaph downloaded is returned to online-only once read, so a cloud library bigger than the disk can be indexed; only the local copy goes, and a setting turns this off.

## Passwords and protection

A PDF with a password keeps its card and asks for the password when opened. **Remember it on this PC** (off by default) keeps it in Windows Credential Manager, and **Make its text searchable** (on by default) lets Bibliotaph index it. A PDF that forbids copying is indexed and read, with copy off. A DRM-protected or unreadable book keeps a card the user can fill in, with **Open in another app**.

Bibliotaph never guesses passwords, removes DRM or bypasses permissions. Passwords and keys never go in the catalog, logs, backups, exports or prompts. **Forget its text** removes a book's pages, search text, cover, AI results and Ask conversation and stops reading it, keeping the card and the user's work; earlier backups may still hold that text.

## AI and privacy

AI is optional. With it, a model reads a bounded excerpt of each book (opening pages, contents, introduction, back cover and a sample of the rest) and suggests every field except tags, Also own and Get it from. The model runs either **Built in**, downloaded once and run on the PC with nothing sent anywhere (chosen from a list of pinned, hash-checked models grouped by whether they fit the graphics card: Qwen3 4B, 8B, 14B and 30B-A3B, Phi-4 mini, Llama 3.1 8B, Granite 3.3 8B and Mistral Small 3.2 24B, or a `.gguf` file already on the PC, used where it is), or on a **model server** the user runs, such as Ollama or LM Studio. Built in, **Download the embedding model** (nomic-embed-text, about 150 MB) lets Ask find pages by meaning; on a server, any embedding model it has does the same. A server off this computer's network needs **Send excerpts to this server** ticked, and Bibliotaph never switches to a cloud service by itself.

Every AI value quotes the page it came from, and a quote that isn't on that page throws the value away. Book text is data, never instructions: it can't change a setting or a decision, and an answer can only fill in suggestions. The model sees a few books the user has already sorted out as examples, never watermark lines, and logs hold counts, never book text. Every value and quote the model writes has a length limit, so a page of bare names (a map's labels) can't keep it writing until its answer runs out; an answer that still runs out keeps the values the model finished, checked as usual, and one that finished nothing fails that book alone with "The model's answer was cut off".

**Scan whole books** (on by default) has the AI read every page once a book is catalogued, a few chapters at a time, from the book's bookmarks (an e-book's contents), else its printed contents page, else runs of about 25 pages. It notes each chapter's setting, environments, themes and creatures with their pages, under the same quote check, and never changes the book's own details: the details panel shows them under **Found inside the book**, with **Scan again**, and the reader's contents beside each chapter. `setting:`, `environment:` and `theme:` also match chapters, and `creature:` searches chapter creatures. The scan runs only while nothing else uses the AI, stops the moment the user asks it anything, resumes where it stopped after a restart, and is kept by file content, so it survives a rebuilt index, is redone only when the file changes or the user asks, and isn't synced.

**Library folders sent to AI** keeps a folder away from the model. **See what the AI is reading** shows what the check kept and threw away. Changing the model never re-reads books unless the user asks (**Reclassify**). If the model stops answering, AI waits and everything else carries on.

There is no account, tracking or cloud service. Bibliotaph goes online only for updates, AI model downloads, a model server the user set up, and online libraries.

## Your data

The catalog lives on the PC, apart from the books. The user's work (corrections, notes, collections, binders, saved searches, pins) is kept separate from what can be rebuilt (text, covers, search index), so a rebuild never touches it.

- **Backups**: made on demand (saved where the user picks, starting in the backups folder) and weekly (the last four kept), without books, passwords, keys or sign-ins; optionally with the search index. Restoring lets moved folders be pointed to their new place.
- **Sync with another PC**: each PC writes its own small file to a folder the user picks, such as one in OneDrive, and merges the other's. The user's work travels; folders, reading places, settings and sign-ins don't. Books are matched by content, the newer change wins, and deletions carry across. There is no sync service, and the catalog itself never goes in that folder.
- **Export**: CSV (a row per book) and JSON (the whole catalog).

A catalog upgrade backs up first. Updates download in the background and go in when the user clicks **Update ready: restart to update**, or at the next start; nothing restarts by itself.

## Design and accessibility

The look comes from [bibliotaph-ui-mockups.html](bibliotaph-ui-mockups.html): a contemporary media library with readable type, generous cover space, a restrained green accent and quiet chrome, in light and dark themes that follow Windows unless changed. No faux parchment or fantasy lettering on controls. Title and cover come first, then system and type, then details.

Everything works from the keyboard, with visible focus, named controls for screen readers, no hover-only actions, layouts that hold at 200% scaling, Windows contrast themes, and no animation when Windows turns animations off. **Ctrl+/** lists every shortcut.

## On the web

[bibliotaph.online](https://bibliotaph.online) is the web version being built for libraries kept in cloud storage, with a server that holds each user's catalog and index and never a copy of their books. It looks like the Windows app: the same sidebar, type, colours and icons, in light and dark. It is invite-only. So far it has accounts and invitations: a one-time link that expires after 14 days makes an account, signed in through Microsoft or Google (name, email and an account ID only), and more sign-ins can be linked to the same account. Admins make invitations in Settings. OneDrive connects in Settings › Storage (read-only access to files, plus Bibliotaph's own app folder), several drives per account, with a Connect again prompt when Microsoft stops allowing it. Library folders are picked from a connected drive in Settings › Library folders, with the Windows app's wording; they are scanned at once, each new file is downloaded to a temporary file to be recognised and deleted straight after, OneDrive's change notifications bring changes within minutes, every folder is looked through twice a day, and Look for changes does it now. The sidebar shows what the library is doing. The server then reads each book as the Windows app does: page count, labels and outline, its text for search, its cover (PDFs, e-books and images), and hints from file and folder names, with PDFium in a separate worker process; a book is downloaded once for this, its copy deleted when its stages are done. The Library page then shows the books as the Windows app does: covers or a list with the same card text and marks, Packs or Each file, the same orders, the filter panel with its counted menus and chips, and search from the top bar with the Documents and Inside documents tabs, the matching words marked; the layout, order and pack view are remembered in the browser, and a search and its filters are in the page's address. Book details and editing, the reader, and OCR and AI with the user's own key follow, and then the Windows app can use the same library. Its [privacy policy](https://bibliotaph.online/privacy) is published with it.

## Out of scope

Bibliotaph is a Windows app for Windows 10 (1809 or later) and 11, with no phone, tablet, macOS or Linux version; the web version below is still being built. The Windows app has no sync service, accounts, player-facing display, virtual tabletop connection, campaign authoring, character sheets, ebook conversion, plugins, background service or tray mode, and no store or recommendations. It never changes source files, never removes DRM, and never reads an online library on its own.
