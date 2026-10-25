# Web version feasibility spike

Throwaway code that answers one question before the web version is designed in detail: can a browser run
Bibliotaph's own index code, keep it on disk, and read and cache books well enough for a library the size of a
real one? Nothing here ships. The production design will take the findings, not the code.

Everything runs on synthetic data: the text generator in `src/Spike.Bench` and the PDFs from `src/Spike.Corpus`.
No real book, title or path is used or committed.

## What is here

| Path | What it is |
| --- | --- |
| `src/Spike.Web` | Blazor WebAssembly on .NET 11 with the Blazor web worker. The worker runs the real `Bibliotaph.Index` (Dapper, FTS5) against SQLite compiled into `dotnet.native.wasm`. |
| `src/Spike.Web/Native/opfsvfs.c` | A SQLite VFS over OPFS synchronous access handles, with a block cache shared by every connection in the worker. |
| `src/Spike.Bench` | The shared benchmark: a Zipf text generator (a realistic mix of very common and rare words) and the library workload. |
| `src/Spike.Native` | The same workload on desktop .NET, as the baseline. |
| `src/Spike.Corpus` | Writes the test PDFs: 40 and 300 text pages, and 120 pages with a large image on each. |
| `bench/` | PDF.js and hash-wasm in a plain page: SHA-256, text extraction, first render, and the OPFS book cache (`cache.js`). |
| `tools/` | A Range-capable static server and the Playwright drivers that produced `results/`. |

## Running it (PowerShell)

```powershell
dotnet publish src/Spike.Web -c Release -o out/web
dotnet run --project src/Spike.Corpus -c Release -- $env:TEMP\spike-books C:\Windows\Fonts\arial.ttf
Push-Location bench; npm ci; Pop-Location
node tools/patch-pdfjs.mjs
Start-Job { node tools/serve.mjs out/web/wwwroot 8080 }
Start-Job { node tools/serve.mjs bench 8081 $env:TEMP\spike-books }
node tools/scale.mjs http://localhost:8080/ 500 100 opfs
$env:MBPS = 50; $env:PATCHED = 1; node tools/pdfbench.mjs http://localhost:8081/ illustrated-120.pdf
```

The drivers need Playwright (`npm i -g playwright`) and Chromium.

## Results

Measured in headless Chromium on a 4-core cloud container, so absolute numbers are pessimistic for a desktop; the
ratios are what matter.

### The index in the browser

| Warm searches | Native .NET | Browser, in memory | Browser, OPFS |
| --- | --- | --- | --- |
| Index 500 books x 100 pages (50,000 pages, 265 MB) | 19.5 s | 36.8 s | 76.6 s |
| `owlbear` (969 pages) | 32 ms | 84 ms | 170 ms |
| `owlbear tavern` | 66 ms | 121 ms | 132 ms |
| `"red dragon"` | 10 ms | 19 ms | 16 ms |
| `necromancer crypt -vampire` | 19 ms | 39 ms | 40 ms |
| `damage` (34,355 pages) | 636 ms | 944 ms | 9.4 s |

Download for the app with SQLite and the index code: about 3.4 MB brotli. The OPFS database survives a reload.

Each OPFS call costs about the same whatever its size (here about 0.8 ms for 4 KB and 1.3 ms for 64 KB; a desktop
is faster, but the shape holds), while SQLite reads and writes one 4 KB page at a time. The first OPFS run, with
every page going straight to OPFS, took 335 s to index and about 1 s for `owlbear`. `opfsvfs.c` now keeps a 64 MB
block cache below SQLite, shared by every connection in the worker: reads come from it with a little read-ahead,
and writes stay in it until SQLite syncs, then go out as runs of up to 1 MB. That made indexing 4.4 times faster
and typical searches 5 to 7 times faster, close to the in-memory numbers. Durability is unchanged (a sync writes
the dirty blocks before flushing), and a reload without closing the database keeps everything committed.

`damage` stays slow on OPFS because its 34,000 pages of text are bigger than the cache.

A side finding that applies to the desktop app too: FTS5 is very slow for a word on nearly every page. `the` over
50,000 pages took 42 s natively. The search box should treat such words as stop words or cap the page query.

### Hashing

SHA-256 of the 47 MB book, all three matching `sha256sum`:

| | Hashing time | Streams |
| --- | --- | --- |
| .NET `IncrementalHash` in the worker | 67 ms | yes |
| SubtleCrypto | about 70 ms | no, whole buffer only |
| hash-wasm | about 250 ms | yes |

.NET's browser build hashes in native code inside `dotnet.native.wasm`, so the pipeline keeps the
`IncrementalHash` it uses today and needs no extra dependency. At 50 Mbps the download (about 8 s) dwarfs all of
them.

### PDF.js

| Book | Text, all pages | First render |
| --- | --- | --- |
| 40 text pages, 0.1 MB | 0.11 s | 60 ms |
| 300 text pages, 0.5 MB | 0.75 s | 50 ms |
| 120 illustrated pages, 47 MB | 0.3 s | 50 to 100 ms |

### Book cache

`bench/cache.js` stores a book in 1 MB OPFS chunks and serves PDF.js range reads from them; whatever was read once
never downloads again, and the rest completes in the background (paused whenever the reader needs something).

The 47 MB illustrated book at 50 Mbps:

| | Open + first page | Downloaded before page 1 |
| --- | --- | --- |
| PDF.js reading the server directly | 8.1 s | the whole file |
| Cache, stock PDF.js | 8.2 s | the whole file |
| Cache, patched PDF.js | 1.0 s | 2 MB |
| Cache, reopened with the server unreachable | 0.5 s | nothing |

Stock PDF.js checks the last page on open, and for a flat page tree it fetches every page object at once. When
page objects sit beside their images, as in this test file, that is the whole book. `tools/patch-pdfjs.mjs` skips
the check outside recovery mode; with it the cache asks for the newest request first, so the page on screen jumps
ahead of PDF.js's prefetch. Many real PDFs have balanced page trees and will not hit this; the patch is cheap
insurance either way and needs testing against real books.

`navigator.storage.persist()` is refused in a headless, never-visited profile. A real installed PWA usually gets
it; the app has to cope when it does not, because unpersisted storage can be evicted.

Raw numbers are in `results/`: `index-*.json` for the index (`opfs-uncached` is the run before the block cache),
`pdf.json` with 30 ms of latency and no bandwidth limit, `pdf-50mbps-stock.json` and `pdf-50mbps-patched.json` at 50 Mbps, and
`hash-dotnet.json`.

## Still open

- Dave's real index.db and books, on his PC.
- OneDrive sign-in with MSAL.js and range reads from Graph download URLs (needs an app registration).
- Several tabs at once (Web Locks around the single worker that owns the databases).
- Safari and Firefox: OPFS synchronous access handles exist in both, but nothing here was run on them.
