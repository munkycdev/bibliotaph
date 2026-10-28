# Cataloging

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Cataloging

Bibliotaph fills in each book's details by itself, so you don't have to type them. You only step in when something's
wrong, and your corrections always win: reading a book again, or a new AI suggestion, never undoes them.

## Where suggestions come from

- **Folder and file names**: a folder called "Adventures" or "D&D 5e" suggests the type or system of the books in it.
- **The file itself**: the title, author and other details a PDF or e-book carries (an e-book's description and
  cover too), and party sizes and levels in the file name.
- **Open Library**, if you turn on **Look up books online by their ISBN** in **Settings > Processing**: Bibliotaph
  sends the ISBNs a book prints, and nothing else, to [Open Library](https://openlibrary.org) and suggests the title,
  authors, publisher and year its record gives. A record is used only when its title is printed in the book, so a
  misprinted ISBN can't rename it. These beat the AI's suggestions: the AI's guesses for those fields aren't shown, and
  come back only if you reject the looked-up value. Off until you turn it on.
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
- **Remove from library**, for a book you own elsewhere. It's on the book's right-click menu in the Library too.
- On the right, icon buttons to pin the book, add it to a binder, and correct it (**Cataloged wrong…**).

The **Details** tab shows:

- The fields: title, game system, edition, type, levels, players, duration, style, setting, themes, environments,
  publisher, authors, series, year, your tags, where else you own it (**Also own**, with the book's page online if you
  like) and where to get it (**Get it from**, a store with an optional link). Empty fields are hidden until you click
  **More fields**, except the main ones, and **Players** on an adventure, so you can say who it's for straight away.
- **Suggested** marks on values nobody has confirmed yet. Open one to see where it came from, with the page and quote.
- **Where it is**: the file's folder, with **Show in File Explorer**.
- **Copies**, when the book is in more than one file.
- **About the file**: its format, when it was added, its folders and what the file says of itself. It includes the
  **ISBN** and **Product code** the book prints, as it prints them. Bibliotaph finds
  these on the first pages and the back cover, from the text it already has, and from an e-book's own information.
- Its collections, as chips you can remove.
- **Found inside the book**, once the AI has [scanned the whole book](ai.md#scanning-whole-books): each chapter with
  its pages and the settings, environments, themes and creatures found in it. These describe parts of the book, not the
  whole book, so they never change its details. **Scan again** has the AI read it again.

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

- A field where the sources disagree, or a book with no useful title. Each button sits under the value it keeps:
  **Keep current** under what the book has, **Accept** under the suggestion, or **Edit** to type your own value.
- A new term the AI used that your vocabulary doesn't know. Add it, file it under an existing term, or reject it.
- **New version?** A file that looks like another version of a book you have. The card names both files with their
  pages, and you choose which one the book opens: each button names its file, such as **Handbook_v2.pdf is current**,
  either way one card for both files. **Separate book** keeps them apart. Nothing is picked until you click one. You can change it later with **Make current** under
  the book's copies in its details.
- **Image pack?** "Make these 12 images one card?": **Make a pack** or **Keep separate**.
- "You own *The Sunken Abbey* elsewhere. Is this its file?": **Same book** or **Separate book**.
- Copies of a book that say different things, and pages that moved in a new version, with **Use this page**.
- A compilation that seems to hold several adventures (see below).
- **Collection?** "Make a collection “Ashen Vale” from these 12 books?" for a folder with three or more books in it
  (see [Collections from folders](organizing.md#collections-from-folders)): **Accept**, **Not now** or **Never**.
- **Add to collection?** "Add these 4 books to “Ashen Vale”?" for books that share what most of a collection's books
  share (see [Books for your collections](organizing.md#books-for-your-collections)).

The cards that offer to make something (a collection, books for a collection, an image pack, a folder rule, or cards
for a file's adventures) have a **×** in their top right corner. It closes the card with its "don't ask again" answer,
the same as **Never**, **Keep separate**, **No thanks** or **Not split**, so that suggestion doesn't come back, though
a new one can (a book that fits the collection later, say). **Undo** on the closed card brings it back.

To check a book before you answer, click the window button beside its title (or middle-click the title, or press
**Shift+Enter** on it). The book opens in a window of its own, at the page the suggestion's evidence is on, and Needs
review stays where it was, so you can read and decide side by side, on a second monitor too. The window is shared:
opening the next card's book shows it in the same window rather than another. Clicking a title still opens the book
in the main window.

When there's more than one kind, **Show** narrows the list to one: New terms, New versions, Image packs, Owned
elsewhere, Adventures, Collections from folders, Books for collections and so on, or the suggestions for one field
such as Setting or Type. Each entry says how many are waiting, and **Everything** shows the whole list again.

**Files needing attention**: files that couldn't be read, with the reason, including books the AI couldn't read.
Files with the same problem share a card, and **Show the 12 files** lists them. Locked PDFs have **Open to unlock**,
protected ones **Open in another app**, and **Try again** and **Try all again** give the others another go. When
Bibliotaph hit a bug, **Copy details** copies the version and the error, without file names, to paste into a bug
report.

Every decision has **Undo**. **What waits for review** in **Settings > Preferences** chooses what waits for a look:
**When unsure** (only what sources disagree about, and missing titles) or **All suggestions**.

## Copies and versions

When two files have the same text on every page, such as the same book downloaded twice or copies stamped with
different buyers' names, Bibliotaph gives them one card, so everything you do with the book is in one place. Its
details list them under **Copies**, where **Make current** chooses which one opens and **Not the same book** gives a
file its own card again.

A file that shares most of its pages with a book you have, or its title and publisher, is asked about as a new
version instead. Nothing is ever joined without asking unless the text is identical.

To join cards yourself, tick them in **Select** mode and click **Same book**. You choose which file opens.

## Packs

A folder (or ZIP) of 20 or more images and no PDFs, such as a map pack or a set of handouts, becomes one card: an
image pack, named after the folder. A smaller set is offered in Needs review instead. **Split into separate images**
gives each image its own card again, and Bibliotaph remembers that. To make a pack by hand, tick images or packs in
**Select** mode and click **Pack together**.

Packs can hold PDFs too, such as a party's character sheets or a set of pregenerated characters with their
portraits. Bibliotaph never puts a PDF in a pack by itself: tick the PDFs (and any images or packs to go with them) in
**Select** mode and click **Pack together**. The card then says "Pack · 6 files" instead of "Image pack · 6 images",
and its cover is still a mosaic of the first four. Opening the pack steps through its files with the arrows at the
top (**Previous file** and **Next file**): PDFs open as PDFs and images as images. Its details list every file, PDFs
shown by their first page, and **Split into separate files** gives each one its own card back, with everything you had
set on it. A PDF with adventures marked in it can't go in a pack, since their cards name it.

A pack's cover is a mosaic of its first four files until you pick one to stand for it, such as the overview of a map
pack. Right-click a file in the pack's details and choose **Use as the pack's cover**, or, while stepping through the
pack in the reader, click the picture button at the top. The file you chose is marked **Cover** in the details, and
**Use the mosaic** there goes back. Your choice is kept when the library is read again; if that file is deleted or
leaves the pack, the card shows the mosaic again.

A search for a file's name finds its pack, and a search inside documents finds the pages of the PDFs in a pack under
the pack's card. To see the files in packs on cards of their own, click **Each file** in the Library's toolbar (see
[Searching](searching.md)); the packs stay packs.

## Adventures inside a file

An anthology or a magazine can hold several adventures. Each can be marked as an adventure of its own, with its own
details, cover and page range, while the file stays one file. Mark one with **Mark as adventure** in the reader,
**Index as its own adventure…** on a bookmark in the reader's Contents, or **Adventures in this file** in the details.
If the adventure's first page or the one after has a blurb such as "for four to six characters of 3rd level",
Bibliotaph suggests its levels and players from it, quoting the blurb. Type over them in its details, as with any
field; values you set are never replaced.

When a file says, in its own words, that it holds several separate adventures ("a collection of four adventures",
"each can be run on its own", "six one-shots"), Needs review says "This file looks like it holds 4 adventures." with
those words and their page, a list you can tick showing each adventure's pages, levels and players as its blurb gives
them, and **Edit**, **Accept** and **Not split**. Its bookmarks and the blurbs on their first pages show where each
adventure starts. A campaign whose chapters each say which level they're for isn't proposed, since it doesn't call
itself a collection. With AI turned on, the AI helps find them too, quoting where the book says so, and reads each
adventure on its own pages to fill in its details.

While you browse, adventures stay inside their book: the file's card says how many it holds ("3 adventures"), and
its details list them under **Adventures in this file**, where clicking one shows its own details. A search finds
them on their own: an adventure that matches shows as a card with its book's cover, an **Adventure** ribbon and the
adventure's title. Click it for its details, or **Open** it to go straight to its first page. If both the book and
one of its adventures match, only the adventure shows, since that's what you were looking for. Page hits are grouped
under the adventure they fall in.

To see every adventure as a card of its own, even while browsing, set **Adventures inside files** in the Library's
**More filters** to **Show as cards**. **Hide** leaves them out altogether, searches included, so only their files
show. A search you saved with adventures hidden keeps them hidden.

**Remove adventure** in an adventure's details takes its card away; its pages are only the file's again, and the file
itself is never changed. To remove several at once, tick their cards in **Select** mode and click
**Remove adventures**. Either way, **Undo** brings them back.

## Books you own elsewhere

Own a book in print or on a virtual tabletop? In the Library, click **Add** and choose **Add a book I
own elsewhere…**. It gets a card with no file, so it shows up in searches alongside the rest, with a
badge on its cover saying where it is. **Also own** records where (print, a VTT, or your own words) on any card, and
can take the book's page online, such as "D&D Beyond https://…", which **Open** then opens. If a file with that title
turns up later, Needs review asks whether it's that book's file.

With no file there's no page to make a cover from. **Choose a cover image…**, in the Add dialog or the book's details,
uses a JPG, PNG or WebP picture of your own: Bibliotaph keeps a cover-sized copy, so the picture itself can move or go.
**Remove the cover** goes back to the online library's cover, or none. A chosen cover stays on this PC; it isn't part
of the library sync file.

Books you own on D&D Beyond or Demiplane, or bought from Humble Bundle or DriveThruRPG, can be imported all at once
with **Import from an online library…** on the same **Add** menu. See [Online libraries](online-libraries.md).

## Check a download

Before copying a new bundle into your library, **Check a download…** under the Library's **Add** button tells you
which files you already have: **Check a folder…** or **Check a ZIP…**. Each file is marked as already owned, another copy, maybe a new version,
or owned elsewhere. Nothing is copied, moved or added. **Copy list** copies the results to paste somewhere else.

## Vocabulary

**Settings > Vocabulary** lists the game systems, types, themes and other terms your books are filed under, with the
other names each goes by ("5e" for "D&D 5e", say). Add terms, rename them, and add or remove other names. Folder names,
searches and what you type all go through these names.

### Your tags

Your tags are your own labels, and **Your tags** in **Settings > Vocabulary** can find them for you. Give a tag a
name and the words that signal it, separated by commas, and click **Add tag**: **Undead** with "undead, zombie, lich",
say. Bibliotaph looks for the words in the pages it has already read, so nothing is read again, and a book whose
pages use them often enough gets the tag, marked **Suggested** like any other guess. Open the mark to see the words
and the pages they're on.

- **Often enough** means the words are on at least 3 of the book's pages, and on at least one page in 20. A passing
  mention, or a few pages in a thick rulebook, doesn't count.
- A word also finds words that start with it, as in search: "zombie" finds "zombies". Several words together, such as
  "old mill", are found only together. To see what a word would find first, search for it on the **Inside documents**
  tab.
- **Keep this** makes the tag yours on that book. **Not right** takes it off, and it never comes back to that book,
  even if you change the tag's words or make the tag again.
- Changing a tag's name or words looks through your books again. **Delete tag** takes its suggestions away; books
  where you kept it keep it.
- New books are checked once their text has been read, scanned pages included.

## Reading a book again

**Reprocess** in the details reads the file again (pages, text, cover and hints from names), keeping everything you
added. Its menu has **Reprocess with OCR on every page…** for a scan whose text came out badly.

**Forget its text**, on a book's right-click menu, removes its pages, search text, cover and AI results from
Bibliotaph and stops reading it. The file isn't touched. **Read it again** brings it back.

---

Previous: [Reading](reading.md) · Next: [Collections, favorites and pins](organizing.md)
