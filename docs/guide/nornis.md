# Using Bibliotaph with Nornis

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Using Bibliotaph with Nornis

[Nornis](https://nornis.app) is a campaign app for game masters and their players, from Bibliotaph's maker.
Connected, Bibliotaph becomes where your Nornis sourcebooks live: your books stay in your own storage, Bibliotaph
catalogues and indexes them, and Nornis asks Bibliotaph when it needs them. This works with your library at
[bibliotaph.online](web.md); a Windows PC connected to it sees the same collections.

## Connecting Nornis

You connect from Nornis, not from Bibliotaph: in Nornis, choose to connect Bibliotaph. Your browser comes to
bibliotaph.online, which signs you in with your MunkyC sign-in (the one Nornis uses, so usually without asking) and
goes back to Nornis when it's done. If you don't have a Bibliotaph account yet, connecting makes one, without an
invitation, while there's room for new accounts; when there isn't, Nornis says so.

Connecting does three things:

- It signs you in to Bibliotaph as the same person you are in Nornis. If you already had a Bibliotaph account with that
  sign-in, it's that one, even if you were signed in to another account in that browser.
- It connects Nornis as storage, so Bibliotaph can read the books you upload to your worlds in Nornis. Bibliotaph only
  reads them; it never changes anything in Nornis.
- It makes a collection called **Nornis** at the top of your collections, where each of your worlds will have a
  collection of its own. If you delete it, connecting again makes a new one.

## Nornis in Settings

**Settings** › **Library** lists Nornis under **Storage** with your other connections. It says what Nornis can do,
"Nornis can search and share books in your Nornis collections, and Bibliotaph reads files you uploaded to Nornis", and
what it was allowed, "read the books you uploaded to Nornis and keep access while you're signed out". There's no
**Connect Nornis** button there, since connecting starts in Nornis. **Resync Nornis worlds** asks Nornis to send all
your worlds again, which brings back a world's collection if it's missing.

The **×** disconnects it. Bibliotaph forgets its access to Nornis, and Nornis can no longer reach your library: it
shows **Reconnect Bibliotaph**, which connects again. Your collections and their books stay. If Nornis stops letting
Bibliotaph in, the connection shows **Needs connecting again**, with **Connect again**.

## Your worlds as collections

Each world you create in Nornis gets a collection of its own inside **Nornis**, named after the world and marked
**Nornis world**. Put a world's sourcebooks in its collection, or in collections inside it, to make them that world's.
Nornis keeps these collections up to date as your worlds change, even while your library isn't open:

- **A new world** gets its collection. Worlds you made before connecting arrive when you connect.
- **Renaming a world** renames its collection, unless you renamed the collection yourself in Bibliotaph: then it
  keeps your name from then on.
- **Deleting a world** marks its collection **World gone**. Nothing is deleted: the collection and its books stay,
  and you can delete it yourself when you no longer want it. Nornis no longer sees what's in it.
- **Deleting a world's collection** in Bibliotaph doesn't change the world. **Resync Nornis worlds** in Settings makes
  it again, empty.

Only a world's creator has a collection for it; players reach books through the game master's world.

## What Nornis can see

Nornis reaches your library only while you're connected, and only for you: it can't reach anyone else's library, and
it reaches yours only through what's described here. Everything it sees is in a world's collection, or in collections
inside it, and only while the world isn't gone:

- **The world's books**, for Nornis's Library page: each book's title, subtitle, type, system, number of pages, format
  and cover.
- **Pages that answer a question.** When you or your players ask Nornis about a world, Bibliotaph finds the pages of
  that world's books that answer it, as **Ask your library** does: by their words, and by their meaning too when your
  library has an embedding model in **Settings** › **AI**. Nornis gets up to ten pages, each cut to its most useful
  part, and its own AI answers from them. If nothing in the books answers the question well, Nornis is told so and
  gets no pages, so it doesn't make an answer up. When you're choosing excerpts in Nornis, it gets the closest pages
  anyway.
- **The text of pages you choose**, to file an excerpt in Nornis, up to 40 pages at a time.

Books in a library folder you keep from AI (**Settings** › **AI**) never go to Nornis: their pages aren't searched
and their text isn't sent, though they're still listed. A book outside every world's collection is invisible to
Nornis, as is anything else in your library.

If your library is closed when Nornis asks, Bibliotaph starts opening it and Nornis tries again a few seconds later.
Nornis asks for this as soon as someone opens a world, so it's usually ready by the time anyone asks a question.

## Sending pages to Nornis

On [bibliotaph.online](web.md), the reader has **Send to Nornis** for a book that's in one of your worlds'
collections. It opens with the page you're on, or an adventure's pages, as From and To; change them to the pages you
want, up to 40, give the excerpt a label if you like, and pick the world if the book is in more than one. **Open in
Nornis** opens Nornis in a new tab, where it reads those pages and asks you to confirm the excerpt. Bibliotaph sends
Nornis nothing itself.

A binder's item of pages has **Send to Nornis** in its menu (right-click it while editing the binder), with the item's
label; a whole book in a binder isn't an excerpt. A book in a folder you keep from AI shows the action greyed out,
since its pages never go to Nornis.

## Players reading your books

When you share a book with your players in Nornis, they can read it in Bibliotaph's reader without an account of their
own. Nornis decides who may read it; Bibliotaph checks that the book is still in the world's collection. Nornis sends
the player to a link that works once and only for two minutes, and the player's browser can then read that book for an
hour. A minute before the hour is up, the reader goes back to Nornis at the page they're on, and Nornis checks again
and, if they still may, opens the book again there.

The player reads view-only: their reader has no notes, Ask, binders, favourites, pins, Keep offline or Pop out, and
says **Shared by** you **in** the world, with a link back to Nornis. Your library keeps nothing about them: their place
isn't kept, the book isn't added to your recently opened books, and nothing they do changes your library. Take the book
out of the world's collection, or delete the world, and they can't read it any more, straight away.

The reader reads the file from your storage, as yours does, so a determined player could save it during their hour.
Nornis keeps a record of who opened which book.
