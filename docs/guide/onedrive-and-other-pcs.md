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
synced folder: to use your library on two PCs, see [A second PC](#a-second-pc) below.

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

## A second PC

To use your library on two Windows PCs, say a desktop and a laptop, set up **Sync with another PC** in
**Settings > Backup** on both:

1. Click **Choose a folder…** and pick a folder both PCs can see, such as one in OneDrive.
2. Each PC writes a small file of its own there, and reads the other's. Changes go across within a few seconds of the
   folder syncing, or straight away with **Sync now**.
3. The status line says when it last merged, such as "Last merged from DESKTOP-2 at 09:14.", or what went wrong.

What travels: your corrections, hearts, notes, collections, saved searches, binders (with any pages online in them),
pins, vocabulary, books you own elsewhere (imported ones too) and your answers in Needs review. Where both PCs change
the same thing, the newer change wins; changes to different things are all kept.

What doesn't: your library folders, where you left each book, settings, the words your tags are found by (the tags
on your books do travel), the sign-ins of Bibliotaph's browser, whether a collection made from a folder still follows
that folder (the collection itself travels), and the books you said Never to for a collection. Add
your folders on each PC. Books are matched by their content, so the same file in a different place on each PC is still
the same book.

**Stop syncing** ends it on that PC. Only the sync files go in that folder, never Bibliotaph's catalog itself.

---

Previous: [AI](ai.md) · Next: [Questions and answers](faq.md)
