# Bibliotaph on the web

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Bibliotaph on the web

Bibliotaph is coming to the browser at [bibliotaph.online](https://bibliotaph.online), for game masters whose books
live in cloud storage. It looks and works like the Windows app. It's being built in steps and is invite-only: today you
can accept an invitation, sign in, invite others, connect your OneDrive and add library folders from it. Seeing and
reading your books in the browser comes next.

## Join with an invitation

An invitation is a one-time link that expires after 14 days. Open it, choose **Continue with Microsoft** or **Continue
with Google**, and sign in with an account you already have. That makes your Bibliotaph account; the link can't be used
again. Bibliotaph asks the provider only for your name, email address and an account ID, and never sees your password.

Without an invitation, signing in with an account Bibliotaph doesn't know shows **Bibliotaph is invite-only for now.**

## Signing in and out

Sign in at [bibliotaph.online](https://bibliotaph.online) with the same account each time. You stay signed in on that
browser for two weeks after you last used it. **Settings** › **Sign out** ends it.

To sign in with another account as well, such as Google beside Microsoft, choose **Link a Google sign-in** (or
Microsoft) in **Settings** › **Account**. Either one then opens the same library. A sign-in that already belongs to another Bibliotaph account
can't be linked.

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

### Collections

**Collections** gathers books around a campaign, a setting or an idea, as in the Windows app. Its page has a card for
each collection at the top, pinned ones first; **New collection** makes one and opens it.

A collection opens in the Library with its name as the heading and **In** its name on a chip above the books (its **×**
shows your whole library, keeping the search and filters). The search box and filters work within it. Collections
inside it show as cards above its books, and **Only books added here** leaves out the books that are only in those.
Its buttons are **New collection** (one inside it), **Rename**, **Move to…** (into another collection, or to the top)
and the bin, which deletes it: its books stay in your library, the collections inside it move up a level, and
**Undo** on Collections brings it back.

A book's details list its collections under **Collections**: a chip opens one, and its **×** takes the book out.
**Add to collection** offers the collections used last, **Add to a new collection…**, and **Add to another
collection…**, which lists them all and narrows as you type. Each change says what it did, with **Undo**.

The reader and binders are the next steps.

## Your theme

The button at the top right cycles between your device's theme, light and dark, as in the Windows app. The browser
remembers your choice.

## Privacy

Your books stay in your own storage. Bibliotaph keeps your account, your storage connections (encrypted), an index of your
library and the work you do in it. The [privacy policy](https://bibliotaph.online/privacy) says exactly what's kept,
where, and how to have it deleted.
