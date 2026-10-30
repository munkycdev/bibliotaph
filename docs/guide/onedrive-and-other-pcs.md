# OneDrive, drives and other PCs

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › OneDrive, drives and other PCs

## OneDrive

You can add a OneDrive folder like any other. Files that are "online-only" (the cloud icon in File Explorer) have to be
downloaded for Bibliotaph to read them, so:

- When you add the folder, Bibliotaph says how many files are online-only. Files already on your PC are read first.
- Online-only files download one by one as Bibliotaph reaches them. If that would leave less than 2 GB free on the
  drive, it waits, and **Settings > Processing** says so.
- Once Bibliotaph has read a file that was online-only, it hands it back to online-only, the same as File Explorer's
  **Free up space**. The book stays in your library and searchable, and opening it later downloads it as usual. That's
  how a cloud library bigger than your disk gets read, a batch at a time.

That last step is the one time Bibliotaph asks Windows to do something to a file: it only drops the downloaded copy.
The file's content, name, place and dates don't change. Files you already had on your PC, files set to
**Always keep on this device**, and a book you have open are left alone. To keep everything downloaded instead, untick
**Return files to online-only after indexing** in **Settings > Processing**.

Other cloud apps' folders work too, as ordinary folders.

Bibliotaph's own catalog is kept in `%LOCALAPPDATA%\Bibliotaph` on your PC, never in OneDrive. Don't move it into a
synced folder: to use your library on two PCs, see [More than one PC](#more-than-one-pc) below.

## USB and external drives

A library folder on a drive that isn't plugged in is shown as offline. Its books stay in your library, with
everything you did with them, and stay searchable. Nothing is marked as deleted because a drive is missing.

Open one of its books and the reader says which folder and drive it needs. Plug the drive in and click **Try again**.

## When files move

Move or rename files inside your library folders as you like, in File Explorer or anywhere else. Bibliotaph recognizes
them by their content, and the book keeps its card, notes, collections and binder items. That includes ZIPs, image
pack folders, and files on USB drives.

If a whole library folder moves for good, say to a new disk, open **Settings > Library** and click
**Point to its new place** beside it. Its books follow without being read again.

A file that's been deleted stays in your library marked as missing, so nothing you did with it is lost. If it turns up
somewhere else in your library, it's matched up again.

## Network drives

Folders on a network drive or NAS work, but they're slower to read and are treated as best effort: if the network
drops, the folder goes offline like an unplugged drive.

## Backups

**Settings > Backup** has everything:

- **Back up now…** saves a backup file wherever you choose. It starts in the backups folder in Bibliotaph's data
  folder, where **Choose a backup…** looks too. Bibliotaph also makes one by itself at the first start of each week,
  in the same folder, and keeps the last four automatic ones; the ones you save are never deleted. **Open backups
  folder** shows them. A copy on another drive, or in OneDrive, survives a failed disk too.
- A backup holds your catalog: folders, corrections, collections, binders and notes. Your books aren't in it; they stay
  where they are. Passwords for protected PDFs and AI keys aren't in it either; Bibliotaph asks again when it needs
  them. Nor are the sign-ins of Bibliotaph's browser: sign in to your online libraries again after a restore on a new
  PC.
- **Include the search index** makes backups much larger, but a restored library is searchable straight away instead
  of after your books are read again.

**Choose a backup…** then **Restore and restart…** puts your catalog back as it was. Your current catalog is backed up
first.

**Export CSV…** saves a row per book (title, system, type, levels, publisher, tags, collections and where its file is)
for a spreadsheet. **Export JSON…** saves everything in the catalog, for other programs.

## A new PC

1. [Install Bibliotaph](getting-started.md#install) on the new PC.
2. Copy a backup across and restore it with **Choose a backup…** in **Settings > Backup**.
3. Bibliotaph lists your library folders as **Found** or **Not found**. For one that's somewhere else now, click
   **Point to its new place**; for a drive you'll plug in later, **Leave it offline**.
4. Click **Restore and restart…**.

## More than one PC

To use your library on two Windows PCs, say a desktop and a laptop, connect each of them to your
[web library](web.md) in **Settings > Devices**: **Connect…** opens bibliotaph.online in your browser, where you sign
in, check the code the app shows and approve the PC. The section then says whose library it's connected to and when it
last merged, such as "Last merged from bibliotaph.online at 09:14.", and **Disconnect** ends it.

What travels: your corrections, hearts, notes, collections, saved searches, binders (with any pages online in them),
pins, vocabulary, your own tags with the words they're found by, books you own elsewhere (imported ones too) and your
answers in Needs review. A new name the AI proposed is answered once for every device: add it, file it under another
term or turn it down on the web or on any PC, and the same card goes from Needs review everywhere, with the books
that were waiting on it. Terms you kept apart in **Find overlaps** stay apart everywhere too. Packs travel as well: files you **Pack together**,
a pack you split, a folder you answered **Make a pack** or **Keep separate** for, and the cover you chose for a pack
are the same on the web and every PC that has those files. Where two devices made a pack of the same folder by
themselves, they settle on one. So do the files you said are the same book (or a new version of one), a file you
matched to a book you own elsewhere, and **Not the same book**, which parts them everywhere. Adventures you mark in a
file travel with their pages, levels and everything else on them, and so does removing one; your answers to an
adventure proposal and to "You own this elsewhere. Is this its file?" travel too. A change reaches the
web a few seconds after you make it, and your other PCs within a minute or so. Where two PCs change the same thing, the
newer change wins; changes to different things are all kept.

What doesn't: your library folders, where you left each book, settings, the sign-ins of Bibliotaph's browser, whether a collection made from a folder still follows
that folder (the collection itself travels), and the books you said Never to for a collection. Add
your folders on each PC. Books are matched by their content, so the same file in a different place on each PC is still
the same book.

What the AI found in a book is shared with your web library and your other connected PCs as well, so the same book is
read by the AI once, wherever that happens first, and so is the text read from scanned pages. See
[Connecting a Windows PC](web.md#connecting-a-windows-pc).

Bibliotaph used to sync two PCs through a folder you picked, such as one in OneDrive. That's gone: the web library
does the same for every PC, and for the web. The files it left in that folder (in **Bibliotaph sync**) aren't read any
more, and you can delete them.

---

Previous: [AI](ai.md) · Next: [Questions and answers](faq.md)
