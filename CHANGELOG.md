# Changes

## Unreleased

- **A book's details put what it holds first.** Its adventures, a pack's files and what the AI found inside come first,
  then collections and the catalogue fields, with About the file, Where it is, Copies and Indexing at the end. Every
  section folds away under its heading, and the last four start folded. See
  [Cataloging](docs/guide/cataloging.md#the-details-panel).

## 1.4 ┬╖ 28 October 2026

New in 1.4.1070 (30 October 2026):

- **Fixed: books that print no ISBN weren't looked up online.** Only books printing an ISBN or product code reached
  **Looking up online**, so most of a library was never searched for by title on AdventureLookup, and **Settings >
  Processing** counted only those. Every book with text is now looked up, and the books read before are queued once
  when the app or server starts. See [Cataloging](docs/guide/cataloging.md#where-suggestions-come-from).

New in 1.4.1065 (30 October 2026):

- **Library settings and folder answers now sync.** The theme, Needs review for every suggestion, the start page, the
  Library's order while browsing and searching, Packs or Each file, site icons and how books owned online open are the
  same on every PC, and the web library takes Needs review and the start page. Folder rules you turned on or off, and folders you made into collections or said Never
  to, travel for the web library's folders and for a folder at the same place on two PCs. See
  [OneDrive, drives and other PCs](docs/guide/onedrive-and-other-pcs.md#more-than-one-pc).
- **Each book's own choices now sync, and books open where you left them.** The page you used as a book's cover,
  Forget its text and Read it again, a Cataloged wrong… you left open, the collections you said Never to, the cover you
  chose for a book you own elsewhere and the book's Ask this book conversation reach the web and your other PCs. A
  book opens at the page you left it on, on whichever device you read it last. See
  [OneDrive, drives and other PCs](docs/guide/onedrive-and-other-pcs.md#more-than-one-pc).
- **Same book, adventures and their answers now sync.** Files you joined as one book (with Same book, as a new version,
  or by matching a file to a book you own elsewhere) are joined on your other devices too, and Not the same book parts
  them everywhere. Adventures you mark in a file reach the web and your other PCs with their pages and values, and
  removing one removes it everywhere. Your answers to adventure proposals and to "Is this its file?" travel as well.
  See [OneDrive, drives and other PCs](docs/guide/onedrive-and-other-pcs.md#more-than-one-pc).
- **Your library in numbers.** A new **Summary** page in the sidebar, under Binders, shows what you have and where it
  came from: how many books, pages and gigabytes, and how long reading them all would take; your folders and drives,
  where else you own books and which store books are still to download; your game systems, types and settings, with
  how much of each is confirmed and how much only suggested; how many books cover each character level and when they
  were published; and how many you've opened, your favorites, notes, collections and binders, and how the library has
  grown month by month. Every figure opens the Library at the books it counts. Home shows the library's size under the
  welcome, with a link to it. The search box has a new field, `opened:`, so `opened:no` finds the books you haven't
  opened yet. It's on bibliotaph.online too, the same page with the same links. See
  [Your library in numbers](docs/guide/summary.md).
- **Monsters, items, and what an adventure includes.** Three new fields, filled by AdventureLookup and by you: an
  adventure's monsters (bosses first) and its magic items, which plain search finds ("owlbear"), and **Includes**, a
  short list of pre-generated characters, handouts, battle maps and playable solo. The AI isn't asked for them. See
  [Cataloging](docs/guide/cataloging.md).
- **Levels and environments from AdventureLookup.** On the web, and on a PC connected to it, books are now looked up
  on [AdventureLookup](https://adventurelookup.com) too, a catalogue of published adventures, by their title. Its
  record is used when the book prints that title and one of its authors or its publisher, or has about as many pages;
  otherwise Needs review asks **Same book?**. It fills in the levels an adventure is for, which no catalogue gave
  before, and its edition, setting, environments and series. Books that print no ISBN or product code are looked up
  by title now too. The vocabulary gains the environments Stronghold, Temple, Mansion and Sky, and the game system
  Draw Steel. See [Cataloging](docs/guide/cataloging.md).
- **Artists and mechanics from RPGGeek, and books found by product code.** A book looked up on RPGGeek now gets its
  artists and its mechanics (such as "Dice (d20)") in two new fields, which plain search finds; the AI isn't asked
  for them. RPGGeek's record is also used when one of its versions lists a product code the book prints, such as
  PZO1110, so books with a code but no ISBN are looked up too. Books looked up before get artists and mechanics once
  you press the run-again button on **Looking up online** in Settings › Processing. See
  [Cataloging](docs/guide/cataloging.md).
- **Packs now sync between the web and your PCs.** Files you pack together, a pack you split, your Make a pack and
  Keep separate answers in Needs review, packs merged into one and a pack's chosen cover reach every device that has
  those files. A pack's title, hearts and collections, which waited for a matching pack, now arrive with it. Where the
  web and a PC packed the same folder by themselves, they settle on one pack. See
  [OneDrive, drives and other PCs](docs/guide/onedrive-and-other-pcs.md#more-than-one-pc).
- **Needs review answers about new names now reach every device.** When the AI proposed the same new name on the web
  and on a PC, adding it on one left it waiting on the other, with its books still in Needs review. Now a name you add,
  file under another term or turn down is decided the same way everywhere, and the books waiting on it move on. A name
  you turned down is never asked about on your other devices. Pairs you kept apart in Find overlaps, and your own tags
  with the words they're found by, travel too. See [OneDrive, drives and other PCs](docs/guide/onedrive-and-other-pcs.md#more-than-one-pc).
- **Privacy policy: notice is for personal data.** The 14 days' notice before a change now covers what we collect
  about you and who we share your personal data with. A new public book catalogue, which only ever receives book
  titles, ISBNs or product codes, is added to the policy's list when we start asking it.
- **The "Powered by BGG" logo has moved** from the foot of every web page to where RPGGeek's data shows: under a
  book's details when some of them came from RPGGeek, on Windows and the web. It's also in About on Windows and on the
  website's licences page. See [Cataloging](docs/guide/cataloging.md).

New in 1.4.1019 (29 October 2026):

- **Run a step again from Settings ΓÇ║ Processing.** A button at the end of the Finding files, Looking up online,
  Cataloguing with AI and Scanning whole books rows runs that step again for every book, on Windows and the web: after
  adding names to your lists, the AI row reads every book again against them. The last three ask first, with how many
  books, since each takes a pass over your library; values you've accepted, typed or rejected stay as they are. Read
  every book again has moved there from Settings ΓÇ║ AI, which points to it. See
  [Getting started](docs/guide/getting-started.md).
- **Same book?** When RPGGeek, Open Library or Google Books has a record for a book's ISBN but its title isn't printed
  in the book, Needs review now asks whether it's the same book, showing what the record says, instead of quietly
  setting it aside. **Same book** fills in the book's title, authors, publisher, year and what RPGGeek files it under,
  shown as coming from that catalogue rather than from you, so anything you set yourself stays; **Not this book**
  means it's never used for that book. Your answer reaches your other PCs and the web. Books set aside this way
  before are looked up once more to ask. See [Cataloging](docs/guide/cataloging.md#needs-review).
- **The guide on bibliotaph.online.** The user guide now has a section of its own on the site, at
  [bibliotaph.online/guide](https://bibliotaph.online/guide): a contents page with a line for each chapter, and a page
  for each chapter with the list of chapters beside it, made from the same files as the guide on GitHub, so the two
  always match. The site's links now put About before Features, with Guide after them. The README, the site's
  Features and landing pages, and the guide itself now cover the tour, the settings groups, ISBN lookups and what the
  web version does today. See [Bibliotaph on the web](docs/guide/web.md#the-site).
- **See your PC's AI at work on the web.** When a Windows PC connected to bibliotaph.online is cataloguing with its own
  AI, Settings ΓÇ║ Processing on the web now says so: **On your PCs** shows the book it's on and how far it has got, and
  Cataloguing with AI and Scanning whole books say **On** and the PC's name instead of **Off** when the server's AI
  isn't set up. The PC sends only the book's fingerprint, so a book only the PC has is never named. Needs this
  version of the Windows app. See [Bibliotaph on the web](docs/guide/web.md#processing).
- **Suggestions name the record by its title.** A looked-up suggestion's quote, in Needs review and the details
  panel, now reads "RPGGeek's record of The Owlbear Mill" rather than giving an ISBN. Books looked up before keep the
  old wording until they're next looked up.
- **Fix: "bibliotaph.online can't be reached" while it could.** A Windows app connected to bibliotaph.online gave up on
  a lookup after 20 seconds, but with RPGGeek's five-second pace a lookup can wait its turn longer than that, so the
  app said the site couldn't be reached and books stopped being looked up. Lookups now wait up to three minutes. When
  the app really can't reach bibliotaph.online, the status at the bottom of the sidebar says so on a line of its own,
  and Settings > Processing just says lookups are waiting for it.
- **Fix: garbled characters in this change log.** Dashes, dots and arrows here had turned into strings of odd
  symbols, a little worse with each release; they're back, and releases leave them alone now.

New in 1.4.995 (29 October 2026):

- **RPGGeek first.** bibliotaph.online, and a Windows app connected to it, now looks books up on
  [RPGGeek](https://rpggeek.com) too, which knows RPGs, small-press ones included, and their designers best. Every
  catalogue is asked, and each field comes from the first of RPGGeek, Open Library and Google Books that gives it, so
  RPGGeek wins wherever it has a value; your own values still beat them all, and they beat the AI's guesses, which
  beat what's read from file and folder names. RPGGeek is searched by the book's title, the one another catalogue
  gives or else the one in your library, and its record is used only when one of its versions lists an ISBN the book
  prints. Every book is looked up once more to fill in from RPGGeek. RPGGeek's data is powered by BGG, whose logo is at the foot of every web page and in About, in Bibliotaph's own colours. See
  [Cataloging](docs/guide/cataloging.md#where-suggestions-come-from).
- **Filed as RPGGeek files it.** RPGGeek's game system and edition, category, genres, setting and series now fill in
  System, Edition, Type, Themes, Setting and Series, in your own lists' terms: "Scenario / Adventure / Module" is
  Adventure, "Dungeons & Dragons (3.5 Edition)" is D&D 3.5. A system, theme or setting you don't have yet waits in
  Needs review like a new name from the AI. Every book is looked up once more to fill them in.

New in 1.4.987 (29 October 2026):

- **A deploy never goes backwards.** Two pushes to main close together could go live in the order their builds
  finished, so the older build sometimes replaced the newer one on bibliotaph.online. The roll-out now leaves the site
  alone when what's live already includes the commit.
- **The site says which build it is.** The footer and About Bibliotaph on bibliotaph.online now name the build
  number and commit that is deployed, as the Windows app's About does, instead of 1.4.0.
- **Terms that may be the same, in Needs review.** Bibliotaph finds the terms in a list that look like one thing
  under two names ("Forgotten Realms" and "The Forgotten Realms", "Greyhawk" and "Grayhawk", "Dark Sun" and "Athas")
  and offers each group as a card in Needs review, with how many books use each term: by comparing the names, which
  needs no AI, and, with AI on, by asking the model about the lists it can add to, in the background, only about
  names it hasn't seen. The term most books use is picked to keep; **Combine** folds the others into it, their names
  becoming its other names and their books moving to it, with Undo; **Keep apart** says they're not the same thing
  and that pair isn't offered again. **Find overlaps** in Settings > Vocabulary compares a list's names now and says
  how many groups wait, with the pairs kept apart to offer again. On Windows and the web. See
  [Cataloging](docs/guide/cataloging.md#find-overlaps).
- **Read every book again.** Settings > AI has a button under Reclassify that sends every book the AI has read back to
  it, so names you've added to your lists since are found in the books that use them. It asks first, since it takes as
  long as cataloguing them did; values you've accepted, typed or rejected stay as they are. On Windows and the web. See
  [AI](docs/guide/ai.md#day-to-day).
- **Let the AI grow a list by itself.** Settings > Vocabulary now has **New names from the AI** at the top of each
  list the AI can add to (everything but editions): **Ask first**, as before, puts a card in Needs review for each
  new name; **Add them** puts the name straight into the list as the AI uses it, with the books that use it getting
  it as an ordinary suggestion, and adds that list's names already waiting in Needs review. Made for campaign
  settings, where the AI only names worlds the book names and the cards pile up. Rejected names stay rejected. On
  Windows and the web. See [Cataloging](docs/guide/cataloging.md#vocabulary).
- **Scanning whole books has its own bar.** Settings > Processing shows the AI's chapter-by-chapter reading as a
  step like the others, under Cataloguing with AI, with how far it has got and the time left, instead of a line of
  text in the summary. Off when AI is, or when Scan whole books is off in Settings > AI. On Windows and the web.

New in 1.4.968 (29 October 2026):

- **A tour on the first day.** The first time Bibliotaph opens, on Windows or at bibliotaph.online, a short tour
  points at the parts of the window with the rest dimmed, in two chapters so it never shows an empty page: where to
  add your first folder (on the web, the storage to connect and the folder picker too) and, once one is in, the
  sidebar's status line; then, the first time your books show, a cover, the Library, books you own online, the search
  box, Needs review, Collections and Binders, and Settings. Skip it or press Esc and it never comes back on its own;
  **Show the tour again** in Settings > Preferences starts it over. See
  [Getting started](docs/guide/getting-started.md#the-tour) and [Bibliotaph on the web](docs/guide/web.md#the-tour).
- **Copy an invitation's link again, and delete invitations.** On bibliotaph.online, Settings > Members now has a copy
  button beside each open invitation, which gives the same link again until it's used, and a bin on every invitation.
  Deleting an open one makes its link stop working straight away, so it asks first; deleting a used one tidies the list
  and keeps the account. See [Bibliotaph on the web](docs/guide/web.md#inviting-others).
- **Books are always looked up by their ISBN.** The **Look up books online by their ISBN** box in Settings >
  Processing is gone: every book that prints an ISBN is looked up in Open Library (and, on bibliotaph.online or from
  a PC connected to it, Google Books), and the Looking up online step always shows its counts. Only the ISBNs are
  sent, and **Looking up online** under the steps says where. See
  [Cataloging](docs/guide/cataloging.md#where-suggestions-come-from).
- **Folder names has gone from Settings > Library.** Folder names like "Adventures" or "D&D 5e" still suggest the type
  or system of the books inside; the list of every such name with a switch each is gone, on Windows and the web. Fix a
  book a folder name misled as you come to it, and Bibliotaph offers to fix the folder's other books with it. Folder
  rules answered in Needs review stay in Settings > Library. See
  [Getting started](docs/guide/getting-started.md#folder-names-count).
- **Settings in four groups.** The section list, on Windows and the web, now reads BOOKS (Library, Online libraries,
  Devices), CATALOGUING (Processing, AI, Vocabulary), YOU (Preferences, Passwords, Backup, and Account on the web) and,
  for those who can invite, ADMIN (Members). Each page is headed as its entry is: Library, not Library folders; Devices,
  not Web library or Windows PCs; and a page of several parts (Library, Devices, Members) has a subheading for each.
  See [Bibliotaph on the web](docs/guide/web.md#settings).

New in 1.4.947 (28 October 2026):

- **Google Books for the Windows app's books too.** With **Look up books online by their ISBN** on, a PC connected to
  bibliotaph.online looks its books up through it, so a book Open Library doesn't know is looked for in Google Books
  as well, and no PC holds a Google key. Books neither catalogue knew before are asked again once there's somewhere
  more to ask, on the PC and on the web, so the ones Open Library missed are tried on Google Books. Disconnected, the
  app asks Open Library itself. See [Cataloging](docs/guide/cataloging.md#where-suggestions-come-from).
- **Replace a server secret in one step.** For whoever runs bibliotaph.online: `infra/setup.ps1 -Replace` asks again
  for the secrets it names (Microsoft, Auth0, Dropbox, GoogleDrive, Box, GoogleBooks), replaces them in Key Vault and
  restarts the app on them, so an old one no longer has to be deleted from Key Vault first. A new client ID for
  Auth0, Dropbox, Google Drive or Box asks for its secret too.

New in 1.4.943 (28 October 2026):

- **Look books up by their ISBN.** Turn on **Look up books online by their ISBN** in Settings > Processing, on the
  Windows app or the web, and Bibliotaph asks Open Library about the ISBNs your books print, and nothing else. It
  suggests the title, authors, publisher and year from the book's record, but only when the record's title is printed in
  the book. These beat the AI's suggestions, which now fill only what the lookup didn't. Settings > Processing shows the
  lookups as their own step, with how many books are done and left, and a book whose text holds broken
  characters is looked up like any other. Off until you turn it on. See
  [Cataloging](docs/guide/cataloging.md#where-suggestions-come-from).
- **Google Books for books Open Library doesn't know.** On bibliotaph.online, Look up books online by their ISBN now
  asks Google Books for a book Open Library has no record of, which finds many more small-press books. It sends the
  ISBNs and nothing else, and the setting names where it looks. See
  [Cataloging](docs/guide/cataloging.md#where-suggestions-come-from).
- **Find a book by its ISBN.** Bibliotaph now finds the ISBN and product code a book prints on its first pages or back
  cover, or in an e-book's own information, and shows each in the book's details. `isbn:` in
  the search box finds a book by it, typed with or without hyphens, and `-isbn:unknown` lists the books that have one.
  Nothing is read again: books already in your library get it from the text Bibliotaph has. See
  [Searching](docs/guide/searching.md#fields).
- **New versions found by ISBN.** A file that prints a book's ISBN as its own, but whose pages differ, such as a
  corrected PDF or a scan of the print edition, is now asked about in Needs review as a new version of it. Bibliotaph
  never joins two files by an ISBN alone, since publishers keep one ISBN across printings. Books already in your
  library are checked from the text Bibliotaph has, without reading a file again, and a book an online lookup renames
  is checked again for another file with its title and publisher. See
  [Cataloging](docs/guide/cataloging.md#needs-review).
- **Your web library's books on your PC.** A PC connected to bibliotaph.online shows the books in your online storage
  as well as its own: each web library folder is a library folder in Settings > Library, its books searchable without
  being downloaded, and the PC's AI reads them as it reads the rest. A book is downloaded only to open it, or to read
  its scanned pages. See [Bibliotaph on the web](docs/guide/web.md#connecting-a-windows-pc).
- **Web library books offline.** Opening a book only your web library has shows the download's progress, and the
  download is kept for next time, up to the room you set in Settings > Devices (5 GB to start). **Keep offline on this
  PC**, in a book's details, keeps one for reading without a connection, and **Open in another app** opens the
  downloaded copy. A book the web library couldn't read is read on the PC instead. See
  [Bibliotaph on the web](docs/guide/web.md#connecting-a-windows-pc).
- **No blank page after an update.** On bibliotaph.online, while your library comes back from storage after an update
  or a restart, the page says so with how long it has taken, and opens your library when it's back, rather than
  staying empty for minutes. The server also starts bringing back the libraries of people who signed in lately as soon
  as it safely can. See [Bibliotaph on the web](docs/guide/web.md#signing-in-and-out).
- **Room for everyone on the web.** bibliotaph.online turns an upload away for a minute when other people's uploads
  are already taking the memory it sets aside, rather than running out for everyone. Sign-up can be opened without
  invitations, up to a set number of accounts; it stays invite-only for now. See
  [Bibliotaph on the web](docs/guide/web.md#join-with-an-invitation).
- **Read links are yours alone.** On bibliotaph.online, a book from Dropbox, Google Drive, Box or WebDAV is read
  through the server only by your own sign-in, so a link copied out of the reader is no use to anyone else. Each user
  reads up to 10 GB a day through the server. See [Bibliotaph on the web](docs/guide/web.md#reading-a-book).
- **Books read on the web are read shut in.** On bibliotaph.online, the process that reads PDFs can open only the
  books being read, can't start programs or reach the network, and never sees the server's keys, so a booby-trapped
  PDF in anyone's storage can't reach other people's libraries. The server's diagnostics port is off. See
  [Bibliotaph on the web](docs/guide/web.md).
- **Sign-in at auth.munkyc.dev.** bibliotaph.online's sign-in page moves to auth.munkyc.dev, the MunkyC sign-in
  shared by Bibliotaph and its maker's other apps, with Google, Microsoft and Discord. Your account, library and
  linked sign-ins stay as they are. See
  [Bibliotaph on the web](docs/guide/web.md#join-with-an-invitation).
- **The logo in large format.** bibliotaph.online/brand/ has the logo and the **b.** monogram as SVG and PNG, for light
  and dark backgrounds, drawn from the same type and shapes as the app's sidebar, for the sign-in page and anywhere
  else the name appears.

## 1.3 ┬╖ 19 October 2026

New in 1.3.877 (26 October 2026):

- **Review buttons under the value they keep.** On a suggestion card, **Accept**, **Accept all** and **Edit** sit
  under the suggestion, and the button that keeps the book's value, now **Keep current** in place of **Reject**, sits
  under what the book has, with the evidence inside the suggestion's box, in the Windows app and on the web. See
  [Cataloging](docs/guide/cataloging.md).
- **Clearer New version? cards.** None of the choices looks already chosen: the card's three buttons look alike, in
  the Windows app and on the web, and nothing is picked until you click one. Page headings on the web
  no longer show an outline when a page opens. See [Cataloging](docs/guide/cataloging.md).
- **The front page, signed in too.** bibliotaph.online shows the front page to everyone, with **Open your library** in
  place of **Sign in**, and the web app's Home moved to bibliotaph.online/home. **b. Bibliotaph** at the top of the
  app's sidebar goes to the front page. See [The site](docs/guide/web.md#the-site).
- **Add to binder in the reader.** **Add page** is now **Add to binder**, in the Windows app and on the web. Its menu
  names your binders, the one you worked on last first, so you choose where the page goes; **New binderΓÇª** and
  **Add pagesΓÇª** are there too. See [Binders](docs/guide/binders.md#add-books-and-pages).
- **Online libraries says how books come in.** **Settings > Online libraries** explains how to bring in books from
  outside, in the Windows app and on the web, and groups the services as **Books you read online** (D&D Beyond,
  Demiplane) and **PDFs you bought from stores** (Humble Bundle, DriveThruRPG). On the web the **Send to Bibliotaph**
  bookmark is always there, even with the extension installed. See [Online libraries](docs/guide/online-libraries.md).
- **A folded sidebar keeps its logo.** Folded, the sidebar shows the **b.** monogram at the top, and the button that
  unfolds it stays at the bottom under **Settings**, in the Windows app and on the web. See
  [Getting started](docs/guide/getting-started.md).
- **A new home page for bibliotaph.online.** It shows what Bibliotaph does, with examples of search, the catalog,
  binders and online libraries, the Windows app and the web side by side, and answers to common questions. See
  [The site](docs/guide/web.md#the-site).
- **Fewer, clearer Settings in the Windows app, in the web's order.** **Preferences** holds light or dark, **Start
  on** (moved from Library) and what waits for review; **Web library** is now **Devices**; and Library comes first.
  See [Getting started](docs/guide/getting-started.md).
- **Sync with another PC is retired.** Connect each PC to your web library in **Settings > Devices** instead: it keeps
  your work the same on every PC and on the web. The files the old sync left in its folder aren't read any more and can
  be deleted. See [More than one PC](docs/guide/onedrive-and-other-pcs.md#more-than-one-pc).
- **Saved pages are no longer imported.** **Import a saved pageΓÇª** and **Save pageΓÇª** are gone from the Windows app,
  and **Import a saved pageΓÇª** and **Choose picturesΓÇª** from the web: Bibliotaph's browser reads the page on Windows,
  and **Send to Bibliotaph** brings it, covers included, on the web. See
  [Online libraries](docs/guide/online-libraries.md#without-bibliotaphs-browser).
- **PDFs with copy or print restrictions open on the web again.** The reader failed on them. When a book can't be
  opened for a reason Bibliotaph doesn't recognise, it now says so plainly instead of showing the technical error.
- **The web library answers first.** Reading books in the background, scanned pages most of all, now steps aside while
  you're using the library, and a library opens faster after an update. See [Processing](docs/guide/web.md#processing).
- **Fewer, clearer Settings on the web.** Storage is now at the top of **Library**, above the folders you add from it;
  **Devices** holds your Windows PCs and the books kept in this browser; **Preferences** holds light or dark, **Start
  on** and what waits for review; and **Members** holds invitations and accounts for those who can invite. Old links,
  such as settings#storage, open the section that holds it now. See [Settings](docs/guide/web.md#settings).
- **Choose which file is the current version.** A New version? card names both files with their pages, and asks
  which one the book opens, each button naming its file, such as **Handbook_v2.pdf is current**. See
  [Needs review](docs/guide/cataloging.md#needs-review).
- **Connect a Windows PC to your web library.** Settings > Devices in the Windows app opens bibliotaph.online in your
  browser, where you check the code the app shows and approve the PC; it gets a key of its own, kept in Windows
  Credential Manager. Settings ΓÇ║ Devices on the web lists connected PCs and disconnects them. See
  [Connecting a Windows PC](docs/guide/web.md#connecting-a-windows-pc).
- **Your work syncs between the Windows app and the web.** A connected PC and bibliotaph.online merge your corrections,
  hearts, notes, collections, binders, saved searches, pins and Needs review answers both ways: a change goes up a few
  seconds after you make it and comes down within a minute or so. Settings > Devices says when it last merged. The app still works offline and catches up when it's back.
- **The AI reads each book once, for all your devices.** With a PC connected to your web library, what the AI found
  in a book is shared by the file's contents: the web library gets the PC's suggestions for the same file, ready in
  Needs review without an AI key of its own, and a book the AI has already read on one device isn't sent to the AI on
  another. Whole-book scans are shared the same way. See [Connecting a Windows PC](docs/guide/web.md#connecting-a-windows-pc).
- **Scanned pages are read once, for all your devices.** The text OCR read on a scanned book is shared by the file's
  contents too, so a book the PC read is searchable in your web library straight away, and the other way round, without
  reading its pages again. See [Connecting a Windows PC](docs/guide/web.md#connecting-a-windows-pc).
- **Cataloguing with AI starts sooner after OCR.** A book waiting for its scanned pages is catalogued as soon as they're
  read, instead of up to two minutes later.
- **Needs review forgets a removed folder's books.** Removing a library folder, on the web or in the Windows app, left
  its books' suggestions, new terms and failed files in Needs review and the sidebar's count; now only the books in the
  library's folders count.

And Bibliotaph on the web, at [bibliotaph.online](https://bibliotaph.online), which the app above connects to. It is
open by invitation while it settles:

- **A footer on the web.** Every page of the web app, except the readers, ends with links to the site's pages
  (Features, About, the Change log, Contact us, Terms, Privacy, Licences and the source code), the version that's
  running, and a tagline that changes each time you load the page. The site's own pages have the same. See
  [The site](docs/guide/web.md#the-site).
- **The site around the web app.** bibliotaph.online now opens, for anyone signed out, on a page saying what
  Bibliotaph is, and has pages of its own for Features, About, the Change log, Contact us, Terms, Privacy and Licences
  (Bibliotaph's licence and every component's). Signed in, it opens your library as before. See
  [The site](docs/guide/web.md#the-site).
- **A clearer tab icon on the web.** The "b." in the browser tab is bolder and bigger, and light on a dark tab bar.
- **Weekly backups and Restore, on the web.** Bibliotaph on the web makes a backup each week and keeps the last four,
  and Settings ΓÇ║ Backup lists them to download or restore. Restore also reads a backup you choose, made on the web or
  by the Windows app, shows what it holds before anything changes, and keeps your current catalog first. See
  [Backup and export](docs/guide/web.md#backup-and-export).
- **Send to Bibliotaph, on the web.** A bookmark in Settings ΓÇ║ Online libraries, dragged to the bookmarks bar and clicked
  on your D&D Beyond, Demiplane, Humble Bundle or DriveThruRPG library page, sends the page to Bibliotaph with its
  covers, so there's no saving and picking files. See [Online libraries](docs/guide/web.md#online-libraries).
- **Send to Bibliotaph extension for Chrome and Edge.** The same from a button in the browser's toolbar, for sites that
  keep the bookmark from reaching Bibliotaph, and with covers from the services' image hosts. See
  [Online libraries](docs/guide/web.md#online-libraries).
- **The start of Bibliotaph on the web.** bibliotaph.online gets its server and a browser app that looks like the
  Windows one. It is invite-only: a one-time link makes an account, signed in through Auth0 with Google, Microsoft or an email address, and admins make
  invitations in Settings. The privacy policy is published at bibliotaph.online/privacy. Your library comes to it in the
  next steps. See [Bibliotaph on the web](docs/guide/web.md).
- **OneDrive on the web.** Connect one or more OneDrives in Settings ΓÇ║ Storage, then add library folders from them as in
  the Windows app. Bibliotaph looks through them, reads each new file once without keeping a copy, and notices changes
  in your OneDrive within minutes. The sidebar says what it's doing.
- **Dropbox on the web.** Connect one or more Dropbox accounts in Settings ΓÇ║ Storage beside OneDrive, including a team's
  space, and add library folders from them. Changes in your Dropbox are noticed within minutes, and books open in the
  reader as they do from OneDrive.
- **Google Drive on the web.** Connect one or more Google Drives in Settings ΓÇ║ Storage and add library folders from
  them. Google Docs and Sheets are left out, two files of one name are told apart, and changes are noticed within five
  minutes.
- **What your storage allowed, in Settings.** Each storage account in Settings ΓÇ║ Storage now says what Bibliotaph was
  allowed to do, such as read your files and keep access while you're signed out. OneDrive no longer asks to write to a
  folder of Bibliotaph's own, as nothing on the web uses it yet.
- **Settings on the web laid out like the Windows app's.** *Make yourself at home.*, with a list of sections beside the
  one chosen: Storage, Library, Windows PCs, AI, Appearance, Needs review, Books in this browser and Account, plus
  Invitations and Accounts for admins. Appearance and Needs review are new on the web, with the Windows app's choices,
  and About Bibliotaph shows the version, the licence and links to the source. A new invitation's link has a Copy link
  button. See [Settings](docs/guide/web.md#settings).
- **Backup and export on the web.** Settings ΓÇ║ Backup downloads a backup the Windows app can restore, and exports the
  whole library as CSV or JSON, as the Windows app's Back up nowΓÇª, Export CSV and Export JSON do. The web server's
  sealed AI keys are left out of both, as the Windows app's keys always are. See
  [Backup and export](docs/guide/web.md#backup-and-export).
- **Start on, folder names and folder rules on the web.** Settings ΓÇ║ Library has the Windows app's Start on (Home or
  Library), the folder names read as suggestions, each of which can be switched off, and the folder rules answered in
  Needs review, each with its switch. See [Library folders](docs/guide/web.md#library-folders).
- **Easier to find your way on the web.** A book in the Library has the Windows app's right-click menu. On a phone,
  Settings and what the library is doing sit under the navigation instead of being hidden. Home welcomes you back
  once your library has books, even ones that came from a connected PC before any folder was added here. The Library's
  Add folder sits beside Add, and on a phone a page's buttons go under its title.
- **Keyboard shortcuts on the web.** Ctrl+K, or /, goes to the search box from anywhere, Ctrl+/ lists every shortcut
  that works on the web (also under Settings), and Shift+Enter or a middle-click on a book opens it in a reader window
  of its own, as in the Windows app. Home's covers have the book's right-click menu too.
- **Install the web version as an app.** bibliotaph.online installs from Chrome, Edge or Safari with its own icon and a
  window without the address bar, handy on a tablet at the table. While a book is open on the web, the screen stays
  on.
- **Processing on the web.** Settings ΓÇ║ Processing shows each step of reading your library with its bar and the time
  left, and what is being read now, as in the Windows app, with Pause indexing, Pause reading scans and Pause AI, each
  kept until you resume it. Settings' sections now follow the Windows app's order. See
  [Processing](docs/guide/web.md#processing).
- **Vocabulary on the web.** Settings ΓÇ║ Vocabulary has the Windows app's tabs of terms, each renamed, given a short
  name for covers or other names in place, and Your tags, each found in your books' pages by its words. See
  [Vocabulary](docs/guide/web.md#vocabulary).
- **Your books read on the web.** bibliotaph.online reads each book in your library folders as the Windows app does:
  pages, outline, text for search, covers, and hints from file and folder names. A book is downloaded once to be read
  and its copy deleted when it's done. OCR and AI on the web come later.
- **Your library in the browser.** bibliotaph.online's Library shows your books with their covers or as a list, with the
  Windows app's filters, orders and Packs or Each file, and the search box finds books and the pages inside them. See
  [Your library and search](docs/guide/web.md#reading-a-book).
- **Book details and Home on the web.** A click on a book opens its details as in the Windows app: its fields with
  where each value came from, its adventures or pack files, its file, where it is, its copies and how far reading it has
  got. Home shows the books opened and added last. Covers are kept with your account across server restarts. See
  [Your library and search](docs/guide/web.md#reading-a-book).
- **Change a book's details on the web.** As in the Windows app: edit fields and choose between suggestions, Cataloged
  wrongΓÇª and the offer to correct look-alikes, favorites, notes, adventures inside a PDF, Reprocess, a pack's cover and
  split, and copies, with Undo. See [Changing a book's details](docs/guide/web.md#changing-a-books-details).
- **Type to filter on the web.** More filters has the Windows app's boxes for setting, themes, environments, publisher
  and edition: type, pick from the matching values with their counts, and each pick becomes a chip. See
  [Your library and search](docs/guide/web.md#reading-a-book).
- **Collections on the web.** Make, open, rename, move and delete collections, and add books to them or take them out
  from a book's details, as in the Windows app, with Undo. See [Collections](docs/guide/web.md#collections).
- **Favorites, saved searches and pins on the web.** Favorites shows the books with a heart. Save search keeps a search,
  its filters and order as a page of its own, with Update saved search and Save as new once it's changed, and the
  Saved searches list has them all. Collections, saved searches, books and Favorites pin to the sidebar and Home, in the
  order you drag them to. They are the same ones the Windows app shows. See
  [Favorites, saved searches and pins](docs/guide/web.md#favorites-saved-searches-and-pins).
- **Select mode on the web.** Select in the Library ticks books, with Shift+click and Ctrl+A, and the Select bar adds
  them to a collection or takes them out, joins them as the Same book, packs them together, removes adventures and
  exports them as a CSV, as in the Windows app, with Undo. See [Select mode](docs/guide/web.md#select-mode).
- **Edit metadata on the web.** In Select mode, Edit metadata sets, adds or removes values on every ticked book at
  once, with Review changes first and Undo after, as in the Windows app. See [Select mode](docs/guide/web.md#select-mode).
- **Binders on the web.** The Binders page groups your binders as in the Windows app, Home has Continue preparing, and a
  binder's page arranges its items and sections, edits labels and notes, and duplicates or deletes it, with Undo.
  Show these books in the Library and pinned binders work too. See [Binders](docs/guide/web.md#binders).
- **Add to binder on the web.** A book's details, the Select bar and a matching page's + add to a binder, the one you
  worked on last, another or a new one, as in the Windows app, with Undo. See [Binders](docs/guide/web.md#binders).
- **Copies and new versions on the web.** Two files of one book on your drive become one card with two copies, and a
  file that shares most of a book's pages is offered as its new version, as in the Windows app.
- **Ask this book on the web.** The reader's Ask panel answers a question from the book's own pages, as in the Windows
  app, shown as the model writes it, with each citation opening its page, follow-ups and New conversation; an
  embedding model in Settings ΓÇ║ AI finds pages by meaning too. See [Ask this book](docs/guide/web.md#ask-this-book).
- **Ask in the top bar on the web.** The Γ£ª button beside the search box reads a request in plain English as a search,
  shown as chips to change before searching, or answers a question from the books the Library shows, each citation
  opening its book at its page. Reading the whole library for meaning is a setting in Settings ΓÇ║ AI, off until ticked.
  Search words now reach the reader on the web, so a matching page opens with them marked.
- **Scan whole books on the web.** Settings ΓÇ║ AI's Scan whole books reads each book chapter by chapter, as in the
  Windows app, and a book's details show what it found under Found inside the book, with Scan again. It's off on the
  web until you tick it, since every chapter is a request on your key. See
  [Scanning whole books](docs/guide/web.md#scanning-whole-books).
- **AI on the web, with your own key.** Settings ΓÇ║ AI catalogues your books as the Windows app's AI does, with a model
  service you choose on the internet, such as OpenAI or OpenRouter, and your own key, kept encrypted and never shown
  again. Use AI, Test with a book, Reclassify and the folders sent to AI work as on Windows. See
  [AI](docs/guide/web.md#ai).
- **Scanned pages on the web.** The server reads scanned pages by OCR, in English, so search finds them as on Windows,
  and the sidebar counts the pages left. See [Bibliotaph on the web](docs/guide/web.md).
- **Sign in to the web through Auth0.** bibliotaph.online's sign-in page has one Continue button, which offers Google,
  Microsoft or your email address, with Apple to come. Link another sign-in in Settings adds another way into the same
  account, and Sign out signs you out of the sign-in page too. The privacy policy names Auth0.
- **Discord sign-ins named in Settings.** A sign-in through Discord, or another of Auth0's custom social connections, is
  listed under its own name rather than as "Oauth2".
- **Books you own elsewhere on the web.** The Library's Add menu has Add a book I own elsewhereΓÇª, as in the Windows
  app, with a cover picture of your own; its details have Choose a cover imageΓÇª, Remove the cover and Remove from
  library with Undo. See [Bibliotaph on the web](docs/guide/web.md#books-you-own-elsewhere).
- **Needs review on the web.** bibliotaph.online's Needs review has the Windows page's two tabs and its sidebar count.
  Field suggestions can be accepted, accepted for every book suggesting the same, edited or rejected, and new terms
  added, filed under another or rejected, each with Undo; Show narrows the list. Files needing attention can be tried
  again, and a fault's details copied for a bug report. The cards for new versions, image packs, books owned
  elsewhere, places to check, adventures inside files, books cataloged wrong, folder rules and collections are
  answered as in the Windows app, with Undo. See [Needs review](docs/guide/web.md#needs-review).

- **Read your books on the web.** bibliotaph.online opens a PDF in a reader laid out as the Windows one: the page box
  with printed page numbers, zoom, contents, page thumbnails, links with Back, Find with F3 and Whole file for an
  adventure, the heart and pin, and Pop out. A book opens where you left it, an adventure at its own pages, and a
  matching page with the search's words marked. Pages come straight from your OneDrive and are kept in your browser,
  never on the server. See [Reading a book](docs/guide/web.md#reading-a-book).
- **Keep books offline in your browser.** The reader's Keep offline button downloads a whole book to the browser, so
  it opens without waiting on the network, and Settings ΓÇ║ Books in this browser shows the books kept there, sets how
  much room the others may take, and lets them go. See [Books in this browser](docs/guide/web.md#books-in-this-browser).
- **Pictures and packs in the web reader.** A picture opens fitted to the window, with the Windows reader's zoom, and a
  pack steps through its pictures and PDFs from the toolbar or with the arrow keys. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **E-books in the web reader.** EPUB and Kindle books open in the browser as in the Windows reader, in the same pages
  search found, with Find, zoom, links and fixed-layout books; the server never sees the book. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **Notes in the web reader.** Note adds a note on the page in view or the selected text's page, and the Notes panel
  lists the book's page notes to go to, change or delete with Undo, as in the Windows reader; thumbnails badge pages
  with notes. See [Reading a book](docs/guide/web.md#reading-a-book).
- **Admins can delete accounts on the web.** Settings ΓÇ║ Accounts lists everyone who has joined; deleting one is the
  same as its owner closing it, and signs them out everywhere. See [Inviting others](docs/guide/web.md#inviting-others).
- **Close your account on the web.** Settings ΓÇ║ Account ΓÇ║ Close accountΓÇª deletes your account, library, covers, storage
  connections and the books kept in that browser, and its backups within 30 days. Nothing in your storage is touched.
  See [Signing in and out](docs/guide/web.md#signing-in-and-out).
- **The web reader's own actions.** Add page and Add pagesΓÇª add pages, or a picture, to a binder with Undo; Mark as
  adventure and the contents' Index as its own adventureΓÇª give pages a card of their own; Use this page as the cover
  and Use this image as the pack's cover change a card's cover, as in the Windows reader. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **Index pages link to their pages on the web.** The page numbers on an index or contents page that has no links of
  its own become links in the web reader, found by the same rules as in the Windows reader. See
  [Reading a book](docs/guide/web.md#reading-a-book).
- **The binder reader on the web.** Opening a binder shows its items down the left, the item in hand in the reader and
  the binder's notes on the right, with Next and Previous (Ctrl+Right and Ctrl+Left), Add this page, renaming in the
  list, Back to this item's pages, the page notes on the item's pages, and Use this page for an item whose pages
  changed. Edit binder opens the list, which has Done and opens each item at its pages. See
  [Binders](docs/guide/web.md#binders).
- **At hand and Pop out in the web binder reader.** At hand lists the binders kept at hand with their pages, those for
  the binder's game systems first, and opens a page in a window of its own; Pop out moves the binder into its own
  window and Return to main window brings it back. See [Binders](docs/guide/web.md#binders).
- **WebDAV and Nextcloud on the web.** Settings ΓÇ║ Storage connects a WebDAV server, such as Nextcloud, ownCloud or a
  NAS on the internet, with its folder's address, your user name and an app password, kept encrypted and used only to
  read. Its books open in the reader through the server, and Nextcloud's changes are noticed within five minutes. See
  [Connecting your storage](docs/guide/web.md#connecting-your-storage).
- **Box on the web.** Settings ΓÇ║ Storage connects a Box account, read-only, once the server's Box app is set up. Its
  books open in the reader through the server, and changes are noticed within five minutes. See
  [Connecting your storage](docs/guide/web.md#connecting-your-storage).
- **The sidebar folds on the web, and the search guide is there.** The button beside Settings, or Ctrl+B, folds the
  sidebar to its icons as in the Windows app. The search box's guide lists the fields to search by and, after a field's
  colon, its values in your library with their counts; the Windows app and the web now list them from the same code.
- **The details say which storage service a file is in.** Where it is, and each copy, start with the service, such as
  OneDrive, Dropbox or Box, with a cloud mark, before the account and folders.
- **Online libraries on the web.** Settings ΓÇ║ Online libraries imports the books you own on D&D Beyond, Demiplane,
  Humble Bundle and DriveThruRPG from a library page saved with Save as, with the Windows app's preview, Import with
  Undo, covers from the pictures saved beside the page, and the books no longer listed. See
  [Online libraries](docs/guide/web.md#online-libraries).
- **More of the Windows app's book actions on the web.** A book's menu and details have Open on the online library you
  own it on, Ask this book, Forget its text and Read it again, and Download the file for one whose protection Bibliotaph
  can't open. Reprocess has Reprocess with OCR on every pageΓÇª, a book owned only online opens its page there, and Home
  offers to set up AI as the Windows app does.
- **Locked PDFs on the web, as in the Windows app.** The reader's password dialog has Make its text searchable, so the
  book is read for search with it, and Remember it, which keeps the password sealed in your web library so the book
  opens without asking. Settings ΓÇ║ Passwords lists those books and forgets their passwords, and Needs review's locked
  files have Open to unlock, and protected ones Download the file. See [Reading a book](docs/guide/web.md#reading-a-book).
- **See what the AI is reading, on the web.** Settings ΓÇ║ AI and Settings ΓÇ║ Processing open the Windows app's AI reading
  page: the book the AI is on, its answer as it writes it, and what the evidence check kept from the last books. See
  [AI](docs/guide/web.md#ai).
- **Check a download on the web.** The Library's Add button has Check a downloadΓÇª, which checks a folder or ZIP on your
  computer against your library as the Windows app does. Your browser reads the files and sends only their fingerprints,
  never the files. See [Check a download](docs/guide/web.md#check-a-download).

New in 1.3.590 (19 October 2026):

- **See each file in a pack.** Two new buttons beside covers and list switch the Library between showing each pack as
  one card (as before) and showing every image and PDF inside packs on a card of its own. A file's card says which
  pack it is in, opens in its pack so the arrows still step through the rest, and finds its pages under its own card.
  Filters, saved searches and its details are its pack's. The choice is remembered, for browsing and searching alike.
- **One Add button in the Library.** **Owned elsewhere** and **Check a download** are now one **Add** button, styled
  like **New collection**, beside **Add folder**. Its menu has **Add a book I own elsewhereΓÇª**, **Import from an
  online libraryΓÇª** and **Check a downloadΓÇª**, which work as before.
- **Back up now starts in the backups folder.** The save dialog opens in the backups folder in Bibliotaph's data
  folder, beside the weekly backups, instead of wherever Windows last saved a file. You can still pick anywhere else.
- The Library's **Select** button shows a stack of ticked boxes instead of a plain tick, so it reads as choosing
  several books.
- **The sidebar's status stays inside the sidebar.** A long status, such as indexing a big library, wraps to a second
  line instead of running past the sidebar's edge and being cut off. Pointing at it shows all of it, also with the
  sidebar folded.

## 1.2 ┬╖ 13 October 2026

New in 1.2.573 (15 October 2026):

- **EPUB books are read.** EPUB files in your library folders, and inside ZIPs, are indexed in place like PDFs: their
  text is searchable page by page, the AI can catalogue them, and their own title, authors, publisher, description
  and cover fill in the card. A book that marks its printed pages keeps those page numbers; otherwise Bibliotaph cuts
  it into pages of about a printed page each, the same way every time. `format:epub` finds them. A DRM-protected
  EPUB still gets a card with its title and author, and is listed in **Files needing attention**.
- **Kindle books are read.** MOBI, AZW and AZW3 files are indexed in place and read in Bibliotaph's reader like
  EPUBs, with their contents, pictures and links, and their title, authors, publisher, description and cover fill in
  the card. Their pages are cut by length, as Kindle keeps printed page numbers in a separate file. `format:kindle`
  finds them. A Kindle book with DRM, or in Amazon's newer KFX format, gets a card but its text isn't read.
- **Read e-books in Bibliotaph.** An EPUB opens in the reader as flowing text in the app's colours, with its
  pictures. The page box, contents, Find, notes, binders and **Ask this book** work as in a PDF, search opens it at
  the page it found with the words marked, its links go to their place with **Back**, and zoom makes the text bigger.
  A comic or picture book shows its pages as drawn. Nothing in a book can run a program or reach the internet.
- **Pick a pack's cover.** A map or image pack can show one of its own images on its card instead of the mosaic of
  its first four. Right-click an image in the pack's details and choose **Use as the pack's cover**, or click the
  picture button in the reader while stepping through the pack. **Use the mosaic** in the details goes back, and the
  card goes back by itself if that image is deleted or leaves the pack.
- **Close a suggestion with its ├ù.** The Needs review cards that offer to make a collection, add books to one, make
  an image pack, start a folder rule or give a file's adventures cards of their own now have a ├ù in the corner. It
  gives the card's "don't ask again" answer, so that suggestion stays closed after a restart or a rescan, while a new
  one can still come. **Undo** brings it back.
- **Fixed: a book with only names on its pages could fail the AI every time with "The model's answer wasn't valid
  JSON".** On a map, with nothing but place names, the built-in AI quoted the whole page over and over until its
  answer ran out of room and stopped part way. Each value and quote the AI writes now has a length limit, and an
  answer that still runs out keeps what the model finished, checked like any other. A book cut off before the model
  finished anything says so: "The model's answer was cut off". Books that failed this way are read again when they're
  next tried, or press **Try all again** in **Files needing attention**.

New in 1.2.544 (15 October 2026):

- **Check a book beside Needs review.** Each card has a button beside its title that opens the book in a window of
  its own, at the page the suggestion's evidence is on, while Needs review stays put so you can read and decide side
  by side. Middle-click or **Shift+Enter** on the title does the same. The cards share the window, so going down the
  list shows each book in it instead of opening another.
- **Adventures show who they're for.** The Library's list shows each book's players ("4ΓÇô6 players") beside its levels,
  and the details header shows both. An adventure's details show **Players** even before anyone has set it, so you
  can fill it in straight away.
- **Adventures pick up their party size from their blurb.** When you accept a file's proposed adventures, a blurb
  such as "for four characters of 3rd level" now suggests the players as well as the levels, and the Needs review
  card lists both. An adventure you mark by hand, from the reader, a bookmark or the details, gets the same
  suggestions from the blurb on its first pages, quoted as evidence. Values you set are never replaced.

New in 1.2.530 (15 October 2026):

- **Adventures are proposed only when the book says it holds them.** A campaign whose chapters each start with a
  level blurb no longer looks like a collection. The rules and the AI now look for the book's own words, before its
  first adventure or on its back cover, saying it is a collection of separate adventures or one-shots, and the Needs
  review card shows those words and their page. Proposals still waiting are cleared and the rules look at the library
  again; files you already accepted or kept as one book stay as they are. The AI asks the stricter question for books
  it reads from now on. Blurbs such as "four 1st- to 2nd-level PCs" are now read, and bookmarks for covers and ads,
  or a contents page listing levels, no longer make adventures.
- **Fixed: the question about a file's adventures could send watermark lines to the AI.** The lines naming the buyer
  and their order are now left out, as they already were when the AI catalogues a book.
- **The Library remembers its order.** The order picked in the toolbar, for the books and for the matching pages,
  stays the next time the Library opens, after a restart too. Browsing and searching each keep their own, so
  **Best match** still comes with a search. A saved search still opens in the order it was saved with.
- **Fixed: Undo after removing an adventure sent it to the AI again.** It now comes back still read by the AI. Undo
  after removing a book owned elsewhere also brings back its page notes, Ask conversation and reading place, which
  it could have kept from a time when a file was joined to it.
- **Fixed: a reader could stay blank.** Closing or switching a book at the moment a page was being drawn could stop
  the reader drawing pages until it was closed. A page that can't be drawn now says why and the rest carry on.
- **Fixed: Ask this book failed on some pages.** A page whose PDF text holds a broken character made the question
  fail; those pages are now searched like any other.
- **index.db is kept when it is only busy.** If another program holds the search index or the disk can't be read at
  start, Bibliotaph now says so instead of deleting the index and reading the whole library again. A damaged index is
  still rebuilt.
- **An AI answer with nothing in it is treated as the AI's hiccup, not the book's.** The AI is checked before a book
  is marked as one it couldn't read, so a server that has stopped answering pauses the AI work instead.
- **An unexpected error now closes Bibliotaph cleanly** instead of leaving it running in a half-finished state. A
  problem outside the app, such as a file that went away, a busy clipboard or the AI server not answering, is still
  just shown, and the app carries on.
- **Problems that used to pass silently are now in the log:** a library sync change that keeps failing to apply, and
  background work such as loading a cover or saving a note that fails.

New in 1.2.487 (14 October 2026):

- **Choose from more built-in AI models.** **Settings > AI > Built in** now has one list, with the models that fit
  your graphics card first: Qwen3 4B and 8B, Phi-4 mini, **Llama 3.1 8B** and Granite 3.3 8B for most cards, and
  Qwen3 14B, Mistral Small 3.2 24B and Qwen3 30B-A3B for bigger ones. Each shows its download size, the graphics
  memory it needs and its licence, and every download is checked against a pinned hash. **Use a model fileΓÇª** runs a
  `.gguf` model already on your PC, such as one LM Studio downloaded, where it is.
- **Fixed: AI stopped with "request exceeds the available context size".** Pages dense with stat blocks or tables
  could make a whole-book scan ask more of the built-in AI than it has room for, and the AI then waited on those
  chapters for ever. They're now cut to fit and read, and if even that fails only those chapters are skipped.

In 1.2:

- **Built on .NET 11 RC2**, the second release candidate.
- **Ask your library a question:** in Ask mode, **Answer from my books** (or Ctrl+Enter) answers from the pages of
  every book the Library shows, citing each claim's book and page as a link that opens it there, and saying plainly
  when your books don't cover it. With an embedding model, your whole library is read for meaning in the background,
  pausing while you use the AI; **Settings > AI** shows how far it has got and can switch it off.
- **Ask this book finds the right pages more consistently:** index and contents pages no longer crowd out the pages
  that answer, a question's rare words count for more than its common ones when deciding whether the book covers it,
  the AI answers without "thinking" first, and a book is prepared for finding by meaning as soon as it's opened.
- **Your tags can find themselves.** In **Settings > Vocabulary > Your tags**, give a tag the words that signal it
  ("Undead": undead, zombie, lich) and every book whose pages use them often enough gets it as a suggestion, without
  reading any file again. New books are checked as they're read, and a tag you reject on a book stays rejected.
- **Collapse the sidebar** to its icons with the button beside Settings or **Ctrl+B**. It stays the way you left it.
- **Setting**, **Themes**, **Environments**, **Publisher** and **Edition** filters under **More filters**: type a few
  letters, pick from the values in your library with their book counts, and each pick becomes a chip. Chips in one box
  find books with any of them, boxes narrow together, and saved searches keep them.
- **Ask this book finds pages by meaning with the built-in AI.** Settings > AI offers nomic-embed-text (about 150 MB)
  to download, so Ask no longer matches words only.
- **Double-click a book** in the Library to open it; a single click still shows its details.
- **Remove from library** is on the right-click menu of a book you own elsewhere, as well as in its details.
- **Lists are in name order**: the filter drop-downs, the search guide's values, collections and sub-collections, the
  Add to collection menu, and binders within each group. The sidebar's Pinned list keeps the order you dragged it into.
- **Collection** under More filters shows the books that aren't in any collection yet, or those that are.
- **Remove adventures** in the Library's Select mode takes away the cards of several adventures inside files at once,
  with one Undo for them all.
- **Packs hold PDFs too.** Tick PDFs, such as character sheets or pregens, with images or packs in Select mode and
  **Pack together** makes them one card, "Pack ┬╖ 6 files". Opening it steps through its files, PDFs opening as PDFs;
  their pages are found under the pack; **Split into separate files** gives each its card back. Folders are still
  packed automatically only for their images.
- **Choose a cover imageΓÇª** gives a book you own elsewhere a cover of your own, from the Add dialog or its details.
- **Use this page as the cover** in the reader makes any page of a PDF its cover in the Library.
- **Show** on Needs review's Metadata suggestions narrows the list to one kind of card, or to the suggestions for one
  field, with a count for each.
- **Collections from folders**: Needs review offers to make a folder of three or more books a collection, nested as
  the folders are. Books that land in the folder later join it, until you change its books by hand.
- **Books for collections**: Needs review suggests books that share a series, or a setting and game system, with most
  of a collection's books.
- **Scan whole books** in Settings > AI (on by default) has the AI read every page of each catalogued book, a few
  chapters at a time, when it has nothing else to do. A book's details show what it found under **Found inside the
  book**, the reader's contents show it beside each chapter, and `setting:`, `environment:`, `theme:` and the new
  `creature:` search find books by their chapters. The book's own details are never changed.
- **Adventures stay inside their book** while you browse the Library: the anthology's card says how many it holds and
  its details list them. A search still finds each adventure, shown with its book's cover and an **Adventure** ribbon,
  in place of the book when both match. **Adventures inside files** in More filters shows them as cards again.
- **Fixed:** importing from Demiplane found no books, because its Sources page now links each book relative to the
  site. Both kinds of link are read.
- **Fixed:** a pop-out window opened from a drawer or menu could stay behind the main window; it now comes to the
  front. The online reader's header matches the PDF reader's, and the details drawer's headings are easier to read.

## 1.0.0 ┬╖ 12 October 2026

Bibliotaph's first release: a Windows app that catalogs the RPG books, maps and handouts you already have, where they
are, and finds the right book and the right page.

- **Your library, in place.** Add your folders and Bibliotaph reads every PDF, JPG, PNG and WebP in them, ZIPs
  included, on local, USB, network and OneDrive folders. It never moves, renames, edits or deletes a file, and notices
  files that are added, moved or changed. Scanned pages are read with Windows' own text recognition.
- **Search every page.** Books on the Documents tab, matching pages with snippets on Inside documents, fields such as
  `system:`, `type:`, `level:` and `players:`, filters shown as chips you clear one at a time, and saved searches.
- **A reader built for the table.** Contents, find in the book, zoom, thumbnails, page notes, index and contents pages
  whose page numbers are links, and as many pop-out windows as you like.
- **A catalog that fills itself in.** Title, game system, type, levels, players, duration, setting, themes and more,
  suggested from folder names, the file and, if you turn it on, AI, each with the evidence it came from. Your own
  corrections always win. Copies share one card, map folders become image packs, and adventures inside an anthology
  get cards of their own. Needs review and Files needing attention list what wants a look.
- **Optional, local AI.** Bibliotaph can download and run a model itself, or use Ollama, LM Studio or another model
  server. It catalogs books, turns a plain-English request into a search with Ask, and answers questions from a book's
  own pages with Ask this book, citing each page.
- **Books you own online.** D&D Beyond and Demiplane books are listed beside your files and read in Bibliotaph's online
  reader; Humble Bundle and DriveThruRPG books are listed under where to get them, and download into your library. You
  sign in to each site yourself; no password is stored.
- **Organized your way.** Favorites, collections, saved searches and pinned books and views in the sidebar.
- **Binders for your next session**, holding books, pages and online pages in order, with notes, and Next and
  Previous at the table.
- **Your work kept safe.** Weekly backups, restore on a new PC, export to a spreadsheet, and a library sync file to
  share your work with a second PC.
- **Installs per user** with no administrator password, and updates itself in the background. Not code-signed yet.
