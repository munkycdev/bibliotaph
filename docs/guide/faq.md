# Questions and answers

[Bibliotaph](../../README.md) › Guide › Questions and answers

## Safety and privacy

**Will Bibliotaph change, move or delete my files?**
No. It only reads them. It never renames, moves, edits or deletes a file, and removing a folder from your library
leaves the folder as it is. The one exception is OneDrive: after reading a file that was online-only, it can hand it
back to online-only, which drops only the downloaded copy. See [OneDrive](onedrive-and-other-pcs.md#onedrive).

**Does it need the internet?**
No. Your library, search, reading and editing all work offline. Bibliotaph goes online only to check for updates, to
download an AI model when you choose one, and to reach a model server if you set one up.

**Does it send anything about me or my books to anyone?**
No. There's no account, no tracking and no cloud service. Your catalog stays in `%LOCALAPPDATA%\Bibliotaph` on your
PC. If you set up AI with a model server, book excerpts go to that server, and only that server. See [AI](ai.md).

**Where are my passwords kept?**
Passwords for PDFs you choose to remember, and AI keys, are kept in Windows Credential Manager on your PC, never in
Bibliotaph's own files, backups or exports.

**Can it open DRM-protected books?**
No, and it never tries to get round a protection. A book it can't read still gets a card you can fill in and find,
and **Open in another app** opens it in the app that can.

## Getting going

**Windows said "Windows protected your PC". Is that safe?**
Bibliotaph isn't code-signed yet, so Windows doesn't recognize the publisher. Click **More info**, then
**Run anyway**. Only download it from this project's
[Releases page](https://github.com/munkycdev/bibliotaph/releases/latest).

**Does it run on a Mac, Linux, a phone or a tablet?**
No. Bibliotaph is a Windows app, for Windows 10 (version 1809 or later) and Windows 11.

**Does it cost anything?**
No. It's free and open source, under the GNU General Public License.

**Which files does it read?**
PDF, JPG, PNG and WebP, including those inside ZIP files. Other formats, such as EPUB, aren't read yet.

**Do I have to tidy my folders first?**
No. Point it at what you have. Folder names help, so a folder called "Adventures" or named after a game system gives
its books a head start, but nothing needs to move.

**How long does the first read take?**
It depends on how many books you have and how many are scans. A big library can take hours. You can search and read
while it works, and it only works while Bibliotaph is open: close it whenever you like and it carries on next time.

## Day to day

**A book is cataloged wrong.**
Right-click it and choose **Cataloged wrong…**. See [Fixing a value](cataloging.md#fixing-a-value).

**The same book shows twice.**
Identical files share a card by themselves. Files that differ (a reprint, a corrected edition) are asked about in
Needs review, or tick both cards in **Select** mode and click **Same book**.

**Search doesn't find words I can see on a page.**
The book may still be being read, or it's a scan whose text came out badly. Check **Settings > Processing**, then try
**Reprocess with OCR on every page…** from the menu beside **Reprocess** in its details.

**Can I show pages to my players with it?**
Bibliotaph is made for your own preparation, not as a player-facing screen. A popped-out reader window on a second
screen works, though.

**Does it work with my virtual tabletop?**
There's no connection to any VTT. You can note that you own a book on one with **Also own**, and add books you only
own there with **Add a book I own elsewhere**.

**How do I report a bug or ask for something?**
[Open an issue](https://github.com/munkycdev/bibliotaph/issues) on GitHub (you'll need a free account). **Copy details**
in **About Bibliotaph** (in Settings) copies the version numbers to paste in. Please never attach a book or a page
from one.

**How do I uninstall it?**
From Windows' **Settings > Apps**. Your books aren't touched, and your catalog stays in `%LOCALAPPDATA%\Bibliotaph` in
case you install again. Delete that folder if you want it gone too.
