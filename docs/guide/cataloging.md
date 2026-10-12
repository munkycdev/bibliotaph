# Cataloging

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Cataloging

Bibliotaph fills in each book's details by itself, so you don't have to type them. You only step in when something's
wrong, and your corrections always win: reading a book again, or a new AI suggestion, never undoes them.

## Where suggestions come from

- **Folder and file names**: a folder called "Adventures" or "D&D 5e" suggests the type or system of the books in it.
- **The file itself**: the title, author and other details a PDF carries, and party sizes and levels in the file
  name.
- **AI**, if you turn it on: a model on your PC reads a few pages and suggests the rest. Every AI suggestion must quote
  the page it came from, or it's thrown away. See [AI](ai.md).
- **You**: anything you type, accept or reject.

## The details panel

Click a cover in the Library to open its details on the right. Click anywhere outside them, even on another card, or
press **Esc**, to close them.

Under the cover is a row of buttons for what you can do with the book:

- **Open**, and the button beside it to open it in a new window.
- **Open on D&D Beyond** (named after its service), for a book you also own online. See
  [Online libraries](online-libraries.md).
- **Ask this book**, with AI set up. See [Reading](reading.md#ask-this-book).
- **Open in another app**, for a protected book Bibliotaph can't read.
- **Remove from library**, for a book you own elsewhere.
- On the right, icon buttons to pin the book, add it to a binder, and correct it (**Cataloged wrong…**).

The **Details** tab shows:

- The fields: title, game system, edition, type, levels, players, duration, style, setting, themes, environments,
  publisher, authors, series, year, your tags, where else you own it (**Also own**, with the book's page online if you
  like) and where to get it (**Get it from**, a store with an optional link). Empty fields are hidden until you click
  **More fields**.
- **Suggested** marks on values nobody has confirmed yet. Open one to see where it came from, with the page and quote.
- **Where it is**: the file's folder, with **Show in File Explorer**.
- **Copies**, when the book is in more than one file.
- Its collections, as chips you can remove.

The **Notes** tab has a note for the whole book, saved as you type, and the notes on its pages.

### Fixing a value

- **Keep this** confirms a suggestion, so it's never replaced.
- **Use this** picks one of the **Other suggestions** instead.
- **Not right** rejects a suggestion. It won't be suggested again, by anything.
- The pencil beside a field (**Edit**) lets you type your own value; **Save** keeps it.
- **Reset to suggestion** forgets what you set and goes back to what the sources suggest.

### Cataloged wrong

When a book is plainly wrong, right-click it (in the Library or on Home) and choose **Cataloged wrong…**, or click the
pencil button under the cover in its details. Every field shows, and each guess carries a **Guessed** mark with its
evidence open, so you can see what to fix. If you leave without changing anything, Needs review keeps a "You said this
book was cataloged wrong" card with **Fix it**, and **It's fine now** if you change your mind.

After you fix a field, Bibliotaph offers to fix the same guess on other books in that folder, for example
"Change 1 other book from “Horror” too?", with **Undo** afterwards. And when you set the same value on three books
in one folder, Needs review offers a folder rule, so every file in that folder, new ones included, gets the value as a
suggestion. Folder rules can be switched off in **Settings > Library**.

### Edit many books at once

In the Library, click **Select**, tick the books (**Shift**+click ticks a run of them, **Ctrl+A** ticks every book
shown), and click **Edit metadata**. Change any field but the title. A field the books disagree on says so instead of
looking blank, and fields you don't touch stay as they are. **Review changes** shows what will change before it's
saved, and **Undo** puts it back afterwards.

## Needs review

**Needs review** in the sidebar holds what Bibliotaph would like you to look at. It's never in the way: every book is
usable while its card waits. It has two lists:

**Metadata suggestions**, such as:

- A field where the sources disagree, or a book with no useful title. **Accept**, **Reject**, or type your own value.
- A new term the AI used that your vocabulary doesn't know. Add it, file it under an existing term, or reject it.
- **New version?** A file that looks like a newer version of a book you have: **Make it current**,
  **Keep as another copy** or **Separate book**.
- **Image pack?** "Make these 12 images one card?": **Make a pack** or **Keep separate**.
- "You own *The Sunken Abbey* elsewhere. Is this its file?": **Same book** or **Separate book**.
- Copies of a book that say different things, and pages that moved in a new version, with **Use this page**.
- A compilation that seems to hold several adventures (see below).

**Files needing attention**: files that couldn't be read, with the reason, including books the AI couldn't read.
Files with the same problem share a card, and **Show the 12 files** lists them. Locked PDFs have **Open to unlock**,
protected ones **Open in another app**, and **Try again** and **Try all again** give the others another go. When
Bibliotaph hit a bug, **Copy details** copies the version and the error, without file names, to paste into a bug
report.

Every decision has **Undo**. **Settings > Needs review** chooses what waits for a look: **When unsure** (only what
sources disagree about, and missing titles) or **All suggestions**.

## Copies and versions

When two files have the same text on every page, such as the same book downloaded twice or copies stamped with
different buyers' names, Bibliotaph gives them one card, so everything you do with the book is in one place. Its
details list them under **Copies**, where **Make current** chooses which one opens and **Not the same book** gives a
file its own card again.

A file that shares most of its pages with a book you have, or its title and publisher, is asked about as a new
version instead. Nothing is ever joined without asking unless the text is identical.

To join cards yourself, tick them in **Select** mode and click **Same book**. You choose which file opens.

## Image packs

A folder (or ZIP) of 20 or more images and no PDFs, such as a map pack or a set of handouts, becomes one card: an
image pack, named after the folder. A smaller set is offered in Needs review instead. **Split into separate images**
gives each image its own card again, and Bibliotaph remembers that. To make a pack by hand, tick images or packs in
**Select** mode and click **Pack together**.

A search for an image's file name finds its pack.

## Adventures inside a file

An anthology or a magazine can hold several adventures. Each can get a card of its own, with its own details, cover
and page range, while the file stays one file. Mark one with **Mark as adventure** in the reader,
**Index as its own adventure…** on a bookmark in the reader's Contents, or **Adventures in this file** in the details.

When a file's bookmarks and first pages show it holds several adventures, Needs review says "This file looks like it
holds 4 adventures." with a list you can tick, and **Edit**, **Accept** and **Not split**. With AI turned on, the AI
helps find them too, and reads each adventure on its own pages to fill in its details.

The file's card says how many adventures it holds. **Adventures inside files** in the filters shows or hides their
cards. Search results group page hits under the adventure they fall in.

**Remove adventure** in an adventure's details takes its card away; its pages are only the file's again, and the file
itself is never changed. To remove several at once, tick their cards in **Select** mode and click
**Remove adventures**. Either way, **Undo** brings them back.

## Books you own elsewhere

Own a book in print or on a virtual tabletop? In the Library, click **Owned elsewhere** and choose
**Add a book I own elsewhere…**. It gets a card with no file, so it shows up in searches alongside the rest, with a
badge on its cover saying where it is. **Also own** records where (print, a VTT, or your own words) on any card, and
can take the book's page online, such as "D&D Beyond https://…", which **Open** then opens. If a file with that title
turns up later, Needs review asks whether it's that book's file.

Books you own on D&D Beyond or Demiplane, or bought from Humble Bundle or DriveThruRPG, can be imported all at once
with **Import from an online library…** on the same menu. See [Online libraries](online-libraries.md).

## Check a download

Before copying a new bundle into your library, **Check a download** in the Library tells you which files you already
have: **Check a folder…** or **Check a ZIP…**. Each file is marked as already owned, another copy, maybe a new version,
or owned elsewhere. Nothing is copied, moved or added. **Copy list** copies the results to paste somewhere else.

## Vocabulary

**Settings > Vocabulary** lists the game systems, types, themes and other terms your books are filed under, with the
other names each goes by ("5e" for "D&D 5e", say). Add terms, rename them, and add or remove other names. Folder names,
searches and what you type all go through these names.

## Reading a book again

**Reprocess** in the details reads the file again (pages, text, cover and hints from names), keeping everything you
added. Its menu has **Reprocess with OCR on every page…** for a scan whose text came out badly.

**Forget its text**, on a book's right-click menu, removes its pages, search text, cover and AI results from
Bibliotaph and stops reading it. The file isn't touched. **Read it again** brings it back.

---

Previous: [Reading](reading.md) · Next: [Collections, favorites and pins](organizing.md)
