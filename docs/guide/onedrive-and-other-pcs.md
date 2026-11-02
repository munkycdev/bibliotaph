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

Other cloud apps' folders work too, as ordinary folders. To read OneDrive, Dropbox, Google Drive, Box or WebDAV
without their apps, see [Storage without its app](#storage-without-its-app).

Bibliotaph's own catalog is kept in `%LOCALAPPDATA%\Bibliotaph` on your PC, never in OneDrive. Don't move it into a
synced folder: to use your library on two PCs, see [More than one PC](#more-than-one-pc) below.

## Storage without its app

Bibliotaph can also read your books straight from OneDrive, Dropbox, Google Drive, Box or a WebDAV server (Nextcloud,
ownCloud or a NAS), with no sync app on the PC and nothing downloaded to keep, the way [the web library](web.md#connecting-your-storage)
does. It's the same storage and the same folders, connected on this PC.

At the top of **Settings > Library**, under **Storage**, choose **Connect OneDrive**, **Connect Dropbox**,
**Connect Google Drive** or **Connect Box**. Your browser opens at the provider's own page: sign in with the account
that holds your books and let Bibliotaph read your files. When the browser says you can close the tab, the account is
listed in Settings with what it was allowed, such as "Allowed to read your files and keep access while you're signed
out"; point at that line for the provider's own words. Bibliotaph asks for nothing that writes, so it can't change,
move or delete your files. If you change your mind while the browser is open, **Cancel** stops waiting for it.
Box only lets Bibliotaph in through bibliotaph.online, which adds Bibliotaph's Box key to the request and keeps nothing
about it; you don't need an account there.

**Connect WebDAV** asks for the folder's address, your user name and a password instead, as the web library does. Here
the server can be on your own network too, such as a NAS at http://192.168.1.20/books/; an address that starts http://
sends the password unencrypted, so use one only at home. Make an app password for Bibliotaph where your server offers
one.

What lets Bibliotaph in is kept in Windows Credential Manager, never in Bibliotaph's files or backups, so after a
[restore on a new PC](#a-new-pc) you connect your storage again and its folders come back.

Then **Add from storage** under the folders, or **Add a folder from storage…** in the Library's **Add** menu, picks a
folder: choose the account if you connected more than one, click down to the folder your books are in, and choose
**Use this folder**. You see what it holds before you add it, as with any folder. Each book is downloaded to be read,
and its copy deleted straight after. Bibliotaph looks for changes in your storage every five minutes, and
**Look for changes** looks now.

Opening a book downloads it again, and the reader shows how much has come. That copy is kept for next time, with the
web library's, up to the **Room for downloads** in **Settings > Devices**; past that, the ones you read least recently
go. To read a book without a connection, such as at the table, tick **Keep offline on this PC** in its details while
you're online: it stays until you untick it, even if you disconnect the storage. **Open in another app** opens the
downloaded copy, and **Free up space** in **Settings > Devices** deletes the downloads not kept offline.

If a provider stops letting Bibliotaph in, for example after you change your password or remove its access, the
account shows **Needs connecting again** with **Connect again**, and its folders are offline until you do; their books
stay in your library and searchable. **Disconnect** forgets the account's access on this PC. Its folders go offline,
and everything you did with their books stays for when you connect it again.

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
adventure proposal and to "You own this elsewhere. Is this its file?" travel too. So does what you chose for each
book: the page you used as its cover, **Forget its text** (and **Read it again**), a **Cataloged wrong…** you left
open, the collections you said Never to, the cover picture you chose for a book you own elsewhere or the one it was
imported with from an online library, and its Ask this book conversation. A book opens where you last left it, on whichever device you read it last. A change reaches the
web a few seconds after you make it, and your other PCs within a minute or so. Where two PCs change the same thing, the
newer change wins; changes to different things are all kept.

Settings about your library travel between your PCs too: the theme, whether every suggestion goes to Needs review, the
page you start on, the Library's order while browsing and searching, Packs or Each file, site icons and how books owned
online open. The web library takes the first ones it has (Needs review and the start page); its own Library keeps its
order and view in your browser.
So do your answers about folders, the folder rules you turned on or off and the collections made from folders, for the
web library's folders (on the web and every connected PC) and for a folder at the same place on two PCs.

What doesn't: your library folders, the settings about each device (AI, downloads, backups and where windows were),
and the sign-ins of Bibliotaph's browser. Add
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
