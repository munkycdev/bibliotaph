# Searching

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Searching

The search box at the top of the window searches your whole library. Click it or press **Ctrl+K**, and start typing:
results update as you type, in the Library.

## Books and pages

Every search has two tabs:

- **Documents**: the books, maps and handouts that match, by their title, details, notes or text. An adventure inside
  an anthology that matches shows with its book's cover and an **Adventure** ribbon, in place of the book. See
  [Adventures inside a file](cataloging.md#adventures-inside-a-file).
- **Inside documents**: the pages that match, grouped by book, each with a snippet of the page around your words.
  Click **Open page** and the book opens at that page, with your words highlighted. The **+** on a page adds it to
  your current binder.

While Bibliotaph is still reading your library, the results say how much is covered, for example "Searching text in
70 of 84 documents."

## What you can type

| Type | To find |
| --- | --- |
| `chase city` | Both words, anywhere. |
| `"chase through a city"` | That exact phrase. |
| `abbey OR monastery` | Either word. |
| `undead -vampire` | The first word but not the second. |
| `lantern*` | Words starting with "lantern". |
| `type:adventure level:3` | Fields, which narrow by a book's details. Mix them with words. |

### The search guide

When the box is empty, clicking it opens the search guide: every field with what it means and an example. Start
typing a field's name and the guide narrows to it; after the colon, it lists that field's values in name order, with
how many books have each. Pick one with the mouse, or with the arrow keys and **Enter**. **Ctrl+Space** opens the
guide at any time, and **Esc** closes it (press **Esc** again to clear the search).

### Fields

| Field | Finds | Example |
| --- | --- | --- |
| `title:` | Words in the title | `title:abbey` |
| `system:` | A game system, or an edition, by any of its names | `system:5e` |
| `edition:` | An edition of a game system | `edition:pf2e` |
| `type:` | What kind of book it is | `type:adventure` |
| `level:` | A character level or range; `none` or `unknown` | `level:3`, `level:2-4` |
| `players:` | Party size | `players:4`, `players:3-5` |
| `duration:` | One-shot, short, long or campaign | `duration:one-shot` |
| `style:` | The kind of adventure: heist, dungeon crawl, hexcrawl… | `style:heist` |
| `theme:` | A theme or tone, such as horror, in the book or one of its chapters | `theme:horror` |
| `environment:` | Where it happens, in the book or one of its chapters | `environment:urban` |
| `setting:` | A campaign setting, in the book or one of its chapters | `setting:"my homebrew"` |
| `creature:` | A creature the AI found in one of its chapters (also `monster:`) | `creature:owlbear` |
| `publisher:` | Words in the publisher's name | `publisher:mossbank` |
| `author:` | Words in an author's name | `author:doe` |
| `series:` | Words in the series name | `series:chronicles` |
| `store:` | Where you can get it again (also `from:`) | `store:kickstarter` |
| `own:` | Where else you own it: in print, on a VTT, on D&D Beyond | `own:print` |
| `where:` | `here` for books with a file on this PC, `elsewhere` for books you own only somewhere else | `where:elsewhere` |
| `tag:` | One of your own tags | `tag:prep` |
| `favorite:` | `yes` for books with a heart, `no` for the rest | `favorite:yes` |
| `collection:` | Books in a collection (in quotes if it has spaces) | `collection:"Winter campaign"` |
| `folder:` | Part of a folder's name | `folder:maps` |
| `format:` | `pdf`, `jpg`, `png`, `webp`, or `image` for any picture | `format:image` |

Any field also takes `unknown`, for books where nobody knows yet: `system:unknown` finds books still to be sorted out.
A level filter only matches books whose levels are known, unless you say otherwise.

If something you type can't be understood, the search says what and where, and keeps what you typed.

## Ask in plain English

With AI set up, the sparkle button beside the search box switches it to **Ask**. Type what you're after the way you'd
say it, for example "Mothership, level 4, jungle world with terraforming", and press **Enter**. Typing doesn't search
while Ask is on; nothing happens until you press **Enter**.

The AI turns your request into a search, and a panel under the top bar shows what it made of it as chips: each field
with its value, which you can change, and the words it kept, each with an × to drop it. When it had to change
something, it says what and why, for example "Mothership has no character levels in your library, so level 4 was
ignored." Click **Search** and the chips go into the search box as an ordinary search, which you can then edit like
any other.

Ask only uses fields and words you could have typed yourself, and only your own words: it never adds a game system,
level or tag you didn't ask for. Click the button again to go back to ordinary searching. Without AI, Ask says it
needs AI, with a link to [set it up](ai.md).

## Ask your library a question

In Ask mode you can also ask a question about what's *in* your books, such as "how does grappling work?" or "which
adventures have a lich in a crypt?". Type it and press **Ctrl+Enter**, or click **Answer from my books**. Bibliotaph
looks through the pages of your books, picks the few that answer it best (no more than three from any one book), and
the AI answers from those pages only.

- **Each claim cites its book and page**, like *[The Sunken Keep, p. 112]*. Click it to open that book at that page,
  with the quoted words marked. The pages it read are listed under the answer, and each opens its page too.
- **It asks the books the Library shows.** Filter the Library first (by game system, type, setting, theme and so
  on, or with fields such as `system:` and `setting:` in the search box) to ask only those books. The panel says which
  filters it used. The words you searched for don't narrow it: the question has its own.
- **When your books don't cover it, it says so** instead of making something up, and names the pages that came
  closest.
- Books in a folder [kept from AI](ai.md#day-to-day) are never read or sent.

Pages are found by their words, and by their meaning once your library has been read for it (see
[AI](ai.md#reading-your-library-for-meaning)). Until then, books that haven't been read for meaning still take part by
their words.

## Filters

**Filters** in the Library's toolbar opens a panel of drop-downs. The four you'll use most come first: **Game system**,
**Type**, **Character level** (with **Include unknown levels**) and **Players**. **More filters** opens the rest, in
four groups:

- **The adventure**: **Style** and **Duration**, once some book has one.
- **About the book**: **Setting**, **Themes**, **Environments**, **Publisher** and **Edition**, each a box you type in,
  once some book has one.
- **Where it lives**: **Source folder**, **Also own** and **Get it from**.
- **Housekeeping**: **Kind** (books, images, packs, on this PC, or owned elsewhere), **AI** (whether a model has
  read it), **Copies** (books in more than one file), **Adventures inside files** (**Inside their book**,
  **Show as cards** or **Hide**; see [Adventures inside a file](cataloging.md#adventures-inside-a-file)) and
  **Collection** (**In a collection**, or **Not in a collection** for the books you haven't put in one yet).

Each drop-down lists its choices by name, with how many books each has and **Unknown** last.

While they're folded away, **More filters** says how many of them are on, such as "More filters · 1".

The **About the book** boxes are for fields with too many values for a menu. Start typing, say "marsh" in
**Environments**, and the box lists the values in your library that match, with how many books have each; other names
count too, so "5e" finds the 5th edition. Click one, or pick it with the arrow keys and Enter, and it becomes a chip
under the box. Several chips in one box find books with any of them: two settings show the books set in either. Boxes
work together like the other filters, so a setting and a theme show only the books with both.

The filters that are on show as chips above the results, whether the panel is open or not, and the button counts them:
**Filters · 2**. Click the × on a chip to turn that filter off, or **Clear filters** beside the chips, once there are
several, to turn them all off. A filter and a search work together.

The toolbar also has the order (**Best match**, **Recently added**, **Recently opened**, **Title A–Z**,
**Publisher A–Z**) and a switch between covers and a list.

## Saved searches

When you've got a search, filter or order you'll want again, click **Save search** and give it a name. It keeps the
search and every filter, the **About the book** chips too, not the results, so new books that match show up in it.
It's pinned to the sidebar straight away.

Open a saved search and change something, and the Library says "You've changed this saved search." with
**Update saved search** and **Save as new**. The **Saved searches** button in the Library lists every one, pinned or
not, with rename and delete (and **Undo**).

## Search inside a collection or binder

Inside a collection, the Library shows only that collection's books, and a chip above the results says so. Click its
× (**Show your whole library**) to search everything again. A binder's **Show these books in the Library** does the
same for the books in a binder.

---

Previous: [Getting started](getting-started.md) · Next: [Reading](reading.md)
