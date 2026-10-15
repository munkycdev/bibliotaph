# Reading

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › Reading

Bibliotaph has its own reader, so you don't need to switch to another PDF app. Double-click a book in the Library to
open it, or click it once for its details and click **Open**. A book opened from the Library or Home opens where you
left it; one opened from a search result opens at the matching page. A book you own online, such as on
D&D Beyond, opens in the online reader instead; see [Online libraries](online-libraries.md#read-a-book-you-own-online).

## Getting around a book

- **Page box**: type a printed page number (the one on the page) or a PDF page number, and press **Enter**.
  **Ctrl+Home** and **Ctrl+End** go to the first and last page.
- **Zoom**: the zoom buttons, **Ctrl+Plus** and **Ctrl+Minus**, or **Ctrl** and the mouse wheel. **Ctrl+0** fits the
  page to the width again.
- **Contents**: the book's own bookmarks, in a panel on the left. Click one to go there. Once the AI has
  [scanned the whole book](ai.md#scanning-whole-books), each chapter also shows the settings, environments, themes and
  creatures found in it.
- **Page thumbnails**: the toolbar button shows a strip of small pages along the bottom. The page you're on is
  outlined, and pages with notes have a badge. Click a thumbnail to go to it.
- **Links**: links in the PDF work. On an index or contents page without links of its own, the printed page numbers
  become links too ("Go to page 34"). After following one, **Back to page …** takes you back.
- **Use this page as the cover**: the picture button at the top makes the page in view the book's cover in the Library,
  for books whose first page is blank or a plain title sheet. To go back, use it on the first page. Reading the file
  again keeps your choice.

## Find and copy

**Ctrl+F** finds words in the book. **Enter** or **F3** goes to the next match, **Shift+F3** to the previous one.

Drag across text to select it, double-click a word or triple-click a line, then **Ctrl+C** to copy. Scanned pages can
be selected too, using the text Windows read from them. If a PDF doesn't allow copying, Bibliotaph says so.

## Notes on pages

Click **Note** in the reader's toolbar to write a note on the page you're looking at (select some text first to note
that page). You can make a note cover several pages with **From page** and **To page**. **Ctrl+Enter** saves it.

The toolbar's notes button shows a panel listing the book's page notes in page order, each with **Go to**, change and
delete (with **Undo**). The book's details have a **Notes** tab with the same list, plus a note for the whole book.

A search finds a book by the words in any of its notes. If you get a new version of a book, or read another copy of
it, notes follow their page by its text, even when the page number has changed.

## Ask this book

With AI set up, **Ask** in the reader's toolbar opens a panel where you can ask the open book a question, such as
"What does the miller know about the cult?". **Ask this book** on a card's menu, or in a book's details, opens the book
with the panel showing. It's offered for PDFs with text.

Bibliotaph finds the pages most likely to answer, and the AI answers from those pages only:

- **Every claim cites its page**, as [p. 112]. Click a citation and the book opens at that page with the quotation
  marked.
- **Quotations are checked.** Bibliotaph looks for each quotation on the pages it cites. One that isn't there is
  shown in italics without its quotation marks, and a sentence whose citation pointed at a page the AI wasn't given is
  marked "(not on the pages it read)".
- **"The pages I was given don't answer that."** is what you'll see when the book doesn't seem to cover your
  question. The panel then lists the pages that came closest.

Under the answer, the passages it read are listed as links. **Stop** ends an answer early.

You can ask follow-up questions: "And where is his mill?" is understood in the light of the questions before it, and
the panel shows "Looked for: …" when it reworded your question to search. Each book keeps its conversation (its last
50 questions), and lists it again when you reopen the book. **New conversation** starts afresh.

Ask finds pages by their words. With an embedding model, built in or on a model server, it can also find them by
meaning, so "who's behind the disappearances" can find a page that never says "disappearances"; see [AI](ai.md). A
book's pages are prepared for that in the background as soon as you open it,
and the panel says how far it has got.

## Windows of their own

Right-click a book and choose **Open in new window** (or press **Shift+Enter**, or middle-click a cover) to read it in
a separate window. In the reader, **Pop out** moves the open book into its own window, and **Return to main window**
brings it back. Open as many as you like, the same book twice included. **Ctrl+W** closes a reader window. Needs review
can open a card's book in a window too, to check it before deciding (see
[Needs review](cataloging.md#needs-review)).

## Adding to a binder

**Add page** puts the page you're on into your current binder, and its menu has **Add pages…** for a range. See
[Binders](binders.md#add-books-and-pages).

## Maps and image packs

Images open with zoom and pan. In an image pack, the **Left** and **Right** arrow keys (or the arrows on screen) go to
the previous and next image. A pack with PDFs in it, such as a party's character sheets, steps the same way through
its files with the arrows on screen: each PDF opens as a PDF, and each image as an image.

WebP images need Microsoft's free WebP Image Extensions from the Microsoft Store. Without them, a WebP file shows in
**Needs review** saying so.

## Locked, protected and missing books

- **A PDF with a password** opens with a box asking for it. **Remember it on this PC** keeps the password in Windows
  Credential Manager, never in Bibliotaph's own files. **Make its text searchable** (ticked unless you untick it) lets
  Bibliotaph read the book for search. **Settings > Passwords** lists remembered passwords, with **Forget**.
- **A protected (DRM) book** that Bibliotaph can't read still gets a card you can fill in, and
  **Open in another app** opens it in the app that can.
- **A book on a drive that isn't plugged in** says so, naming the folder and drive. Plug it in and click
  **Try again**.
- **A book whose file has gone** offers **Find it**, which looks through every library folder, and
  **Show last folder**.

---

Previous: [Searching](searching.md) · Next: [Cataloging](cataloging.md)
