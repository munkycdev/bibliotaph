# AI

[Bibliotaph](../../README.md) › [Guide](../../README.md#the-guide) › AI

AI is optional. Without it, Bibliotaph still finds, searches and opens everything, and fills in what it can from folder
names and the files themselves. With it, a model running on your own PC reads a little of each book and suggests the
rest of its details, so far fewer books need you.

## What it does

For each book, the model reads a few pages: the opening pages, the contents, the introduction and a sample of the
rest. It suggests the title, game system, edition, type, levels, players, duration, style, publisher, authors, series,
year, setting, themes and environments.

- **Every suggestion has to quote the page it came from.** Bibliotaph checks the quote is really on that page, and
  throws the suggestion away if it isn't. You can see the page and quote for each one in the book's details.
- **A book is an adventure only when it is one.** The model is told what each type means, and an Adventure
  suggestion has to quote where the book calls itself one, such as "an adventure for four characters of 3rd level". A
  rulebook, setting, bestiary or supplement isn't called an adventure because it has a sample adventure, adventure
  hooks or character levels. Books the AI read before this update keep their suggestions until you
  **Reclassify** them in **Settings > AI**.
- **Your values always win.** Anything you've typed, accepted or rejected stays as it is. AI never fills in your tags.
- **It learns from you.** The model is shown a few books you've already sorted out, from the same folder or system, as
  examples of how you catalog.
- **It reads whole books, chapter by chapter**, once everything else is done. See
  [Scanning whole books](#scanning-whole-books).
- **It finds adventures inside anthologies**, when the book says it is a collection of them, and reads each one on
  its own pages. See
  [Cataloging](cataloging.md#adventures-inside-a-file).
- **It reads requests in plain English** in the search box's **Ask** mode. See
  [Searching](searching.md#ask-in-plain-english).
- **It answers questions about a book** from the book's own pages, citing and checking each one. See
  [Reading](reading.md#ask-this-book).
- **It answers questions from your whole library**, citing the book and page of each claim. See
  [Searching](searching.md#ask-your-library-a-question).
- **Book text can't give it orders.** Text in a book is treated as text to read, never as instructions, and an answer
  can only fill in suggestions.

## What it sends, and where

Nothing leaves your PC unless you set it up that way.

- **Built in**: Bibliotaph downloads a model once and runs it itself, on your PC. Nothing is sent anywhere.
- **A model server**: a program such as Ollama or LM Studio that you install and run yourself. Bibliotaph sends each
  book's excerpt (a few thousand words of its text) to that server. If the server is on another computer outside your
  network, Settings says so, and you have to tick **Send excerpts to this server** first. **Ask** sends it what you
  typed, **Ask this book** and **Answer from my books** send your question with the few pages that answer it, and,
  with an embedding model, each page's text is sent once to find pages by meaning.

Bibliotaph never switches to a cloud service by itself.

## Set it up: Built in

1. Open **Settings > AI**, and choose **Built in** under **Where the model runs**.
2. Pick a model from the list. Those that fit your graphics card come first, and Bibliotaph preselects the one it
   suggests: **Qwen3 8B** (about 5 GB) for a card with 7 GB of memory or more, or **Qwen3 4B** (about 2.5 GB) for a
   smaller card or a PC without one. The list also has Phi-4 mini, Llama 3.1 8B and Granite 3.3 8B for 8 GB cards, and
   Qwen3 14B, Mistral Small 3.2 24B and Qwen3 30B-A3B for bigger ones. Each shows its download size, the graphics
   memory it needs and its licence. Llama 3.1 comes under Meta's own licence; the others are Apache-2.0 or MIT.

   Already have a model? **Use a model file…** uses a `.gguf` file on your PC where it is, such as one LM Studio
   downloaded. Bibliotaph never changes or deletes it.
3. Click **Download**. You can pause it and carry on later, even after closing Bibliotaph. As soon as the download
   finishes, AI switches on and starts reading.

**Test with a book** tries the model on one of your books and shows what it kept, with each page and quote, and what it
threw away and why. **Delete model** frees the space.

The built-in AI uses your graphics card when it can and falls back to the processor, which is much slower. It only
runs while it has books to read.

Under **Ask this book**, **Download the embedding model** (nomic-embed-text, about 150 MB) lets **Ask this book** and
[questions to your library](searching.md#ask-your-library-a-question) find pages by what they mean as well as by their
words. Without it, they find pages by their words only. **Delete embedding model** frees the space.

## Set it up: a model server

1. Install and start your model server, and download a model in it.
2. In **Settings > AI**, choose **Use a model server**.
3. Type its **Address** (Ollama is usually at `http://localhost:11434` and LM Studio at `http://localhost:1234`) and
   click **Connect**.
4. Choose a **Model** and click **Save**. AI switches on.

If the server needs a key, type it under **Key, only if the server needs one**. It's kept in Windows Credential
Manager and never shown again.

**Embedding model, for Ask this book** lets **Ask this book**, and questions to your library, find pages by what they
mean, not only by their words. Download one such as nomic-embed-text on the server first, then choose it here.

## Scanning whole books

Cataloging reads only a few pages of each book. With **Scan whole books** on (in **Settings > AI**, on unless you turn
it off), the AI then goes back and reads every page, a few chapters at a time, and notes the setting, environments,
themes and creatures in each chapter, with the pages they're on.

- **Chapters come from the book.** Bibliotaph uses the book's bookmarks (an e-book's contents), else its printed
  contents page, else runs of about 25 pages. Long chapters are read in parts.
- **It waits its turn.** It starts only after a book is catalogued, and stops the moment you use the AI for anything
  else (cataloging, **Ask**, **Ask this book**), carrying on half a minute after you've finished. Closing Bibliotaph
  doesn't lose its place.
- **It never changes a book's own details.** What it finds is shown apart, under **Found inside the book** in the
  book's details, and beside each chapter in the reader's **Contents**. **Scan again** there reads the book again.
- **Search uses it.** `setting:`, `environment:` and `theme:` also find books by what's inside them, and `creature:`
  finds the books with a creature in one of their chapters, such as `creature:owlbear`.
- **Each finding is checked like any other**: its quote must be on its page, inside its chapter.
- **Dense pages still fit.** Stat blocks and tables take up more of the model's room than ordinary text. When a few
  chapters turn out too long for the model, they're sent again with each page cut shorter, and if even that doesn't
  fit, only those chapters are skipped.
- A book is read once. It's read again only if the file changes or you ask. What it found stays on this PC, even if
  the index is rebuilt, and isn't part of sync.
- **Settings > Processing** has a bar for it, **Scanning whole books**, under the AI's, and **Settings > AI** says how
  far it's got: "Scanning whole books · 12 of 40".

## Day to day

- **Use AI** turns it **On** or **Off** without losing your setup.
- **Pause AI** in **Settings > Processing** stops it for now, while indexing carries on.
- **See what the AI is reading** (in **Settings > Processing** and **Settings > AI**) shows the book it's on, its
  answer as it writes it, and what it kept from the last books.
- **Library folders sent to AI**: untick a folder to keep its books away from the model. They still get suggestions
  from their names, and you can fill them in yourself.
- If the model isn't answering, AI waits and tries again every minute. Nothing else stops.
- Changing the model never re-reads your books on its own, and neither does an update that changes what the model is
  asked. **Settings > AI** counts the books an earlier model or an earlier version of Bibliotaph read and offers
  **Reclassify**, with the number, to read them again.
- Adding names to your lists doesn't re-read your books either: a name the AI used is kept with the books it used it on,
  but a name it never used isn't found in a book it has already read. The button at the end of **Cataloguing with AI**
  in **Settings > Processing** reads every book again: it sends every book the AI has read back to it with your lists
  as they stand now, and asks first,
  since it takes as long as cataloguing them did. Values you've accepted, typed or rejected stay as they are, and the
  rest of the suggestions are replaced as each book is read. Nothing re-reads your library unless you press it.
- **An answer that runs out of room still counts.** Each value and quote the AI writes has a length limit, so a page of
  nothing but names, such as a map's labels, can't keep it writing. If an answer still stops part way, what the model
  finished is checked and kept like any other; a book cut off before it finished anything says "The model's answer was
  cut off".
- **Files needing attention** in Needs review lists books the AI couldn't read, with **Try all again**.
  **Settings > AI** counts them too, with **Try again** (or, alongside books an earlier model read, **Reclassify**).

## Reading your library for meaning

With an embedding model set, Bibliotaph slowly reads every book in your library for meaning in the background, so
[a question to your library](searching.md#ask-your-library-a-question) and [`about:` in the search
box](searching.md#search-by-meaning) can find a page by what it means, not only by its words. For a large library this takes hours. It goes last: it waits while you're using the AI (asking a question,
say) and for a little after, gives way to a book you've just opened, and carries on where it was after you restart
Bibliotaph. Books in folders kept from AI are left out.

Before the pages, it reads each book's title, details, tags, notes and what the AI found in its chapters, which takes
minutes rather than hours, so `about:` can rank books straight away. That part runs whenever an embedding model is set,
and a book whose details change is read again.

**Settings > AI > Read the whole library for meaning** shows how many pages are read so far. Untick **Read the library
for meaning in the background** to stop it; questions to your library and `about:` then find the remaining pages by
their words.

---

Previous: [Online libraries](online-libraries.md) · Next: [OneDrive, drives and other PCs](onedrive-and-other-pcs.md)
