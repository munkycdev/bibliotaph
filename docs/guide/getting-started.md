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

## Add your folders

The first time Bibliotaph opens, Home says **Welcome to Bibliotaph**.

1. Click **Add a folder** and pick a folder that holds RPG files. Pick the top folder: everything under it is
   included.
2. Bibliotaph counts what's inside (PDFs, images, ZIPs) and shows it. Click **Add to library**.
3. Add more folders the same way. You can add or remove folders later in **Settings > Library**.

Bibliotaph reads PDF, JPG, PNG and WebP files, including ones inside ZIPs, which are never unpacked next to
themselves. Folders on USB drives, network drives and OneDrive all work; see
[OneDrive, drives and other PCs](onedrive-and-other-pcs.md).

Removing a folder keeps your notes, collections and corrections, and never deletes a file.

Own books on D&D Beyond, Demiplane, Humble Bundle or DriveThruRPG? They can join your library too; see
[Online libraries](online-libraries.md).

### Folder names count

Folder names like "D&D 5e", "Adventures" or "Maps" are read as suggestions for the books inside. You don't have to
reorganize anything first, but a tidy folder or two helps. If a folder name means something else in your library,
switch it off under **Folder names** in **Settings > Library**.

## While it reads your books

Home shows **Indexing your library.** with progress, and books appear in the Library as soon as they're found. You can
search and read straight away: a book becomes searchable as soon as its text has been read, while the rest carry on.

Bibliotaph finds the files, reads their text, makes covers, reads scanned pages with Windows' text recognition and,
if you turn it on, asks AI to fill in the details. A big library can take hours the first time. After that, only new
and changed files are read.

- It only works while Bibliotaph is open. Closing the app stops it; reopening carries on where it left off.
- **Settings > Processing** shows each step and the time left, with **Pause indexing**, **Pause reading scans** and
  **Pause AI**.
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
| **Needs review** | Suggestions to check and files that need something from you. The number is how many are waiting. |
| **Pinned** | Anything you've pinned: a book, Favorites, a collection, a binder or a saved search. |
| **Settings** | At the bottom: processing, folders, online libraries, appearance, Needs review, vocabulary, AI, passwords and backups. |

The **Collapse sidebar** button beside Settings, or **Ctrl+B**, folds the sidebar down to its icons for more room;
point at an icon for its name. The same button, now at the top, opens it again. Bibliotaph remembers which way you
left it.

The search box at the top is always there. **Ctrl+K** jumps to it from anywhere.

In the Library, click a cover to see its details on the right, then click **Open** there to read it. Click anywhere
outside the details, or press **Esc**, to close them. Right-click a cover for everything else you can do with it,
including **Open** and **Open in new window**.

**Ctrl+/** lists every keyboard shortcut. **Settings > Appearance** switches between light, dark and Windows'
own setting, and **Settings > Library > Start on** chooses whether Bibliotaph opens on Home or the Library.

---

Next: [Searching](searching.md)
