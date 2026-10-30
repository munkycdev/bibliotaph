# Getting started

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Getting started

## Install

You need Windows 10 (version 1809 or later) or Windows 11, on a 64-bit PC.

1. Open the [latest release](https://github.com/munkycdev/bibliotaph/releases/latest) page on GitHub. You don't need
   a GitHub account to download.
2. Under **Assets**, click **BibliotaphApp-win-Setup.exe**.
3. Run the file you downloaded. Bibliotaph installs for your Windows account only, so there's no administrator
   password to type, and it opens when it's done.

Bibliotaph isn't code-signed, so Windows may show "Windows protected your PC" the first time. Click **More info**,
then **Run anyway**.

From then on, open Bibliotaph from the Start menu.

### Updates

An installed Bibliotaph checks for a new version when it starts and once a day, and downloads it in the background.
When it's ready, the bottom of the sidebar says **Update ready: restart to update**. Click it and Bibliotaph closes,
updates and opens again. If you don't, the update goes in the next time you start it. Nothing restarts by itself.

### Uninstall

Uninstall Bibliotaph from Windows' **Settings > Apps** like any other app. Your books aren't touched. Your catalog
(corrections, notes, collections, binders) is kept in `%LOCALAPPDATA%\Bibliotaph`, so it's still there if you install
again. Delete that folder if you want it gone too. It also holds the built-in AI's model, if you downloaded one, and
the sign-ins of Bibliotaph's browser.

## The tour

The first time Bibliotaph opens, a short tour points at the parts of the window as you need them, in two chapters.
On an empty library it shows only where to add your first folder and, once one is in, the sidebar's line about what
Bibliotaph is doing. The second chapter waits until your first books show, then walks through them: a book's cover,
the Library, books you own online, the search box, Needs review, Collections and Binders, and Settings. **Next**,
**Back** or the arrow keys move through it, and **Skip tour** or **Esc** ends it for good. The dimmed window still
lets you click what a step points at.

To see it again, open **Settings > Preferences** and click **Show the tour again**: it starts on Home, with the chapter
your library is ready for.

## Add your folders

The first time Bibliotaph opens, Home says **Welcome to Bibliotaph**.

1. Click **Add a folder** and pick a folder that holds RPG files. Pick the top folder: everything under it is
   included.
2. Bibliotaph counts what's inside (PDFs, e-books, images, ZIPs) and shows it. Click **Add to library**.
3. Add more folders the same way. You can add or remove folders later in **Settings > Library**.

Bibliotaph reads PDF, EPUB, Kindle (MOBI, AZW and AZW3), JPG, PNG and WebP files, including ones inside ZIPs, which are never unpacked next to
themselves. Folders on USB drives, network drives and OneDrive all work; see
[OneDrive, drives and other PCs](onedrive-and-other-pcs.md).

Removing a folder keeps your notes, collections and corrections, and never deletes a file.

Own books on D&D Beyond, Demiplane, Humble Bundle or DriveThruRPG? They can join your library too; see
[Online libraries](online-libraries.md).

### Folder names count

Folder names like "D&D 5e", "Adventures" or "Maps" are read as suggestions for the books inside. You don't have to
reorganize anything first, but a tidy folder or two helps. If a folder name means something else in your library,
fix the books it misled as you come to them: after you correct one, Bibliotaph offers to fix the same guess on the
folder's other books (see [Cataloging](cataloging.md#cataloged-wrong)).

## While it reads your books

Home shows **Indexing your library.** with progress, and books appear in the Library as soon as they're found. You can
search and read straight away: a book becomes searchable as soon as its text has been read, while the rest carry on.

Bibliotaph finds the files, reads their text, makes covers, reads scanned pages with Windows' text recognition and,
if you turn it on, asks AI to fill in the details. A big library can take hours the first time. After that, only new
and changed files are read.

- It only works while Bibliotaph is open. Closing the app stops it; reopening carries on where it left off.
- The bottom of the sidebar says what it's doing now. A long status wraps to a second line, and pointing at it
  shows all of it. Click it to open **Settings > Processing**.
- **Settings > Processing** shows each step and the time left, from finding files to the AI's scan of whole books, with
  **Pause indexing**, **Pause reading scans** and **Pause AI**. The button at the end of a row runs that step again
  for every book: **Finding files** looks through your folders now, and **Looking up online**, **Cataloguing with AI**
  (with your lists as they stand now, say after adding names) and **Scanning whole books** ask first, since each takes
  a pass over your library. Values you've accepted, typed or rejected stay as they are.
- Files that couldn't be read are listed in **Needs review**, under **Files needing attention**.

### Set up AI, or not

After your first folder, Home asks **Fill in the catalog with AI?** Click **Set up AI** to do it now, or **Not now**
to do it later from **Settings > AI**. Bibliotaph is fully usable without it. See [AI](ai.md).

## Finding your way around

The sidebar on the left has the main pages:

| Page | What's there |
| --- | --- |
| **Home** | Where you left off: **Continue preparing** (your next binder), **Recently opened**, **Recently added** and your pins. |
| **Library** | Every book, map and handout, as covers or a list, with search, filters and the details panel. |
| **Collections** | Books you've gathered around a campaign, a setting or an idea. |
| **Binders** | The books and pages for each session, in order. |
| **Summary** | Your library by the numbers, each figure a link into the Library. See [Your library in numbers](summary.md). |
| **Needs review** | Suggestions to check and files that need something from you. The number is how many are waiting. |
| **Pinned** | Anything you've pinned: a book, Favorites, a collection, a binder or a saved search. |
| **Settings** | At the bottom: your folders, online libraries and web library, how books are catalogued, and your own preferences. |

The **Collapse the sidebar** button beside Settings, or **Ctrl+B**, folds the sidebar down to its icons for more room;
point at an icon for its name. The monogram stays at the top, and the same button, now under Settings (**Expand the
sidebar**), opens it again. Bibliotaph
remembers which way you left it.

The search box at the top is always there. **Ctrl+K** jumps to it from anywhere.

In the Library, double-click a cover to open the book. Click it once to see its details on the right, with **Open**
and the rest of what you can do with it. Click anywhere outside the details, or press **Esc**, to close them.
Right-click a cover for everything else you can do with it, including **Open** and **Open in new window**.

**Ctrl+/** lists every keyboard shortcut. **Settings > Preferences** switches between light, dark and Windows'
own setting, and its **Start on** chooses whether Bibliotaph opens on Home or the Library.

**Settings** lists its sections in groups, as on the web: **BOOKS** holds **Library** (your folders), **Online
libraries** and **Devices** (your web library); **CATALOGUING** holds **Processing**, **AI** and **Vocabulary**; and
**YOU** holds **Preferences** (light or dark, where Bibliotaph opens, what waits for review and the tour),
**Passwords** and **Backup**.

---

Next: [Searching](searching.md)
