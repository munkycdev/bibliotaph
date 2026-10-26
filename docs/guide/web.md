# Bibliotaph on the web

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Bibliotaph on the web

Bibliotaph is coming to the browser at [bibliotaph.online](https://bibliotaph.online), for game masters whose books
live in cloud storage. It looks and works like the Windows app. It's being built in steps and is invite-only: today you
can accept an invitation, sign in, invite others, connect your OneDrive and add library folders from it. Seeing and
reading your books in the browser comes next.

## Join with an invitation

An invitation is a one-time link that expires after 14 days. Open it and choose **Continue**. Bibliotaph's sign-in
page, run by Auth0, offers Google, Microsoft or your email address; sign in with the one you'll use each time. That
makes your Bibliotaph account; the link can't be used again. Bibliotaph receives only your name, email address and an
account ID, and never sees your password.

Without an invitation, signing in with an account Bibliotaph doesn't know shows **Bibliotaph is invite-only for now.**

## Signing in and out

Sign in at [bibliotaph.online](https://bibliotaph.online) with **Continue** and the same account each time. You stay
signed in on that browser for two weeks after you last used it. **Settings** › **Sign out** ends it, and signs you out
of the sign-in page too, so the next person on that browser has to sign in themselves.

To sign in another way as well, such as Google beside Microsoft, choose **Link another sign-in** in **Settings** ›
**Account** and sign in the other way. Either one then opens the same library, and **Account** lists each by name. A
sign-in that already belongs to another Bibliotaph account can't be linked.

## Inviting others

If your account can invite, **Settings** has **Invitations**. Type who it's for (only you see this), tick **Can invite
others** if they should be able to invite too, and choose **New invitation**. Copy the link straight away and send it
yourself: it's shown only once. The list below shows each invitation and whether it has been used.

## Connecting your OneDrive

Bibliotaph reads your books where they are, so first it needs your storage. In **Settings** › **Storage**, choose
**Connect OneDrive** and sign in to Microsoft with the account whose OneDrive holds your books (it doesn't have to be the
one you sign in to Bibliotaph with). Microsoft asks you to let Bibliotaph read your files and keep access while you're
away; Bibliotaph can't change, move or delete any of them. It can write only to a folder of its own in your OneDrive,
which is where books you import will go.

You can connect more than one OneDrive. If Microsoft stops letting Bibliotaph in, for example after you change your
password or remove its access, the drive shows **Needs connecting again** with a **Connect again** button. Its books stay
in your library meanwhile. The **×** beside a drive disconnects it: Bibliotaph forgets its access, and your notes,
collections and corrections stay for when you connect it again.

## Library folders

**Settings** › **Library folders** works as in the Windows app. Choose **Add a folder** (or **Add a folder** on Home),
walk down to the folder you want, and choose **Use this folder**. Folders already in your library are marked. The new
folder shows what's directly in it; choose **Add to library** to keep it or **Cancel**. Each folder then says when it was
added and what it holds, such as "488 PDFs, 12 e-books, 120 images · 15 other files ignored". The **×** removes a folder
from your library without touching its files.

Bibliotaph looks through a new folder straight away, then reads each new file to recognise it. Your books aren't
copied: each is downloaded only while it's being read and deleted straight after. The line at the bottom of the sidebar
says what it's doing, such as **Reading 120 new files** or **Up to date**.

Changes in your OneDrive (books added, moved or deleted) are picked up within a few minutes, whether or not you have
Bibliotaph open, and twice a day it looks through every folder anyway. **Look for changes** looks straight away.

Each book is then read as the Windows app reads it: its pages, outline and text for search, its cover, and hints from
its file and folder names. Bibliotaph downloads a book once for this and deletes its copy as soon as it's done; the
sidebar shows **Reading 120 new files** and then **Up to date**. Scanned pages that need OCR, and AI suggestions, aren't
read on the web yet.

## Your library and search

**Library** shows your books as the Windows app does, with their covers, or as a list with the **List** button beside
**Covers**. Each card says what the book is ("D&D 5e · Adventure", or "PDF · 96 pages" until it's catalogued), and marks
a book owned elsewhere, a locked or protected file, a file that's missing, a favourite and a book read by AI. The
**Packs** and **Each file** buttons show a pack as one card or each of its files on its own. Order the books with the
menu beside **Filters**: **Recently added**, **Recently opened**, **Title A–Z** or **Publisher A–Z**. The browser
remembers the layout, the order and the pack view.

**Filters** opens the Windows app's filter panel: game system, type, character level (with **Include unknown levels**)
and players, then under **More filters** the style and length of an adventure, the book's setting, themes,
environments, publisher and edition, the source folder, where else you own a book, where to get it, and housekeeping
(kind, AI, copies, adventures inside files, collections). Each menu says how many books it would show. Setting, themes,
environments, publisher and edition are boxes to type in: they list the matching values with how many books have each
(the arrow keys move through them, **Enter** picks one), and a picked value becomes a chip under its box. Values picked
in one box widen it; different filters narrow together. The filters that are on show as chips under the toolbar, each
with an **×**.

Type in the search box at the top to search your library; a pause in typing searches, **Enter** searches at once and
**Esc** clears it. Results come in two tabs, as in the Windows app: **Documents**, the books that match, and **Inside
documents**, the pages that match with the words marked. The same search words work: quotes for a phrase, `-` to leave a
word out, and fields such as `system:` and `type:`. A search and its filters are in the page's address, so you can
bookmark them.

Click a card, a list row or a matching book's title to open its details over the page, as in the Windows app: its cover
and what it is, then **Details**, each field with where its value came from (**Sources**, **Suggested** or **Sources
disagree** opens the evidence and the other suggestions; **More fields** shows the empty ones), the adventures marked in
a file or the file an adventure is in, a pack's files, **About the file**, **Where it is** in your storage, its copies
when it has more than one, and how far reading it has got. **Esc**, the **×** or a click outside closes them.

**Home** greets you with the books you opened last and those added last, a cover each; a click opens the book's details
in the Library.

Covers are kept with your account, so they don't need drawing again when the server restarts.

### Changing a book's details

The details change a book as the Windows app's do:

- **The heart** beside **×** adds the book to your favorites, or takes it out.
- **The pencil** on a field edits it: type the value, a list separated with commas, and **Save** (or **Enter**; **Esc**
  cancels). A field with a list of terms, such as Game system or Type, says how what you typed will be filed and lets
  you pick from the list.
- **The evidence** a field's **Sources** or **Suggested** opens has **Keep this**, **Use this** on another suggestion,
  **Not right** on any value, and **Reset to suggestion** to forget what you set.
- After a correction, Bibliotaph may offer to correct the other books from the same folder that showed the same thing:
  **Change them** does, with **Undo**; **No thanks** leaves them.
- **The pencil under the cover** is **Cataloged wrong…**: every field shows, the guessed values are marked **Guessed**
  with their evidence open, and **Done** ends it. If you leave without changing or keeping anything, Needs review keeps
  a reminder.
- **Notes** is the book's own note, saved as you type and found by search, with the notes on its pages below.
- **Adventures in this file**: **Add** gives pages of a PDF a card of their own, typed as the file numbers them; each
  has **Edit range** and **Remove**, with **Undo**. An adventure's own details have **Show the file**, **Edit range** and
  **Remove adventure**.
- **Reprocess**, under **Where it is**, reads the file again; what you added stays.
- A pack's files: click one for **Use as the pack's cover**, or **Use the mosaic**; **Split into separate images** (or
  files) gives each its own card, with **Undo**.
- A book in more than one file: **Make current** chooses which copy opens, and **Not the same book** gives a file its own
  card again.

### Books you own elsewhere

A book you own in print or on a virtual tabletop can have a card too, so it turns up in searches, collections and
binders with the rest. **Add ▾** › **Add a book I own elsewhere…** in the Library asks for its title and, if you like,
its game system, type, publisher and where you own it: tick the places, or type somewhere else or the book's link
online. **Choose a cover image…** picks a picture of your own as its cover; a picture that can't be one says so before
the book is added. **Add to library** makes the card and opens its details for everything else.

In its details, **Choose a cover image…** changes the cover, **Remove the cover** stops using the picture you chose,
and **Remove from library** takes the book out, with **Undo**. The covers you choose are kept with your account, so a
new version of the server keeps them. When a file with its title arrives, Needs review asks whether it's this book's
file.

### Collections

**Collections** gathers books around a campaign, a setting or an idea, as in the Windows app. Its page has a card for
each collection at the top, pinned ones first; **New collection** makes one and opens it.

A collection opens in the Library with its name as the heading and **In** its name on a chip above the books (its **×**
shows your whole library, keeping the search and filters). The search box and filters work within it. Collections
inside it show as cards above its books, and **Only books added here** leaves out the books that are only in those.
Its buttons are **New collection** (one inside it), **Rename**, **Move to…** (into another collection, or to the top)
and the bin, which deletes it: its books stay in your library, the collections inside it move up a level, and
**Undo** on Collections brings it back. **Pin** puts it in the sidebar and on Home.

A book's details list its collections under **Collections**: a chip opens one, and its **×** takes the book out.
**Add to collection** offers the collections used last, **Add to a new collection…**, and **Add to another
collection…**, which lists them all and narrows as you type. Each change says what it did, with **Undo**.

### Favorites, saved searches and pins

**Favorites** in the sidebar shows the books you marked with a heart, with **In Favorites** on a chip above them (its
**×** shows your whole library). It starts pinned; its **Pin** button takes it out of the sidebar or puts it back.

**Save search** in the Library's toolbar, while you search, filter or look inside a collection or Favorites, keeps
what's shown: the search, the filters, where it looks, the order and covers or list. Name it and it opens as a page of
its own, pinned in the sidebar, and it runs again each time, so new books show up in it. Change its search, filters,
order or layout and it says so, with **Update saved search** to keep the change and **Save as new** to keep both. Its
buttons are **Rename**, **Pin** or **Unpin**, and the bin, which deletes it without touching a book, with **Undo**.
**Saved searches** in the toolbar lists them all, pinned or not, each with its pin, rename and delete. A saved search
made in the Windows app opens here too, including one saved in a binder.

The sidebar's **Pinned** group, also on Home under **Pinned**, holds your pinned collections, binders, saved searches,
books and Favorites. Drag one to move it, or use Alt+Up and Alt+Down; right-click one for **Move up**, **Move down**,
**Unpin**, and for a saved search **Rename…** and **Delete**. A book's details have a pin button too. A pinned book
whose file can't be opened right now is dimmed.

### Select mode

**Select** in the Library's toolbar ticks books instead of opening them, as in the Windows app. Click a book to tick
it, **Shift**+click to tick a run of them, and **Ctrl+A** to tick every book shown; **Esc** or **Done** leaves Select
mode and **Clear** unticks them all. The Select bar says how many are ticked and offers what fits them:

- **Edit metadata** changes fields on every ticked book at once (see below).
- **Add to collection** adds them to a collection, and inside a collection **Remove from collection** takes them out.
- **Add to binder** adds them to a binder (see [Binders](#binders)).
- **Same book** joins cards that are copies or versions of one book; you choose which file opens.
- **Pack together** makes a pack of images or PDFs, adds them to a pack, or merges packs, under a name you can change.
- **Remove adventures** removes ticked adventures marked inside files; their pages are only their files' again.
- **Export these** saves the ticked books as a CSV file for a spreadsheet, with the Windows app's columns.

Each change says what it did, with **Undo**.

**Edit metadata** shows every field but the title with what the ticked books have in it: the value they share, "Mixed
(3 values)" with how many have each, or "Not set". The pencil sets one value on every book, typed or picked from the
list, and **Back to suggestions** forgets what you set yourself on all of them. A field with several values, such as
Type or Your tags, shows each value as a chip with how many of the books have it: click a chip to put it on every
book, its **×** to take it off every book, or type values (separated by commas) and **Add** them. Fields you don't
touch stay as they are. **Review changes** says what will happen before anything is saved, and warns where it
replaces values you set yourself; **Apply** saves it all at once, and the note's **Undo** puts every field back. The
books stay ticked, so another change can follow.

### Binders

**Binders** in the sidebar shows your binders as cards, each with its date, how many items it holds and its first
books' covers, in groups as in the Windows app: **Upcoming**, **At hand**, **No date yet**, and **Show earlier
binders** for those whose date has passed. Home's **Continue preparing** shows the binder you worked on last.
**New binder** asks for a title and, if you like, the date it's played, or **Keep at hand during games** for rules and
references, for one game system or any. The binder opens at once.

A binder's page has its date above its title, and the pencil (**Edit title and date**), **Duplicate** and the bin,
which deletes the binder but never its books, with **Undo**. **Binder sequence** lists its items in order, numbered,
each with its cover, its label, which book and pages it is, and your note:

- Click the label or **Add a note** to type; Enter or leaving the box saves it. A blank label shows the book's title.
- Drag an item by its grip, use its arrows or press **Alt+Up** and **Alt+Down** to move it. Dropped on another item it
  takes that item's place; dropped on a section's heading it goes first under it.
- Right-click an item for **Move up**, **Move down**, **Move to** each section and **Remove from binder**; its **×**
  or the **Delete** key removes it too, with **Undo**.
- **Add section** offers Adventure, Maps, Encounters and Rules, or **Another name…**. A section's pencil renames it and
  its bin deletes the heading, moving its items up to the section above.
- An item whose file can't be reached is dimmed and says why.

**Binder notes**, on the right, are saved as you type. **Show these books in the Library** shows the binder's books
there, with **In** and the binder's name on a chip; **Find resources** opens the Library. Binders are the same ones
the Windows app shows.

**Add to binder** adds books as in the Windows app: the notebook button in a book's details adds that book (a file
in a pack shown on its own card goes in on its own), and **Add to binder** in the Select bar adds every ticked book.
Its menu leads with the binder you worked on last, then **Add to another binder…**, which lists them all and narrows as
you type, and **Add to a new binder…**, which makes one first. While searching, the **+** beside a matching page on the
**Pages** tab adds that page to the binder you worked on last. Each says what it added, with **Undo**. Opening a
binder's items in a reader comes next.

### Needs review

**Needs review** in the sidebar has a count of what's waiting, as in the Windows app, and its page, *A little
clarification.*, has the same two tabs.

**Metadata suggestions** lists new terms first, then a card for each field of each book that needs a second look:
sources that disagree, a title that looks like a file's name, or, with **All suggestions** chosen in the Windows app's
settings, every value nobody has confirmed. A card shows what the book has now, the suggestion and where it came
from, with:

- **Accept**, which makes the suggestion the book's value, and **Accept all**, which accepts every card that suggests
  the same for the same field, shown or not. A line above the cards says how many, with **Undo all**.
- **Edit** (or **Type a title**), to type the value yourself, with the field's list to pick from and a line saying
  how what you type will be filed. Enter saves; Esc cancels.
- **Reject** (or **Keep as is**), which keeps what the book has. A rejected value is never suggested again.

A new term card offers **Add as a new** term, **Reject**, or a term the vocabulary has to **File under it**. Each
decided card folds to a line saying what happened, with **Undo**, until the page is opened again. **Show** narrows
the list to new terms or to one field's suggestions, with how many of each are waiting; 60 cards show at a time, with
**Show more** for the rest. The book's title opens its details in the Library. When the library's suggestions change
while you work, a line offers **Show the new list**.

The cards that ask one question come between the new terms and the field suggestions, as in the Windows app:

- **New version?**: a file that shares most of its pages, or its title and publisher, with a book you have. **Make it
  current**, **Keep as another copy**, or **Separate book**. **Open the book** shows the book it was matched with.
- **Image pack?**: a folder of images, with the first few shown. **Make a pack** or **Keep separate**.
- **Owned elsewhere**: a new file with the title of a book you own elsewhere. **Same book** puts the file on that
  book's card, or **Separate book**.
- **New version** (places to check): the session items and page notes whose pages weren't found in a new version of
  their book. **Use this page** takes the page suggested for each, with **Undo**; the card is done once every place is
  checked. Opening a place at its page comes with the reader.
- **Adventures?**: a file that looks like it holds several adventures, from its bookmarks and the blurbs on their
  first pages, or read by a model. Each adventure is ticked, with its pages and the levels and party its blurb gave;
  **Edit** turns the titles and pages into text boxes. **Make 3 cards** gives each ticked adventure a card of its own
  that opens its pages, without changing the file. **Not split** (or **×**) says it's one book.
- **Adventure in a file**: an adventure whose pages weren't found in its file as it is now, perhaps a new version.
  **Set its pages** types where it is now, as the file numbers its pages; **Remove adventure** takes its card away.
- **Cataloged wrong**: a book whose details you closed without a change after **Cataloged wrong…**. **Fix it** opens
  its details in the Library with the guesses marked again; **It's fine now** takes the card away.
- **Folder rule?**: the same value set on several books in one folder. **Use this rule** suggests it for every file
  there, or **No thanks**.
- **Collection?** and **Add to collection?**: a folder that could be a collection, or books that could join one.
  **Accept**, **Not now** (asked again next time the page opens) or **Never**.

The **×** on a pack, adventures, folder rule or collection card gives its "don't ask again" answer. Every answer has
**Undo**. Where a file or folder is, the card says so under its heading.

**Files needing attention** lists the files that couldn't be read, grouped by reason. **Try again** gives a file, or
every file on a card, another go, and **Try all again** does it for all of them apart from locked and protected
files. A card for a fault in Bibliotaph has **Copy details**, which copies the version and the error, without file
names, to paste into a bug report. Opening a locked file to unlock it comes with the reader.

## Reading a book

A book opens in the reader as in the Windows app: double-click it in the Library, or click it once and click **Open**
in its details. A matching page under **Inside documents** has **Open page**, which opens the book at that page with
the search's words marked. A book opened from the Library opens where you left it, and says so; an adventure inside a
file opens at its own first page and shows where you are in it ("page 3 of 19 of this adventure").

The pages are read straight from your OneDrive, a piece at a time, so a big book shows its first page in a moment; the
server never has a copy. What you've read is kept in your browser, so turning back to a page doesn't fetch it again.

- **Page box**: type a printed page number or a PDF page number, and press **Enter**. **Previous** and **Next** turn a
  page; the line under the pages says which printed and PDF page you're on.
- **Zoom**: the zoom buttons and menu, **Ctrl+Plus** and **Ctrl+Minus**, or **Ctrl** and the mouse wheel. **Ctrl+0**
  fits the page to the width again.
- **Contents**: the list button shows the book's bookmarks on the left, with what the AI found in each chapter when it
  has read the whole book. An adventure's own bookmarks stand out.
- **Page thumbnails**: the toolbar button shows a strip of small pages under the pages, the page you're on outlined.
  The browser remembers whether it was showing until you close it.
- **Links** in the PDF work, and **Back to page …** takes you back after following one.
- **Find**: **Ctrl+F**, then **Enter** or **F3** for the next match and **Shift+F3** for the one before. In an
  adventure, Find looks in its pages unless you tick **Whole file**.
- **Copy**: select text and press **Ctrl+C**. If a PDF doesn't allow copying, Bibliotaph says so.
- The **heart** and the **pin** make the book a favorite and pin it to the sidebar. **Pop out** moves the book to a
  window of its own, and **Return to main window** brings it back. **Open in new window** beside **Open** in a book's
  details, and beside a matching page, opens it in one straight away.

A PDF with a password asks for it each time it opens; the password isn't kept. A book owned elsewhere has nothing to
open, and a file inside a ZIP opens in the Windows app for now.

A picture opens on its own, fitted to the window; the zoom menu, **Ctrl+Plus**, **Ctrl+Minus** and **Ctrl** with the
mouse wheel show it larger, and **Ctrl+0** fits it again. A pack opens at its first file (or the file you opened), and
the arrows beside **1 of 6** step through its files: pictures, and PDFs at their first page. In a pack of pictures the
**Left** and **Right** arrow keys do the same, and each picture shows at the zoom of the one before.

An e-book (EPUB, MOBI, AZW or AZW3) opens as in the Windows reader: one long page of text in Bibliotaph's colours, light
or dark, with the book's own pictures, cut into the same pages search found, so **Open page** and the place you left
it open where they should. The page box, the contents, **Find** (Ctrl+F, F3 for the next match) and the search's
marked words work as they do in a PDF. **Zoom in** and **Zoom out** (or Ctrl+Plus, Ctrl+Minus and Ctrl with the mouse
wheel) make the text bigger or smaller, and **Usual size** sets it back. A link to another place in the book goes
there, with **Back to page …**; a web link opens in a new tab. A fixed-layout book, such as a comic, shows each page as
it was drawn, with **Fit page** and **Fit width**. As in the Windows app, anything in the book that would run a
program, fill in a form or reach the internet is left out, and the book is read in your browser: the server never
sees it. A book with DRM, or one in Amazon's KFX format, can't be opened.

### Books in this browser

The pages you read are kept in your browser, so a book you've opened before shows its pages without downloading them
again. Each browser keeps its own, and they're only ever a copy: letting one go never touches the file or anything
you've written about it.

- **Keep offline**: the drive button in the reader's toolbar downloads the whole book to this browser, showing how far
  it has got, so it opens and turns pages at once. Click it again to let the book go with the others. If the download
  stops, it carries on next time you open the book.
- **Settings → Books in this browser** lists the books kept here, how much room they take, and when you last read
  them. Set the room for books not kept offline (2 GB to start); past it, the books you read longest ago are let go
  first. A book kept offline is never let go for room. **×** lets one book go, and **Let go of books not kept
  offline** lets go of the rest.
- When you first keep a book offline, Bibliotaph asks the browser not to clear its books when the computer runs short
  of space. Some browsers agree only for a site you visit often or have bookmarked; until then, Settings says the
  browser may clear them.
- A private window can't keep books: each page downloads as you read it.

## Your theme

The button at the top right cycles between your device's theme, light and dark, as in the Windows app. The browser
remembers your choice.

## Privacy

Your books stay in your own storage. Bibliotaph keeps your account, your storage connections (encrypted), an index of your
library and the work you do in it. The [privacy policy](https://bibliotaph.online/privacy) says exactly what's kept,
where, and how to have it deleted.
