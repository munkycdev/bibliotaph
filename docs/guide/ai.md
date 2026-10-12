# AI

[Bibliotaph](../../README.md) › Guide › AI

AI is optional. Without it, Bibliotaph still finds, searches and opens everything, and fills in what it can from folder
names and the files themselves. With it, a model running on your own PC reads a little of each book and suggests the
rest of its details, so far fewer books need you.

## What it does

For each book, the model reads a few pages: the opening pages, the contents, the introduction and a sample of the
rest. It suggests the title, game system, edition, type, levels, players, duration, style, publisher, authors, series,
year, setting, themes and environments.

- **Every suggestion has to quote the page it came from.** Bibliotaph checks the quote is really on that page, and
  throws the suggestion away if it isn't. You can see the page and quote for each one in the book's details.
- **Your values always win.** Anything you've typed, accepted or rejected stays as it is. AI never fills in your tags.
- **It learns from you.** The model is shown a few books you've already sorted out, from the same folder or system, as
  examples of how you catalog.
- **It finds adventures inside anthologies**, and reads each one on its own pages. See
  [Cataloging](cataloging.md#adventures-inside-a-file).
- **It reads requests in plain English** in the search box's **Ask** mode. See
  [Searching](searching.md#ask-in-plain-english).
- **It answers questions about a book** from the book's own pages, citing and checking each one. See
  [Reading](reading.md#ask-this-book).
- **Book text can't give it orders.** Text in a PDF is treated as text to read, never as instructions, and an answer
  can only fill in suggestions.

## What it sends, and where

Nothing leaves your PC unless you set it up that way.

- **Built in**: Bibliotaph downloads a model once and runs it itself, on your PC. Nothing is sent anywhere.
- **A model server**: a program such as Ollama or LM Studio that you install and run yourself. Bibliotaph sends each
  book's excerpt (a few thousand words of its text) to that server. If the server is on another computer outside your
  network, Settings says so, and you have to tick **Send excerpts to this server** first. **Ask** sends it what you
  typed, and **Ask this book** sends your question with the few pages that answer it (and, with an embedding model,
  each page's text once, to find pages by meaning).

Bibliotaph never switches to a cloud service by itself.

## Set it up: Built in

1. Open **Settings > AI**, and choose **Built in** under **Where the model runs**.
2. Pick a model. Bibliotaph suggests the one that suits your graphics card:
   - **Qwen3 8B**, about a 5 GB download, for a graphics card with 7 GB of memory or more.
   - **Qwen3 4B**, about a 2.5 GB download, for smaller graphics cards or a PC without one.
3. Click **Download**. You can pause it and carry on later, even after closing Bibliotaph. As soon as the download
   finishes, AI switches on and starts reading.

**Test with a book** tries the model on one of your books and shows what it kept, with each page and quote, and what it
threw away and why. **Delete model** frees the space.

The built-in AI uses your graphics card when it can and falls back to the processor, which is much slower. It only
runs while it has books to read. With it, **Ask this book** finds pages by their words; to find them by meaning as
well, use a model server with an embedding model (see [Reading](reading.md#ask-this-book)).

## Set it up: a model server

1. Install and start your model server, and download a model in it.
2. In **Settings > AI**, choose **Use a model server**.
3. Type its **Address** (Ollama is usually at `http://localhost:11434` and LM Studio at `http://localhost:1234`) and
   click **Connect**.
4. Choose a **Model** and click **Save**. AI switches on.

If the server needs a key, type it under **Key, only if the server needs one**. It's kept in Windows Credential
Manager and never shown again.

**Embedding model, for Ask this book** lets **Ask this book** find pages by what they mean, not only by their words.
Download one such as nomic-embed-text on the server first, then choose it here.

## Day to day

- **Use AI** turns it **On** or **Off** without losing your setup.
- **Pause AI** in **Settings > Processing** stops it for now, while indexing carries on.
- **See what the AI is reading** (in **Settings > Processing** and **Settings > AI**) shows the book it's on, its
  answer as it writes it, and what it kept from the last books.
- **Library folders sent to AI**: untick a folder to keep its books away from the model. They still get suggestions
  from their names, and you can fill them in yourself.
- If the model isn't answering, AI waits and tries again every minute. Nothing else stops.
- Changing the model never re-reads your books on its own. **Settings > AI** counts the books an earlier model read and
  offers **Reclassify**, with the number, to read them again.
- **Files needing attention** in Needs review lists books the AI couldn't read, with **Try all again**.
  **Settings > AI** counts them too, with **Try again** (or, alongside books an earlier model read, **Reclassify**).

## Next

- [Cataloging](cataloging.md): checking and correcting suggestions.
- [Questions and answers](faq.md)
