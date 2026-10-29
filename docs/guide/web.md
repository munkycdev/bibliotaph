# Bibliotaph on the web

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Bibliotaph on the web

Bibliotaph is coming to the browser at [bibliotaph.online](https://bibliotaph.online), for game masters whose books
live in cloud storage. It looks and works like the Windows app. It's being built in steps and is invite-only: today you
can accept an invitation, sign in, invite others, connect your OneDrive, Dropbox, Google Drive, Box or a WebDAV server such as Nextcloud and add library folders from it. Seeing and
reading your books in the browser comes next.

## Join with an invitation

An invitation is a one-time link that expires after 14 days. Open it and choose **Continue**. Bibliotaph's sign-in
page, at auth.munkyc.dev (MunkyC is the sign-in Bibliotaph shares with its maker's other apps) and run by Auth0,
offers the sign-ins it has switched on, such as Google, Microsoft, Discord or your email address; sign in with the one
you'll use each time. That
makes your Bibliotaph account; the link can't be used again. Bibliotaph receives only your name, email address and an
account ID, and never sees your password.

Without an invitation, signing in with an account Bibliotaph doesn't know shows **Bibliotaph is invite-only for now.**
If the service opens sign-up later, signing in makes your account straight away, until it has as many people as it
can take; then it shows **Bibliotaph is full for now.**, and an invitation still works.

## The tour

The first time you sign in, the same short tour as the Windows app's points at the parts of the page as you need
them. On an empty library it shows where to add your first folder, then, in **Settings > Library**, the storage to
connect and the folder picker, and once a folder is in, the sidebar's line about what Bibliotaph is doing. It picks up
where the account is, so coming back from a storage provider's sign-in page lands on the right step. The second
chapter waits until your first books show, then walks through them: a book's cover, the Library, books you own online,
the search box, Needs review, Collections and Binders, and Settings. **Next**, **Back** or the arrow keys move through
it, and **Skip tour** or **Esc** ends it for good. What a step points at can still be clicked.

To see it again, open **Settings > Preferences** and click **Show the tour again**. It's the same tour, and the same
record of how far you got, as the Windows app's.

## Settings

**Settings**, at the bottom of the sidebar, is laid out as in the Windows app: *Make yourself at home.*, with a list of
sections beside the one you're in, in the Windows app's groups: **BOOKS** holds **Library** (your storage and the
folders from it), **Online libraries** and **Devices** (your Windows PCs and the books kept in this browser);
**CATALOGUING** holds **Processing**, **AI** and **Vocabulary**; **YOU** holds **Preferences** (light or dark, where
Bibliotaph opens and what waits for review), **Passwords**, **Backup** and **Account**; and **ADMIN**, for those who can
invite, holds **Members** (invitations and accounts). Each page is headed as its entry is. The address names the section, such as
bibliotaph.online/settings#library, so Back returns to the last one; an older link to a section since folded into
another, such as #storage, opens the one it's in now.
**About Bibliotaph**, under the list, shows the version, the licence and links to the source code, problem reports,
the privacy policy, the terms and the change log. **Keyboard shortcuts**, under it, lists every key that works on the web, by where it works; Ctrl+/
shows the same list from anywhere. Ctrl+K, or / when you're not typing, puts the keyboard in the search box, and in a
reader popped out into a window of its own, Ctrl+K searches in the main window. The button beside **Settings**, or
Ctrl+B, folds the sidebar to its icons, each named when you point at it, as in the Windows app. Folded, the monogram
stays at the top and the same button, now under **Settings**, unfolds it; the browser remembers which you chose.

## Signing in and out

Sign in at [bibliotaph.online](https://bibliotaph.online) with **Continue** and the same account each time. You stay
signed in on that browser for two weeks after you last used it. **Settings** › **Sign out** ends it, and signs you out
of the sign-in page too, so the next person on that browser has to sign in themselves.

After Bibliotaph is updated or the server restarts, your library comes back from storage before it opens, which can
take a few minutes for a large one. The page says so while it waits, with how long it has taken, and opens your
library by itself when it's back. The server starts on the libraries of people who signed in lately a couple of
minutes after it starts, so yours is often back before you look.

To sign in another way as well, such as Google beside Microsoft, choose **Link another sign-in** in **Settings** ›
**Account** and sign in the other way. Either one then opens the same library, and **Account** lists each by name. A
sign-in that already belongs to another Bibliotaph account can't be linked.

**Close account…** in **Settings** › **Account** closes your account once you confirm. Bibliotaph deletes the account,
your library (its catalog, index and covers), everything you've done in it, your storage connections and the books
kept in that browser straight away, and its backups within 30 days, then signs you out everywhere. Nothing in your
storage is touched. It can't be undone: to come back you need a new invitation. Your sign-in with Google, Microsoft or
another service is theirs, so it stays.

## Inviting others

If your account can invite, **Settings** has **Members**. Type who it's for (only you see this), tick **Can invite
others** if they should be able to invite too, and choose **New invitation**. Copy the link and send it yourself. The
list below shows each invitation and whether it has been used: the copy button beside an open one puts its link on
the clipboard again, and the bin deletes an invitation. Deleting an open one makes its link stop working straight
away, so it asks first; deleting a used one only tidies the list, and the account made from it stays.

Under the invitations, **Accounts** lists everyone who has joined, with their email address, when they joined and when they last
signed in. The bin beside an account deletes it once you confirm: it's the same as its owner choosing **Close
account…**, so their library and everything done in it go, they're signed out in every browser, and nothing in their
storage is touched. It can't be undone. Your own account isn't deleted here: use **Close account…**.

## Connecting your storage

Bibliotaph reads your books where they are, so first it needs your storage. At the top of **Settings** › **Library**, under **Storage**, choose
**Connect OneDrive**, **Connect Dropbox**, **Connect Google Drive** or **Connect Box** and sign in with the account that holds your books
(it doesn't have to be the one you sign in to Bibliotaph with). Microsoft, Dropbox, Google or Box asks you to let Bibliotaph
read your files and keep access while you're away; Bibliotaph asks for nothing that writes, so it can't change, move or
delete any of them. Each account in the list says what it was allowed, such as "Allowed to read your files and keep
access while you're signed out"; point at that line to see the same in the provider's own words. A Dropbox that's part of a team shows
the team's folders as well as your own. Google may warn that it hasn't verified Bibliotaph yet: choose **Advanced**,
then go on to bibliotaph.online.

**Connect WebDAV** is for Nextcloud, ownCloud, a NAS or any other WebDAV server on the internet. It has no sign-in page
of its own, so type the address of the folder your books are in (or one above it), your user name and a password, and
choose **Connect**. Nextcloud and ownCloud show the address under Files › Settings › WebDAV, such as
https://cloud.example.com/remote.php/dav/files/you/. Make an app password for Bibliotaph where your server offers one (in
Nextcloud, Personal settings › Security › Create new app password), so you can take it back without changing your own.
The server must be reachable from the internet over https: Bibliotaph on the web can't reach a server on your home
network. The password is kept encrypted and never shown again, and Bibliotaph only reads with it. If the server stops
accepting it, the account shows **Needs connecting again**, and **Connect again** asks for it once more. A backslash in a
file's name shows as ∖.

Google Drive lets two files in one folder have the same name, and a name have a slash in it; Bibliotaph shows the
second of a name as "Handbook (2).pdf", and a slash as ∕. Google Docs, Sheets and shortcuts aren't books, so they're
left out.

You can connect more than one account, of either kind. If one stops letting Bibliotaph in, for example after you change
your password or remove its access, it shows **Needs connecting again** with a **Connect again** button. Its books stay
in your library meanwhile. The **×** beside an account disconnects it: Bibliotaph forgets its access, and your notes,
collections and corrections stay for when you connect it again.

## Library folders

**Library folders**, under your storage in **Settings** › **Library**, works as in the Windows app. Choose **Add a folder** (or **Add a folder** on Home),
walk down to the folder you want, and choose **Use this folder**. Folders already in your library are marked. The new
folder shows what's directly in it; choose **Add to library** to keep it or **Cancel**. Each folder then says when it was
added and what it holds, such as "488 PDFs, 12 e-books, 120 images · 15 other files ignored". The **×** removes a folder
from your library without touching its files, and its books' cards leave Needs review and its count with them.

Bibliotaph looks through a new folder straight away, then reads each new file to recognise it. Your books aren't
copied: each is downloaded only while it's being read and deleted straight after. The part of the server that reads
them is shut in with the books being read, so a damaged or booby-trapped file can't reach anyone's library. The line at the bottom of the sidebar
says what it's doing, such as **Reading 120 new files** or **Up to date**.

Changes in your storage (books added, moved or deleted) are picked up within a few minutes (Google Drive's, Box's and WebDAV's within five), whether or not you have
Bibliotaph open, and twice a day it looks through every folder anyway. Nextcloud and ownCloud mark a folder changed when
anything inside it changes; other WebDAV servers may not, and then their changes wait for that twice-daily look or **Look
for changes**, which looks straight away.

Below the folders, as in the Windows app, **Folder rules** lists the rules you answered in Needs review, such as
"Fey Court" → "system Pathfinder 2e", with where the folder is. Untick one to stop it suggesting its value; ticking it
again brings the suggestions back.

Each book is then read as the Windows app reads it: its pages, outline and text for search, its cover, and hints from
its file and folder names. Two files of the same book, such as copies bought by different people, become one card with
two copies, and a file that shares most of its pages or its ISBN with a book you have is offered in Needs review as a
new version of it, as on Windows. Bibliotaph downloads a book once for this and deletes its copy as soon as it's done; the
sidebar shows **Reading 120 new files** and then **Up to date**. Scanned pages with no text of their own are read by
OCR, in English, as on Windows: the sidebar shows **Reading scanned pages · 40 left** while it works, and they're then
found by search like any other page. With [AI](#ai) set up, each book is then catalogued by it too.

## Your library and search

**Library** shows your books as the Windows app does, with their covers, or as a list with the **List** button beside
**Covers**. Each card says what the book is ("D&D 5e · Adventure", or "PDF · 96 pages" until it's catalogued), and marks
a book owned elsewhere, a locked or protected file, a file that's missing, a favourite and a book read by AI. The
**Packs** and **Each file** buttons show a pack as one card or each of its files on its own. Order the books with the
menu beside **Filters**: **Recently added**, **Recently opened**, **Title A–Z** or **Publisher A–Z**. The browser
remembers the layout, the order and the pack view.

Right-click a book (or press and hold it on a touch screen) for the Windows app's menu: **Open**, **Open in new
window**, **Open on D&D Beyond** (or wherever else you own it online, with a link), **Details**, **Ask this book** while
AI is set up, **Add to favorites**, **Pin**, **Cataloged wrong…**, **Download the file** for a file whose protection
Bibliotaph can't open, **Forget its text** or **Read it again**, **Add to collection** and **Add to binder**, each with
the list to choose from, and **Remove from library** for a book owned elsewhere. **Open in new window** opens the book
in a reader window of its own, as Shift+Enter on a book or a middle-click does. A double-click on a book you own only
online opens its page on that site in a new tab.

**Forget its text** removes what reading the book made (its pages and their search text, its cover, the AI's results,
what it found in each chapter and Ask this book's conversation) and stops reading it; the file and your work on the
book stay. **Read it again** reads it from the start. **Download the file** opens it from your storage in a new tab,
where the browser shows it or saves it for an app that can read it, as **Open in another app** does in the Windows
app.

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
word out, and fields such as `system:` and `type:`. The **search guide** under the box lists them as in the Windows app:
it opens on an empty box (or with Ctrl+Space or the Down arrow anywhere), narrows to the fields that start with what you
type, and after a field's colon lists its values in your library with how many books have each. The arrow keys move
through it, **Enter** or **Tab** puts the highlighted one in the box, and **Esc** closes it. A search and its filters
are in the page's address, so you can bookmark them.

Click a card, a list row or a matching book's title to open its details over the page, as in the Windows app: its cover
and what it is, then **Details**, each field with where its value came from (**Sources**, **Suggested** or **Sources
disagree** opens the evidence and the other suggestions; **More fields** shows the empty ones), the adventures marked in
a file or the file an adventure is in, a pack's files, **About the file**, **Where it is** in your storage (the service first, such as OneDrive or
Dropbox, then the account and its folders), its copies when it has more than one, each with its service too, and how far reading it has got. **Esc**, the **×** or a click outside closes them.

**Home** greets you with the books you opened last and those added last, a cover each; a click opens the book's details
in the Library, and a right-click has the same menu as in the Library. Until AI is set up, Home asks **Fill in the
catalog with AI?** once your folders are in: **Set up AI** goes to Settings › AI, and **Not now** stops asking.

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
- **Reprocess**, under **Where it is**, reads the file again; what you added stays. The arrow beside it has **Reprocess
  with OCR on every page…**, for a scan whose own text is poor: it says how long that takes and asks first.
- **Open on D&D Beyond** (or wherever you own the book online) opens its page there, **Ask this book** opens it with
  the Ask panel, and **Download the file** is there for a file whose protection Bibliotaph can't open.
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

### Online libraries

The books you own on D&D Beyond or Demiplane, and those you bought on Humble Bundle or DriveThruRPG, can come in all
at once. **Add ▾** › **Import from an online library…** in the Library opens **Settings** › **Online libraries**,
which says how books come in from outside and lists the services in two groups: **Books you read online** and **PDFs
you bought from stores**. The quickest way is the **Send to Bibliotaph** bookmark under **How they come in**, always
there whether or not you have the extension: drag it to your browser's bookmarks bar once. Then choose **Open D&D Beyond** (or another service) to open your library there in a new tab, sign in if it
asks, and click **Send to Bibliotaph** on that page. Bibliotaph opens in a new tab, reads the page as it shows, fetches
the new books' covers from the service's page, and shows the preview in **Online libraries**. If your browser blocks
the new tab, allow pop-ups from the service; if the service's site won't let the page reach Bibliotaph, the bookmark
says so. The **Send to Bibliotaph** extension for Chrome and Edge does the same from a button in the browser's toolbar,
and can fetch covers the services keep on other sites, which the bookmark sometimes can't; once it is in your browser,
Settings says so beside the bookmark. Bibliotaph on the web never visits the
service itself and never sees your password; the Windows app does the same with a browser of its own.

Bibliotaph shows what importing does before anything changes, as the Windows app does: **New** books become books owned
elsewhere, opening on their service (or, from a store, books to get there); a book **Already in your library** gets the
service and its link rather than a second card; one **Imported before** only has its empty fields filled. Untick what
you'd rather leave out and choose **Import**, with **Undo**. A page sent with the bookmark brings its covers; when some
didn't come, the preview says how many did. Books imported before that the page no longer lists are offered under **No longer in this library**, where
**Remove from library** takes one out; nothing goes unless you say so.

### Check a download

Before copying a new bundle into your storage, **Add ▾** › **Check a download…** in the Library tells you which files
you already have, as in the Windows app: **Check a folder…** or **Check a ZIP…** on your computer. Each PDF, e-book and
picture, those in the folder's ZIPs included, is marked as new, already owned, another copy, maybe a new version, or
owned elsewhere, grouped so the new ones come first, with **Open the book** for a match. **Stop** stops it, and **Copy
list** copies the results to paste somewhere else. Your browser reads the files itself: only each file's SHA-256 hash
and, for a PDF that isn't the very same file, its title and fingerprints of its pages' text go to bibliotaph.online,
never the files. Nothing is copied, moved or added.

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

Opening a binder shows its reader, as in the Windows app. Its items are listed down the left, numbered under their
sections, with the one in hand highlighted; the reader in the middle shows that item's pages, with every other page
of the book still reachable; and **Binder notes** on the right are saved as you type, with **On these pages** under
them listing the page notes on the item's pages (all of the book's for a whole book) to go to.

- **Next** and **Previous**, or **Ctrl+Right** and **Ctrl+Left**, step through the items; clicking one in the list
  opens it. The line under the title says which pages and where you are ("p. 42–44 · 1 of 3"), and **Back to this
  item's pages** shows once you've gone elsewhere in the book.
- **Add this page** adds the page in view (or the whole picture) right after the item in hand, in its section, and
  opens it with its name ready to type over.
- To rename an item, press **F2**, double-click it or use its pencil. Enter or leaving the box saves; a blank name
  shows the book's title, and Esc keeps it as it was.
- An item whose pages changed since it was added opens at the same printed page with a line over the pages saying
  so. If it's the wrong page, go to the right one and choose **Use this page**: the item opens there from now on.
  The thumbnails mark the item's pages with a bar.
- An item whose file can't be reached says why, with **Try again**; a page owned online has **Open it**, in a tab of
  its own.
- **Pin** puts the binder in the sidebar's Pinned group and on Home, the panel button folds the notes away, and the
  pencil (**Edit binder**) opens the binder's list for sections, order and the rest.
- **At hand** lists the binders kept at hand during games with their pages, those for this binder's game systems
  first. Pick a page and it opens in a window of its own, so the binder and page you're on stay put; a page on an
  online library opens in a tab. Your browser may ask once to allow pop-ups for bibliotaph.online.
- **Pop out** moves the binder into its own window, for a second screen. **Return to main window** brings it back at
  the same item and page.

The binder's list has **Done**, back to its reader, beside the pencil (**Edit title and date**), **Duplicate** and the
bin, which deletes the binder but never its books, with **Undo**. **Binder sequence** lists its items in order,
numbered, each with its cover, its label, which book and pages it is, and your note:

- Click the label or **Add a note** to type; Enter or leaving the box saves it. A blank label shows the book's title.
- Drag an item by its grip, use its arrows or press **Alt+Up** and **Alt+Down** to move it. Dropped on another item it
  takes that item's place; dropped on a section's heading it goes first under it.
- Right-click an item for **Move up**, **Move down**, **Move to** each section and **Remove from binder**; its **×**
  or the **Delete** key removes it too, with **Undo**.
- **Add section** offers Adventure, Maps, Encounters and Rules, or **Another name…**. A section's pencil renames it and
  its bin deletes the heading, moving its items up to the section above.
- An item's arrow (**Open**, or **Enter**) opens it in the reader at its pages, with **Use this page** when they
  changed; **Shift+Enter** or Ctrl+click opens it in a new tab. An item whose file can't be reached is dimmed and says
  why.

**Binder notes**, on the right, are saved as you type. **Show these books in the Library** shows the binder's books
there, with **In** and the binder's name on a chip; **Find resources** opens the Library. Binders are the same ones
the Windows app shows.

**Add to binder** adds books as in the Windows app: the notebook button in a book's details adds that book (a file
in a pack shown on its own card goes in on its own), and **Add to binder** in the Select bar adds every ticked book.
Its menu leads with the binder you worked on last, then **Add to another binder…**, which lists them all and narrows as
you type, and **Add to a new binder…**, which makes one first. While searching, the **+** beside a matching page on the
**Pages** tab adds that page to the binder you worked on last. Each says what it added, with **Undo**.

### Needs review

**Needs review** in the sidebar has a count of what's waiting, as in the Windows app, and its page, *A little
clarification.*, has the same two tabs.

**Metadata suggestions** lists new terms first, then a card for each field of each book that needs a second look:
sources that disagree, a title that looks like a file's name, or, with **All suggestions** chosen for **What waits for review** in
**Settings** › **Preferences**, every value nobody has confirmed. **When unsure**, the default, keeps to the first two; changing it
counts every book's cards again, and the sidebar's count follows. A card shows what the book has now, the suggestion and where it came
from. Each button sits under the value it keeps:

- **Accept**, under the suggestion, which makes the suggestion the book's value, and **Accept all**, which accepts every card that suggests
  the same for the same field, shown or not. A line above the cards says how many, with **Undo all**.
- **Edit** (or **Type a title**), to type the value yourself, with the field's list to pick from and a line saying
  how what you type will be filed. Enter saves; Esc cancels.
- **Keep current** (or **Leave empty**, **Keep this card's**, **Keep as is**), under what the book has now, which
  keeps it. A value turned down this way is never suggested again.

A new term card offers **Add as a new** term, **Reject**, or a term the vocabulary has to **File under it**. Each
decided card folds to a line saying what happened, with **Undo**, until the page is opened again. **Show** narrows
the list to new terms or to one field's suggestions, with how many of each are waiting; 60 cards show at a time, with
**Show more** for the rest. The book's title opens its details in the Library. When the library's suggestions change
while you work, a line offers **Show the new list**.

The cards that ask one question come between the new terms and the field suggestions, as in the Windows app:

- **New version?**: a file that shares most of its pages, or its title and publisher, with a book you have. The card
  names both files with their pages, and you choose which the book opens: each button names its file, such as
  **Handbook_v2.pdf is current**, either way one card for both. **Separate book** keeps them apart. Nothing is picked
  until you click one. **Open the book** shows the book it
  was matched with.
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
names, to paste into a bug report. A locked file has **Open to unlock**, which opens it in the reader to ask for its
password, and a file with protection Bibliotaph can't open has **Download the file**, to open it in an app that can.

## Reading a book

A book opens in the reader as in the Windows app: double-click it in the Library, or click it once and click **Open**
in its details. A matching page under **Inside documents** has **Open page**, which opens the book at that page with
the search's words marked. A book opened from the Library opens where you left it, and says so; an adventure inside a
file opens at its own first page and shows where you are in it ("page 3 of 19 of this adventure"). While a book is
open, the screen stays on, so a tablet at the table doesn't dim between glances; it dims as usual once you leave the
book or switch away.

The pages are read from your storage a piece at a time, so a big book shows its first page in a moment: straight from
OneDrive, and through the server from Dropbox, Google Drive, Box and WebDAV, which passes each piece on without keeping it. The link the reader uses for those works only while you're signed in, and only for you. Through the server, each account reads up to 10 GB a day, many big books' worth; past that, books you've read already still open from your browser. What you've read is kept in your browser, so turning back to a page doesn't fetch it again.

- **Page box**: type a printed page number or a PDF page number, and press **Enter**. **Previous** and **Next** turn a
  page; the line under the pages says which printed and PDF page you're on.
- **Zoom**: the zoom buttons and menu, **Ctrl+Plus** and **Ctrl+Minus**, or **Ctrl** and the mouse wheel. **Ctrl+0**
  fits the page to the width again.
- **Contents**: the list button shows the book's bookmarks on the left, with what the AI found in each chapter when it
  has read the whole book. An adventure's own bookmarks stand out.
- **Page thumbnails**: the toolbar button shows a strip of small pages under the pages, the page you're on outlined.
  The browser remembers whether it was showing until you close it.
- **Links** in the PDF work, and **Back to page …** takes you back after following one. On an index or contents page
  whose entries aren't links, the page numbers are made into links anyway, as in the Windows app: point at one to see
  where it goes ("Go to page 34", with the PDF page too when that's different) and click to go there.
- **Find**: **Ctrl+F**, then **Enter** or **F3** for the next match and **Shift+F3** for the one before. In an
  adventure, Find looks in its pages unless you tick **Whole file**.
- **Copy**: select text and press **Ctrl+C**. If a PDF doesn't allow copying, Bibliotaph says so.
- **Note** at the top adds a note on the page in view, or on the page with the text you've selected: type the note,
  widen it to a range with **To page** if you like, and **Save note** (or **Ctrl+Enter**). **Notes** in the toolbar
  shows the notes on the book's pages, from all its copies, beside the pages: click a note's pages to go there, the
  pencil to change it and the bin to delete it, with **Undo**. Pages with notes have a badge in the thumbnails. A note
  whose page changed in a new version says so when you go to it, and one made in another copy says its page may be a
  little different here. A book's notes are the same ones the Windows app shows.
- **Add to binder** at the top lists your binders by name, the one you worked on last first; choose one and the page
  in view goes into it (a picture is added whole). Past the eight most recent, **Another binder…** picks from the
  rest, and **New binder…** makes one. **Add pages…** at the bottom is for a range: type the pages, a label (it starts
  with the bookmark the page is under) and which binder and section it goes in. What was added shows over the pages,
  with **Undo**.
- **Mark as adventure** gives pages of the file a card of their own, as an adventure, starting at the page in view.
  Right-click a line of the contents for **Index as its own adventure…**, which runs to the next line at the same
  level. The file isn't changed.
- **Use this page as the cover** (the picture button) makes the page in view the PDF's card cover, for a book whose
  first page is blank or a plain title sheet; on the first page it puts the first page back. In a pack of pictures,
  **Use this image as the pack's cover** shows the picture in view on the pack's card in place of its mosaic.
- The **heart** and the **pin** make the book a favorite and pin it to the sidebar. **Pop out** moves the book to a
  window of its own, and **Return to main window** brings it back. **Open in new window** beside **Open** in a book's
  details, and beside a matching page, opens it in one straight away.

A PDF with a password asks for it as it opens, as the Windows reader does. **Make its text searchable**, ticked to
start with, lets Bibliotaph read the book for search with it; untick it to only read the book. **Remember it in your
web library** keeps the password, encrypted with the server's own keys and never shown, so the book opens without
asking next time, on any browser; a remembered password that no longer opens the book is forgotten and you're asked
again. **Settings › Passwords** lists the books with a remembered password, never the passwords, with **Forget** for
one and **Forget all**. Text already read stays searchable after a password is forgotten. A book owned elsewhere has nothing to
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
- **Books in this browser**, in **Settings** › **Devices**, lists the books kept here, how much room they take, and when you last read
  them. Set the room for books not kept offline (2 GB to start); past it, the books you read longest ago are let go
  first. A book kept offline is never let go for room. **×** lets one book go, and **Let go of books not kept
  offline** lets go of the rest.
- When you first keep a book offline, Bibliotaph asks the browser not to clear its books when the computer runs short
  of space. Some browsers agree only for a site you visit often or have bookmarked; until then, Settings says the
  browser may clear them.
- A private window can't keep books: each page downloads as you read it.

## AI

AI is optional on the web too. Without it, Bibliotaph still finds, searches and opens everything, and fills in what it
can from folder names and the files themselves. With it, a model reads a few pages of each book and suggests the rest of
its details, each with the page and quote it came from, as in the Windows app (see [AI](ai.md)). Bibliotaph doesn't run
a model on the web or pay for one: you use your own key with a service that speaks the OpenAI chat API, such as OpenAI
or OpenRouter, or your own model server on the internet.

1. Open **Settings** › **AI** and type the server's **Address**, such as `https://api.openai.com/v1` or
   `https://openrouter.ai/api/v1`, and its **Key**, then choose **Connect**. Only an https address on the internet
   works: the web can't reach a server on your own computer or network.
2. Tick **Send excerpts to this server**. Each book's excerpt, a few thousand words of its text, is sent there.
3. Choose a **Model** and **Save**. AI switches on and books are catalogued in the background, one at a time, even
   when Bibliotaph isn't open; the sidebar shows **Cataloguing with AI · 120 left**.

The key is kept encrypted on Bibliotaph's server, used only to ask that server, and never shown again: Settings says
which key is saved by its last four characters, and **Forget key** removes it. **Test with a book** tries the model on
one of your books and shows what it kept, with each page and quote, and what it threw away and why. **Use AI** turns it
**Off** or **On** without losing your setup, **Reclassify** reads again the books an earlier model read or couldn't
read, and **Library folders sent to AI** keeps a folder's books away from the model.

**See what the AI is reading**, under **Use AI** and beside **Pause AI** in **Settings** › **Processing**, shows the book
the AI is on, how long it has been at it, its answer as the model writes it, and the last 20 books read with what the
evidence check kept, each with its page and quote, and what it threw away and why, or why a book couldn't be read. With
no book being read, it says why: AI is off, paused or waiting for the model service. The list starts afresh when the
server restarts.

The service bills you for what it reads: cataloguing reads a few thousand words of each book once.

### Scanning whole books

**Scan whole books** in Settings › AI has the AI read all of each book once it's catalogued, chapter by chapter, and
note each chapter's setting, environments, themes and creatures, with the page that shows them, as in the Windows app
(see [Scanning whole books](ai.md#scanning-whole-books)). It's off on the web until you tick it, since every chapter is
a request on your key and costs far more than cataloguing. It goes after everything else and stops whenever you ask the
AI something; Settings and the sidebar show **Scanning whole books · 12 of 840**, and a scan carries on where it was
the next time.

What it finds shows under **Found inside the book** in a book's details, apart from the book's own details, which it
never changes, and in the reader's **Contents** under the chapter it was found in. **Scan again** forgets what was found
in a book and reads it again.

### Ask this book

With AI on, **Ask** in the reader's toolbar opens a panel where you ask the open book a question, as in the Windows
app (see [Ask this book](reading.md#ask-this-book)); **Ask this book** in a book's details opens it with the panel
showing. It's offered for PDFs and e-books with text. Bibliotaph finds the pages most likely to answer, and the model
answers from those pages only, shown as it writes: each [p. 112] opens its page with the words marked, a quotation
that isn't on its page is in italics, and **Stop** ends an answer early. The pages it read are listed under the
answer. Follow-up questions are understood in the light of the ones before, each book keeps its conversation, and
**New conversation** starts afresh.

Ask finds pages by their words. Choose an **Embedding model, for Ask this book** in Settings › AI, such as OpenAI's
`text-embedding-3-small`, and it finds them by meaning too: a book's pages are read for meaning when you open it, on
your key, and the panel says how far that has got.

### Ask in the top bar

The ✦ button beside the search box turns on **Ask**, as in the Windows app (see
[Ask in plain English](searching.md#ask-in-plain-english) and [Ask your library a question](searching.md#ask-your-library-a-question)).
Type a request in plain English, such as “a one-shot heist for 4 players in a city”, and press **Enter**: the AI reads
it as the search box's own fields and words, shown as chips with notes on anything it changed or left out. Change or
remove a chip, then **Search** puts them in the box and searches. Or type a question, such as “how does grappling
work?”, and press **Ctrl+Enter** or **Answer from my books**: it's answered from the pages of the books the Library
shows (its filters and the search box's fields, as the Library last showed them), each citation naming its book and
page and opening that book there. **Esc** leaves Ask and keeps what you typed.

With an embedding model chosen, Settings › AI offers **Read the library for meaning in the background**, so questions to
your library find pages by meaning as well as by words. It's off until you tick it, since every batch of pages is a
request on your key; it goes slowly, pauses while you're using the AI, carries on where it was, and Settings says how
far it has got.

## Connecting a Windows PC

The Windows app can connect to your web library, the way a TV signs in to a streaming service:

1. In the Windows app, open **Settings > Devices** and click **Connect…**. Your browser opens bibliotaph.online's
   **Connect a PC** page, and the app shows a code such as K7XQ-M2PD.
2. Sign in if asked, check the page shows the same code, and click **Connect**. Only connect a PC you just asked to
   connect. **Don't connect** turns it away.
3. The app says whose library it's connected to. It has a key of its own, kept in Windows Credential Manager on that PC.

Once connected, your work is merged both ways, with the web and [your other PCs](onedrive-and-other-pcs.md#more-than-one-pc): your
corrections, hearts, notes, collections, saved searches, binders, pins, vocabulary, books you own elsewhere and your
answers in Needs review. A change in the app reaches the web a few seconds after you make it, and one on the web
reaches the app within a minute or so; where both changed the same thing, the newer change wins. Settings stay on each
side, and so do the PC's own library folders. The app keeps working offline and catches up when it's back, and its
**Settings > Devices** says when it last merged.

Your web library's books show in the app too. Each of its library folders is a library folder in the app's
**Settings > Library**, with a cloud icon and where it is, such as **bibliotaph.online › OneDrive · Dana › RPG**, and its
books join your library within a minute or so of connecting. Nothing is downloaded for that: the app takes what the
web library already read from each book (its pages, contents, text and cover), so you can search them straight away,
and the PC's AI reads them as it reads the PC's own books, unless you keep that folder from AI in **Settings > AI**. A
book the web library hasn't read yet waits until it has. Scanned pages that need reading (OCR) are read on the PC,
which downloads the book for that. A book the web library couldn't read, such as one with a password it hasn't been
given, is downloaded and read on the PC instead. A folder you add or remove on the web follows on the PC within a
minute. **×** beside one in the app's **Settings > Library** removes it from that PC only. Disconnecting removes them
all from the PC; what you did with their books stays, as it does for any folder you remove.

A book that's on the PC as well opens from the PC's copy. One only your web library has is downloaded from your
storage when you open it, and the reader shows how far the download has got. Downloads are kept for next time, up to
the **Room for downloads** in the app's **Settings > Devices** (5 GB to start); past that, the ones you read least
recently go. To read a book without a connection, such as at the table, tick **Keep offline on this PC** in its
details while you're online: it's downloaded then and stays until you untick it. Opened without a connection, any
other web library book says it needs one. **Open in another app** opens the downloaded copy. **Free up space** in **Settings > Devices** deletes the downloads not kept
offline, and disconnecting deletes them too; the ones kept offline stay for when you connect again.

A connected PC looks its books up through bibliotaph.online, which
asks Open Library and then Google Books, so the PC's own books are found in both, as the web library's are; the ISBNs
are all it sends. The books it couldn't find before it connected are asked again. Disconnected, it asks Open Library
itself. See [Cataloging](cataloging.md#where-suggestions-come-from).

What the AI found is shared too, so a book is read by the AI only once, wherever that happens first: usually on the PC,
with the built-in AI on its graphics card. The same file in your OneDrive and on the PC is the same book, matched by
its contents, so when the PC's AI has read it, the web library has its suggestions within a minute or so, for you to
check in Needs review, without an AI key of its own. It works the other way as well: a book the web library's AI read,
or another connected PC's, isn't sent to this PC's AI. The chapters a whole-book scan found are shared the same way. A
file in a folder you keep from AI gets no suggestions from elsewhere. If two devices read the same book, the newest
reading wins; your answers about its suggestions travel with the rest of your work.

Scanned pages are shared the same way. Reading a scanned book's pages (OCR) is slow, so before a device reads one it
asks whether another of your devices already has, and takes that text, with each word's place on the page so selecting
text still works. A book the PC read is ready to search in the web library without being read again there, and the
other way round. **Reprocess with OCR on every page** still reads the pages again on the device where you ask for it.

The request lasts 10 minutes; after that, click **Connect…** again. **Settings › Devices** on the web lists each
connected PC with when it was connected and last used, and **×** disconnects one: its key stops working straight
away, and the PC keeps its own books and work. What it already shared stays in your web library. **Disconnect** in the app does the same from the PC.

## Processing

**Settings** › **Processing** shows what Bibliotaph is doing with your library, as the Windows app's does: a bar for each
step (finding files, reading them, making them searchable, reading scans, looking books up online and cataloguing with AI) with how far it has
got and the time left, what each part is working on now, and how many files need attention, with a link to them. The
server does the work, so it carries on with the page closed. **Pause indexing**, **Pause reading scans** and **Pause
AI** each stop one step until you resume it, even after you sign out; pausing the AI stops its requests on your key.
The library comes first: while you're using it, reading books in the background steps aside, and carries on when you
pause.

**Looking up online**, under the steps, says where books are looked up: the server asks Open Library about the ISBNs
your books print, and nothing else, and suggests each book's title, authors, publisher and year from its record, as the
Windows app does. A book Open Library doesn't know is looked for in Google Books next. This is always on; a book
neither knew is asked again once there's somewhere more to ask. See [Cataloging](cataloging.md#where-suggestions-come-from).

## Vocabulary

**Settings** › **Vocabulary** holds the game systems, types and other terms your books are filed under, as in the
Windows app, with a tab for each list. Type in **Add to types** (or whichever list is chosen) and press **Add**, or find
a term by any of its names. The pencil beside a term opens it: change its **Name** or its **Short name, on covers** and
**Save name**, and add or remove its **Other names** ("5e" for an edition, say). Folder names, searches and what you type
all go through these names. Renaming keeps everything filed under the term, and the library catches up a few seconds
after you stop. **New names from the AI**, at the top of each list the AI can add to, chooses **Ask first** (a card in
Needs review for each name, the default) or **Add them** (the name joins the list as the AI uses it, and the names
already waiting are added too), as in the Windows app.

The last tab, **Your tags**, holds tags of your own, each with the words that signal it ("Undead": zombie, lich).
**Add tag** looks through your books' pages at once and suggests the tag for each book whose pages use the words often
enough; Needs review and a book's details show it as suggested until you keep it. The pencil changes a tag's name or
words, and **Delete tag** takes its suggestions away; books where you kept it keep it.

## Backup and export

**Settings** › **Backup** › **Download a backup** saves a backup of your catalog (folders, corrections, collections,
binders and notes, but not your books) as the Windows app's **Back up now…** does, named for the day, such as
"Bibliotaph backup 2026-10-26.zip". The Windows app restores it from its own **Settings** › **Backup**, asking where
each folder is on that PC. AI keys and PDF passwords are never in a backup.

Bibliotaph also makes a backup each week while you use it, once your library has books in it, and keeps the last four
in its own storage, as the Windows app keeps its automatic backups. **Settings** › **Backup** lists them with when each
was made: **Download** saves one, and **Restore…** reads it.

**Choose a backup…** under **Restore** reads a backup from your computer instead, one downloaded here or made by the
Windows app. Nothing changes yet: Bibliotaph shows when the backup was made, by which version, how many books,
collections and notes it holds, and each of its library folders, saying whether it is in your storage. A Windows PC's
folders aren't, so they stay offline here. **Restore…** asks once more, then keeps a backup of your catalog as it is
now (listed as *Made before a restore*, so a restore can be undone by restoring that one) and puts the backup's catalog
in its place. Anything you did since the backup was made is replaced. Your books are then read again for search in the
background. **Cancel** forgets the backup read.

To keep the Windows app's work and this library up to date with each other, connect the PC as **Settings** ›
**Devices** describes instead: that merges your work both ways, where a restore replaces it.

**Export CSV** saves a row per book (title, system, type, levels, publisher, tags, collections and where its file is)
for a spreadsheet, and **Export JSON** everything in your catalog, for other programs, as in the Windows app.

## Your preferences

**Settings** › **Preferences** holds the choices about how Bibliotaph works for you:

- **Light or dark** chooses **Use system**, which follows your device, **Light** or **Dark**, as in the Windows app. The
  button at the top right cycles through the same three. The browser remembers your choice, so another browser or
  computer keeps its own.
- **Start on** chooses whether bibliotaph.online opens on **Home** or the **Library**.
- **What waits for review** chooses **When unsure** or **All suggestions**, as described under Needs review.

## Installing it as an app

bibliotaph.online can be installed like an app, with its own icon and a window without the browser's address bar,
which suits a tablet at the table. In Chrome or Edge on a PC, choose **Install** at the right of the address bar; on
Android, **Add to home screen** in the browser's menu; on an iPad or iPhone, **Share** › **Add to Home Screen** in
Safari. It is the same site, signed in the same way, so nothing else changes.

## Privacy

Your books stay in your own storage. Bibliotaph keeps your account, your storage connections (encrypted), an index of your
library and the work you do in it. The [privacy policy](https://bibliotaph.online/privacy) says exactly what's kept,
where, and how to have it deleted.

## The site

[bibliotaph.online](https://bibliotaph.online) shows what Bibliotaph is, signed in or not: what it does, with the app's
screenshot and small examples of search, the catalog, binders and online libraries; the Windows app and the web side by
side; and answers to common questions. **Sign in** is at the top, and **Get the Windows app** downloads the latest release. Its
footer links the rest of the site: [Features](https://bibliotaph.online/features), [About](https://bibliotaph.online/about),
the [Change log](https://bibliotaph.online/changelog), [Contact us](https://bibliotaph.online/contact),
[Terms](https://bibliotaph.online/terms), [Privacy](https://bibliotaph.online/privacy) and
[Licences](https://bibliotaph.online/licences), which has Bibliotaph's licence and every component's. Signed in, the site's
pages offer **Open your library** in place of **Sign in**, which goes to your Home at bibliotaph.online/home, and
**b. Bibliotaph** at the top of the app's sidebar comes back to this page.

The same links are under every page of the app too, except the readers, with the version that's running and a tagline,
a different one each time the page loads. They open in a new tab, so whatever you had open stays as it was.
