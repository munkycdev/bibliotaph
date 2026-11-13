# Cataloging

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Cataloging

Bibliotaph fills in each book's details by itself, so you don't have to type them. You only step in when something's
wrong, and your corrections always win: reading a book again, or a new AI suggestion, never undoes them.

## Where suggestions come from

- **Folder and file names**: a folder called "Adventures" or "D&D 5e" suggests the type or system of the books in it.
  Until something better comes along, a book's title is read from its file name: `CurseOfTheVault_v2 (1).pdf` becomes
  "Curse of the Vault". Words run together are split, underscores and dots become spaces, and copy markers like "(1)" or
  "- Copy", tags like "[PDF]", version numbers, dates, product codes and words like "hi-res" or "print friendly" are
  dropped. Acronyms and editions such as D&D, OSR and 5e stay as they are. The file's own title, an online catalogue's
  and the AI's all beat it, and a title you typed or kept is never touched.
- **The file itself**: the title, author and other details a PDF or e-book carries (an e-book's description and
  cover too), and party sizes and levels in the file name.
- **Book catalogues**: Bibliotaph sends the ISBNs a book prints, and nothing else, to
  [Open Library](https://openlibrary.org) and suggests the title, authors, publisher and year its record gives. On
  bibliotaph.online, and in the Windows app while it's [connected to bibliotaph.online](web.md#connecting-a-windows-pc),
  [RPGGeek](https://rpggeek.com) and [Google Books](https://books.google.com) are asked too, and each field comes from
  the first of RPGGeek, Open Library and Google Books that gives it, so RPGGeek, which knows RPGs and their designers
  best, wins wherever it has a value. RPGGeek also gives the book's artists and its mechanics (such as "Dice (d20)"),
  in its own words, which plain search finds. It says what it files the book under, too, and that fills in the game
  system and edition, type, themes, setting and series, in your lists' own terms: its "Scenario / Adventure / Module"
  is your Adventure, its "Dungeons & Dragons (3.5 Edition)" your D&D 3.5. A system, theme or setting your lists don't
  have is treated as a new name from the AI (see [Vocabulary](#vocabulary)); a category none of the types stands for, and
  genres nearly every book has, such as Fantasy, are left out. RPGGeek can only be searched by name, and finds only
  names holding every word searched for, so it gets the title Open Library or Google Books gives, the title the book's
  card shows (yours or the AI's), the one its file gives and the one its cover prints, and then the name of its line:
  the folder it's in, or the one above, when the book prints that name on its first pages, as a publisher's credits
  page names its line; the line RPGGeek named the other books in its folder after ("Gull Rock Saga" for "Gull Rock
  Saga: Book 1"); and a line of three words or more its cover prints. A file called "Monster Tome Vol1" in a folder
  called Gull Rock Saga is found among RPGGeek's Gull Rock Saga books that way, and a folder whose name the book
  doesn't print is never sent. For a book with an ISBN or product code, its record is used only when one of its
  versions lists an ISBN the book prints, or a product code it prints (such as PZO1110), so a search as broad as a
  line's can't give a book the wrong record, and a book with a code but no ISBN is found there too. A book printing
  neither is looked up on RPGGeek by its titles alone, never its line: an item is taken only when it's the one RPGGeek
  names as the book is (whole, or after its line's name), the book prints every part of that name on its first pages,
  and one of its designers or its publisher too; a book printing the name but nothing else that agrees gets a
  **Same book?** card. A book none of them found is looked up again as soon as its title changes, when the AI names
  it or you do. [AdventureLookup](https://adventurelookup.com), a catalogue of published adventures, is asked about
  every book as well, by its title, even one another catalogue already found. It knows no ISBNs, so its record is used
  only when the book prints that title on its first pages and also one of its authors or its publisher, or has about
  as many pages (within two); a book printing the title but nothing else that agrees gets a **Same book?** card, and
  one that doesn't print it is left alone. It's searched by the book's titles only, never its line, since a line names
  many books. It fills in the levels an adventure is for, which no other catalogue has,
  its monsters (bosses first) and magic items, which plain search finds, what it includes (pre-generated characters,
  handouts, battle maps, or that it plays solo), and its edition, setting, environments and series, in your lists' terms: its "Dungeon" and "Cave" are your
  Underground, its "Town/Village" your Urban. Because of it, books that print no ISBN or product code are looked up
  too, by title; an image, which prints no title, isn't. **Looking up online** in **Settings > Processing** counts
  them all, not only the books that print an ISBN. The app asks through bibliotaph.online, and **Settings > Processing** names where it asks. A
  book is asked again once there's somewhere more to ask, such as when the app connects. A book none of them knows
  says **Unable to find in any online listing** beside **Looking it up online** under **Processing** in its details; you
  can give it its RPGGeek record yourself, with [a link to its page there](#look-up-from-an-rpggeek-link). A record is used only when
  its title is printed in the book, so a misprinted ISBN can't rename it; when it isn't, Needs review asks you
  **Same book?** about it (see below). These beat the AI's suggestions: the AI's guesses for those fields aren't shown, and
  come back only if you reject the looked-up value. **Settings > Processing** says where the ISBNs go. RPGGeek's data
  comes from BoardGameGeek, whose "Powered by BGG" logo shows under a book's details when some of them came from
  RPGGeek, in **About** on Windows, and on the website's licences page.
- **AI**, if you turn it on: a model on your PC reads a few pages and suggests the rest. Every AI suggestion must quote
  the page it came from, or it's thrown away. See [AI](ai.md).
- **You**: anything you type, accept or reject.

## The details panel

Click a cover in the Library to open its details on the right. Click anywhere outside them, even on another card, or
press **Esc**, to close them. Drag their left edge to make them wider; they stay that wide.

Under the cover is a row of buttons for what you can do with the book:

- **Open**, and the button beside it to open it in a new window.
- **Open on D&D Beyond** (named after its service), for a book you also own online. See
  [Online libraries](online-libraries.md).
- **Ask this book**, with AI set up. See [Reading](reading.md#ask-this-book).
- **Open in another app**, for a protected book Bibliotaph can't read.
- **Remove from library**, for a book you own elsewhere. It's on the book's right-click menu in the Library too.
- On the right, icon buttons to pin the book, add it to a binder, and correct it (**Cataloged wrong…**).

The **Details** tab starts with what the book holds and ends with its file. Click a section's heading to fold it
away, or to open it again; every book's details keep the sections as you left them on this PC. The last four start
folded.

- **Adventures in this file**, for a compilation, or **In a file**, for an adventure inside one. See
  [Adventures inside a file](#adventures-inside-a-file).
- A pack's files.
- **Found inside the book**, once the AI has [scanned the whole book](ai.md#scanning-whole-books): each chapter with
  its pages and the settings, environments, themes and creatures found in it. These describe parts of the book, not the
  whole book, so they never change its details. **Scan again** has the AI read it again.
- **Collections**, as chips you can remove, with **Add to collection**.
- **Details**: title, game system, edition, type, levels, players, includes, duration, style, setting, themes,
  environments, monsters, items, publisher, authors, artists, series, year, mechanics, your tags, where else you own it (**Also own**, with the book's page online if you
  like) and where to get it (**Get it from**, a store with an optional link). Empty fields are hidden until you click
  **More fields**, except the main ones, and **Players** on an adventure, so you can say who it's for straight away.
  **Suggested** marks values nobody has confirmed yet. Open one to see where it came from, with the page and quote.
- **About the file**: its format, when it was added, its folders and what the file says of itself. It includes the
  **ISBN** and **Product code** the book prints, as it prints them. Bibliotaph finds
  these on the first pages and the back cover, from the text it already has, and from an e-book's own information.
- **Where it is**: the file's folder, with **Show in File Explorer** and **Reprocess**.
- **Copies**, when the book is in more than one file.
- **Indexing**: how far reading the book has got, step by step.

The **Notes** tab has a note for the whole book, saved as you type, and the notes on its pages.

### Fixing a value

- **Keep this** confirms a suggestion, so it's never replaced.
- **Use this** picks one of the **Other suggestions** instead.
- **Not right** rejects a suggestion. It won't be suggested again, by anything.
- The pencil beside a field (**Edit**) lets you type your own value; **Save** keeps it.
- **Reset to suggestion** forgets what you set and goes back to what the sources suggest.

### Look up from an RPGGeek link

When no lookup finds a book, or finds the wrong one, you can say which book it is on
[RPGGeek](https://rpggeek.com). Find the book there, copy the address of its page, then click **Look up from an
RPGGeek link…** at the end of the book's **Details** section, paste the address and click **Look up**. The address of
a book's page looks like `https://rpggeek.com/rpgitem/12345/its-name`; the page of a game, a series or a publisher
isn't a book's, and the box says so. RPGGeek is asked one request at a time, so it can take a few seconds.

The book takes that record at once: its title, designers, publisher, year, game system, series, artists, mechanics
and what RPGGeek files it under, as suggestions above the AI's and below your own values, like any looked-up record.
Nothing the book prints has to match, since you said which book it is. From then on:

- The record stays the book's whatever later lookups find, and any other RPGGeek record a lookup gave the book is
  dropped.
- It goes to your other PCs and your web library with [sync](onedrive-and-other-pcs.md), as an answer to
  **Same book?** does. Each reads the record from RPGGeek itself.
- **On RPGGeek as …** shows at the end of **Details**, and opens the book's page. **Not this book** beside it takes
  the record and its suggestions away, whether you gave the record or a lookup found it, and **Use another RPGGeek
  link…** replaces it.

It's offered for a book with a file of its own or one you own elsewhere (not a pack or an adventure inside a file),
on the web and on a PC connected to its web library, since RPGGeek is asked through bibliotaph.online. Only the
number in the address is sent.

### Books a search by title misses

RPGGeek often names a book after its line ("Gull Rock Saga: Book 2. Spells") and lists no ISBN for it, so a search
for the title a file gives finds nothing. Bibliotaph tries these too, wherever RPGGeek is asked:

- **Inside the line.** When no item is named as the book is, RPGGeek's items for the book's line come back by name
  only, and Bibliotaph checks them on your side against what the book prints: its text never leaves your library. The
  one item whose own part of the name ("Book 2. Spells") is in the book's title or on its cover is taken, and then
  has to agree on a designer or the publisher like any record found by title. A name of one word has to be a line of
  the cover by itself.
- **By title when the ISBN isn't listed.** A book whose ISBN or product code RPGGeek doesn't know is searched for as
  a book printing none is, with the same checks.
- **From the books beside it.** Once one book in a folder has an RPGGeek record, found or
  [given by its page](#look-up-from-an-rpggeek-link), the other books in the folder are also checked against the
  rest of that record's game on RPGGeek, whose books needn't hold the line's name at all.
- **Asking instead of dropping.** When two or three items might be the book (two lines' *Game Master's Screen*, or a
  name the cover prints only in pieces), each gets a **Same book?** card in Needs review. **Same book** on one
  settles the others from that catalogue as not this book; Undo brings them back. More than three and none is asked
  about.

A book's details show what its last lookup did, under **Online lookup** at the end of **Details**: the ISBN, product
code or titles it was looked up by, the lines searched, where it was asked, how many items were found by name, and
which record it uses or Needs review asks about.

Some files aren't books at all. A file printing no ISBN or product code isn't looked up when its **Type** is only
Map pack or Handout, or, with no type yet, when it has a page or two or its name says what it is ("character
sheet", "battle map", "handout", "pregens", "tokens"). Its details say **It looks like a map, a sheet or a handout
rather than a book, so it isn't looked up**, it doesn't count as not found, and it's looked at again when its title
or the catalogues change. Give it a book's type and it's looked up the next time the app starts.

To try the lookup again without touching the books already found, click the button at the end of **Looking up
online** in **Settings > Processing** and choose **Only the N not found**. On Windows the question's **Yes** is only
those and **No** is every book.

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

**Goes with**, at the end of the same dialog, sets the book the ticked maps, handouts and packs go with, all at once:
type a few words of the book's title, pick it, and each ticked card takes the book's values (see
[Maps and handouts that go with a book](#maps-and-handouts-that-go-with-a-book)). **No book** takes them away from the
book they had. When the cards go with different books it says **Mixed**, with how many go with which, and **Review
changes** warns when it replaces a book you chose yourself.

## Needs review

**Needs review** in the sidebar holds what Bibliotaph would like you to look at. It's never in the way: every book is
usable while its card waits. It has two lists:

**Metadata suggestions**, such as:

- A field where the sources disagree, or a book with no useful title. Each button sits under the value it keeps:
  **Keep current** under what the book has, **Accept** under the suggestion, or **Edit** to type your own value.
- A new term the AI used that your vocabulary doesn't know. Add it, file it under an existing term, or reject it. A
  list you'd rather let the AI grow by itself, such as campaign settings, can take its new names at once instead:
  see [Vocabulary](#vocabulary).
- **Same setting?** Two or more terms in a list that may be the same thing under different names, "Greyhawk" and
  "Grayhawk" say, found by comparing the names or by the AI: see [Find overlaps](#find-overlaps). Pick the one to
  keep and **Combine**, or **Keep apart**.
- **New version?** A file that looks like another version of a book you have: most of its pages are the book's, it
  prints the book's ISBN as its own, or it has the book's title and publisher (from a lookup too, once one names it).
  Two files are never joined by an ISBN alone, since publishers keep one ISBN across corrected printings. The card
  says which it is and names both files with their pages, and you choose which one the book opens: each button names its file, such as **Handbook_v2.pdf is current**,
  either way one card for both files. **Separate book** keeps them apart. Nothing is picked until you click one. You can change it later with **Make current** under
  the book's copies in its details.
- **Image pack?** "Make these 12 images one card?": **Make a pack** or **Keep separate**.
- "You own *The Sunken Abbey* elsewhere. Is this its file?": **Same book** or **Separate book**.
- **Same book?** "RPGGeek's record for its ISBN is of *The Sunken Abbey*, which isn't printed in it. Is it this
  book?" (or "for its product code"), for a record from RPGGeek, Open Library or Google Books whose title the book doesn't print, with what the
  record says: its title, authors, publisher, year and what RPGGeek files it under. For AdventureLookup, which is
  searched by title: "AdventureLookup has a record of *The Sunken Abbey*, the title it prints, but not its authors,
  publisher or page count. Is it this book?", with the record's page count too. **Same book** fills them in at
  once, as that catalogue's suggestions rather than your own values, so they beat the AI's guesses and anything you
  set yourself stays; the book uses that record from then on. **Not this book** means the record is never used for
  it. A book with two files is asked once. Books looked up before this card existed are looked up once more to ask.
- Copies of a book that say different things, and pages that moved in a new version, with **Use this page**.
- A compilation that seems to hold several adventures (see below).
- **Collection?** "Make a collection “Ashen Vale” from these 12 books?" for a folder with three or more books in it
  (see [Collections from folders](organizing.md#collections-from-folders)): **Accept**, **Not now** or **Never**.
- **Add to collection?** "Add these 4 books to “Ashen Vale”?" for books that share what most of a collection's books
  share (see [Books for your collections](organizing.md#books-for-your-collections)).
- **Goes with a book?** "Does it go with *Harbor of Shadows*?" or "Which book do these 6 files go with?", for maps,
  handouts and packs whose book Bibliotaph isn't sure of, with why each book is suggested ("its name carries “Harbor
  of Shadows”"). The files in one folder that could go with the same books share a card. **Goes with …** on a book
  links them, and **Not from these books** leaves them as they are and never suggests those books for them again
  (see [Maps and handouts that go with a book](#maps-and-handouts-that-go-with-a-book)).

The cards that offer to make something (a collection, books for a collection, an image pack, a folder rule, or cards
for a file's adventures) have a **×** in their top right corner. It closes the card with its "don't ask again" answer,
the same as **Never**, **Keep separate**, **No thanks** or **Not split**, so that suggestion doesn't come back, though
a new one can (a book that fits the collection later, say). **Undo** on the closed card brings it back.

To check a book before you answer, click the window button beside its title (or middle-click the title, or press
**Shift+Enter** on it). The book opens in a window of its own, at the page the suggestion's evidence is on, and Needs
review stays where it was, so you can read and decide side by side, on a second monitor too. The window is shared:
opening the next card's book shows it in the same window rather than another. Clicking a title still opens the book
in the main window.

When there's more than one kind, **Show** narrows the list to one: New terms, Terms that may be the same, New versions,
Image packs, Owned elsewhere, Same book?, Goes with a book?, Adventures, Collections from folders, Books for collections and so on, or the suggestions for one field
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

WebM videos, such as the animated versions of a map, count as images here, so a folder of maps and their animated
versions is one pack. A video's cover is the image beside it whose name is most like its own (map makers ship a still
of each animated map); with no image beside it, its card shows the placeholder.

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

## Maps and handouts that go with a book

A map, a handout, a character sheet or a pack of images usually comes with a book. Once Bibliotaph knows which, the
file **goes with** that book: it takes the book's game system, edition, publisher, series, year, setting, authors,
levels, players and your tags, as suggestions ranked above lookups and the AI, so it turns up wherever the book does.
It keeps its own title and type, and a value you set on it yourself always stays. A tag you add to the book reaches
its maps too.

Bibliotaph looks for the book a few minutes after it starts, and again whenever files arrive or move, from what the
library already knows:

- **Its pages are in the book**: a handout printed from it.
- **It came in the same ZIP** as the book, the only book in it.
- **It sits beside the book**, the only book in its folder, or in a folder of extras under it, such as **Maps**,
  **Handouts**, **Pregens** or **VTT assets**.
- **Its name carries the book's** title, product code or initials ("LMoP handouts").
- **The AI read the book's title** on its pages, when the AI is on.

The first three link the file at once, as a suggestion. A name, the AI, or a folder of two or three books only
suggest it, and Needs review asks with a **Goes with a book?** card. A file counts when it isn't a book: an image, a
pack, or a PDF typed as a map pack, handout or character sheet, or of a page or two, or named like one ("character
sheet", "battle map"). **Character sheet** is a type of its own for that.

In a map's details, **Goes with** names its book and why. **Keep** makes the suggestion yours, so later passes leave it
alone; **Change…** picks another book (any book, an adventure inside a file, or a book you own elsewhere); **Not from
this book** takes it away and never suggests that book for it again. **Choose a book…** links a file nothing was found
for. A book's details list what goes with it under **Maps and handouts**, each a click away. Every change has
**Undo**, and your choices reach your other PCs and the web library with sync.

While you browse, maps and handouts stay inside their book, as adventures do: the book's card says how many go with it
("12 maps and handouts") and a search still finds them, each card saying which book it goes with. **Maps and
handouts** in the Library's **More filters** shows them as cards of their own instead, or **Hide** leaves them out
altogether. To link many at once, use **Goes with** in [Edit many books at once](#edit-many-books-at-once).

## Books you own elsewhere

Own a book in print or on a virtual tabletop? In the Library, click **Add** and choose **Add a book I
own elsewhere…**. It gets a card with no file, so it shows up in searches alongside the rest, with a
badge on its cover saying where it is. **Also own** records where (print, a VTT, or your own words) on any card, and
can take the book's page online, such as "D&D Beyond https://…", which **Open** then opens. If a file with that title
turns up later, Needs review asks whether it's that book's file.

With no file there's no page to make a cover from. **Choose a cover image…**, in the Add dialog or the book's details,
uses a JPG, PNG or WebP picture of your own: Bibliotaph keeps a cover-sized copy, so the picture itself can move or go.
**Remove the cover** goes back to the online library's cover, or none. Both kinds of cover are kept with your own
data and reach your other PCs and the web library with the rest of your library.

Books you own on D&D Beyond or Demiplane, or bought from Humble Bundle or DriveThruRPG, can be imported all at once
with **Import from an online library…** on the same **Add** menu. See [Online libraries](online-libraries.md).

A book you own elsewhere is looked up online too, by its title, on RPGGeek and AdventureLookup, wherever they're
asked: after an import, when the app starts, and when you look books up again. With no pages to check, a record is
used when it has the book's title and its publisher; one with only the title gets a **Same book?** card. Its details
take **Look up from an RPGGeek link…** as a file's do.

## Check a download

Before copying a new bundle into your library, **Check a download…** under the Library's **Add** button tells you
which files you already have: **Check a folder…** or **Check a ZIP…**. Each file is marked as already owned, another copy, maybe a new version,
or owned elsewhere. Nothing is copied, moved or added. **Copy list** copies the results to paste somewhere else.

## Vocabulary

**Settings > Vocabulary** lists the game systems, types, themes and other terms your books are filed under, with the
other names each goes by ("5e" for "D&D 5e", say). Add terms, rename them, and add or remove other names. Folder names,
searches and what you type all go through these names.

**New names from the AI**, at the top of each list the AI can add to (everything but editions), chooses what happens
when the AI, or RPGGeek, uses a name the list doesn't have. **Ask first**, the default, puts a card in Needs review, one per name,
and the books that use it wait for your answer. **Add them** puts the name straight into the list, and those books get
it as an ordinary suggestion; choosing it also adds that list's names already waiting in Needs review, so a long list of
campaign settings clears in one click. A name you rejected before stays rejected either way, and a name the AI added
can be renamed, given other names or filed under another term here like any other. Adding names doesn't re-read the
books the AI has already read; the button at the end of **Cataloguing with AI** in **Settings > Processing** reads
every book again (see [AI](ai.md#day-to-day)).

### Find overlaps

A list grows two names for one thing over time: "Forgotten Realms" and "The Forgotten Realms", "Greyhawk" and
"Grayhawk", "Wizards of the Coast" and "WotC", "Dark Sun" and "Athas". Bibliotaph looks for those in two ways, and
each group it finds is a card in [Needs review](#needs-review), **Same setting?** say, listing the terms with how many
books use each and why they were paired. Nothing changes until you answer a card.

- **By their names**, on every list: the same name but for small words, brackets or a plural; spelt almost the same;
  one name is the other's initials; one name is the other with a word added. This needs no AI, so the web can tidy a
  list too.
- **By the AI**, on the lists the AI can add to (every list but editions), whenever it is on: the model is shown the
  list's names, with each term's other names, and asked which refer to one and the same thing, an abbreviation and
  what it stands for, another spelling, a translation, an older name of the same line. It runs in the background once
  a list changes or the AI has gone quiet after cataloguing, gives way to anything you ask the AI, and is asked only
  about names it hasn't seen, so an unchanged list costs nothing. Two terms that came with Bibliotaph are never paired.

The terms came from wherever they came from: the AI, a lookup in Open Library, Google Books or RPGGeek, a folder
name, or your own typing. On the card, the term to keep is picked already, the one most books use, and **Combine**
folds the others into it: their names become its other names, every book filed under them moves to it, and anything
you rejected or any folder rule for them follows. **Undo** puts everything back, and the card comes back with it.
**Keep apart** says they are not the same thing, and that pair is left alone from then on, whichever way it is found;
a group combined one at a time, or kept apart, is gone from the list, and the AI isn't asked about it again.

**Find overlaps**, under New names from the AI on each list in **Settings > Vocabulary**, compares the list's names
now, has the AI compare them again if it is on, and says how many groups wait, with **Open Needs review** beside it.
The pairs kept apart are listed under it, each with × to have it offered again.

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
