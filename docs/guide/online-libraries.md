# Online libraries

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Online libraries

Not every book you own is a file on your PC. Some you read on a website, such as D&D Beyond or Demiplane. Others you
bought from a store, such as Humble Bundle or DriveThruRPG, and haven't downloaded. Bibliotaph can bring them into your
library as books owned elsewhere, so they turn up in searches, collections and binders with the rest.

## What Bibliotaph does, and doesn't

- **You sign in yourself**, on the service's own page, in Bibliotaph's browser. Bibliotaph never sees or keeps your
  password.
- **Nothing happens on its own.** The browser opens your library page and then only does what you do: it never clicks,
  scrolls or goes to another page by itself. Nothing is read until you press **Read this page**, and nothing is
  imported until you press **Import**.
- **Only books you own** are imported. Books someone shares with you on D&D Beyond are left out.
- **Covers are the pictures the page already showed.** Bibliotaph fetches nothing else for the books.

## The services

| Service | What you get |
| --- | --- |
| **D&D Beyond**, **Demiplane** | Books you read online. Each becomes a book owned elsewhere, with the service and the book's link under **Also own**, and opens in Bibliotaph's online reader. |
| **Humble Bundle**, **DriveThruRPG** | Books you bought to download. Each becomes a book owned elsewhere, with the store and a link under **Get it from**, until you download its file. A book you already have as a file gets the store and link on its own card instead. Your DMs Guild books are in the DriveThruRPG library. |

## Import your library

1. In the Library, click **Owned elsewhere** and choose **Import from an online library…**. It opens
   **Settings > Online libraries**.
2. Click **Open D&D Beyond** (or the service you want). Bibliotaph's browser opens at your library.
3. Sign in if the site asks, and open your library. **Read this page** lights up when the page lists your books, and
   the browser says how many, for example "This page lists 30 books you own."
4. Click **Read this page**. **Settings > Online libraries** comes forward with a preview of what importing does:
   - **New**: books that get a card of their own.
   - **Already in your library**: a card with the same title is there already, such as one you added by hand. It gets
     the service and the link, rather than a second card.
   - **Imported before, with more to fill in**: only fields that are empty are filled. Nothing you've set changes.
   - **Imported before**: nothing to change.
5. Untick any book you'd rather leave out, and click **Import 30 books**. **Undo** takes it all back.

A few services need a step first:

- **Demiplane**: each game has its own Nexus. Open a game's Nexus, then its **Sources** page. Read each game's page in
  turn; each one read in the same window adds its books to the preview.
- **Humble Bundle**: choose **eBook** under **Platform**, so the library lists only books.
- **DriveThruRPG**: if your library runs to several pages, read each one.

Import again whenever you buy something. When the page you read is your whole library, books you imported before that
it no longer lists show under **No longer in this library**, each with **Remove from library**. Nothing is removed
unless you remove it.

### A saved page instead

Bibliotaph's browser needs the Microsoft Edge WebView2 Runtime. Windows 11 has it; on Windows 10 you may need to install
it from Microsoft. Without it, or if **Read this page** doesn't recognize your library, import a saved page:

1. In your everyday browser, open your library, signed in, and save it with **Save as**, choosing
   **Webpage, complete**. That keeps the covers too.
2. In **Settings > Online libraries**, click **Import a saved page…** and pick the file. The same preview shows.

Bibliotaph's browser has **Save page…** too. It saves the page as it shows, signed in as you, without pictures, for
when you want to show someone a page that **Read this page** doesn't recognize. It holds whatever the page showed you,
so share it only with people you trust.

## Read a book you own online

Open a book you own on D&D Beyond or Demiplane, from the Library, Home, a search, a pin or a binder, and it opens in
the online reader: the service's own page, inside Bibliotaph, signed in through Bibliotaph's browser. A book you have
as a file and also own online has **Open on D&D Beyond** (named after its service) in its menu and details.

Around the page:

- Back, forward and reload buttons for the site (**F5** reloads too).
- The heart, and the pin.
- **Note** shows the book's own note beside the page.
- **Add this page** adds the page you're on to a binder (see below).
- **Open in your browser** opens the page in your everyday browser.
- **Pop out** moves the book into a window of its own, and **Return to main window** brings it back.
- **Reading mode**, the open-book button, hides D&D Beyond's menus, ads and footer, and leaves the book and its
  contents. It's on to start with, and your choice holds for every book.

The reader follows links within the book's site, and the site it signs you in on. A link to anywhere else opens in your
everyday browser, and the reader says so. Bibliotaph copies no text from the page, so there's no **Find**, page box,
thumbnails, page notes or **Ask** here: use the site's own.

To read these books in your everyday browser instead, untick **Read books owned online in Bibliotaph** in
**Settings > Online libraries**.

## Pages in binders

**Add this page** in the online reader adds the page you're on, such as a chapter or a section of it, to your current
binder. Its label is the page's own name, such as "Chapter 3: The Drowned Stair", without the service's name or the
book's title. The menu beside it adds to another binder.

In the binder, the page opens in the online reader, and **Next** and **Previous** step between PDFs and pages online
alike. See [Binders](binders.md).

## Get a book from a store

Books from Humble Bundle and DriveThruRPG can come into your library as files, downloaded in Bibliotaph's browser:

1. In **Settings > Online libraries**, under **Downloads**, click **Choose folder…** and pick one of your library
   folders, or a folder inside one, such as a "Store downloads" folder. Without one, downloads go to your Downloads
   folder, outside your library.
2. Open the store and import your library, as above. Each book you haven't downloaded is listed under **Get it from**.
3. When you want one, click the store's own **Download** button in Bibliotaph's browser. The file goes into your
   downloads folder, and the browser lists it with its progress, **Cancel** and **Show in folder**.
4. Bibliotaph reads the new file like any other, and **Needs review** asks "You own *The Sunken Lighthouse* elsewhere.
   Is this its file?". **Same book** gives the card its file.

Nothing downloads unless you click **Download**. **Stop using it** goes back to your Downloads folder.

## The badge on the cover

A book owned elsewhere has a badge at the bottom left of its cover saying where it is, such as D&D Beyond, with the
site's own icon. Bibliotaph fetches each site's icon once and keeps it. Untick **Show site icons** in
**Settings > Online libraries** and the badge shows a plain cloud instead, and nothing is fetched.

## Find them

- **Kind** in the Library's filters (under **More filters**) has **On this PC** and **Owned elsewhere**. **Also own**
  and **Get it from** filter by service or store.
- In the search box, `where:elsewhere` finds books owned only elsewhere, and `where:here` those with a file on this
  PC. `own:` and `store:` find them by service or store, such as `store:drivethrurpg`.

## Sign out

Bibliotaph's browser keeps you signed in between visits, apart from your everyday browser. **Sign out of all services**
in **Settings > Online libraries** forgets every sign-in, cookie and page it kept.

---

Previous: [Binders](binders.md) · Next: [AI](ai.md)
