# Bibliotaph PDF feasibility spike

Throwaway code that answers one question: can PDFium, isolated in a worker process, render, extract and survive
a real collection of purchased RPG PDFs, with a WPF viewer that stays smooth? The plan and the pass gates were
in a private planning doc; the results are summarized in `NOTES.md` and `docs/architecture.md`.

The corpus itself is not in the repo: the books are purchased and the fixtures cut from them are gitignored.
`corpus/manifest.json` lists the files relative to `<corpus>`; set its `root` to your own folder before a run.

| Project | What it is |
| --- | --- |
| `src/Spike.Contracts` | Messages and pipe framing shared by everything |
| `src/PdfWorker` | Console app that owns PDFium; one per host |
| `src/Spike.WorkerHost` | Starts, feeds, times out, restarts and memory-caps the worker |
| `src/Harness` | Batch run over `corpus/manifest.json`; writes CSVs to `results/` |
| `src/ViewerPrototype` | WPF viewer: virtualized pages, zoom with tiling, page labels, search highlights |

## Build

Needs the .NET 11 RC SDK (x64). Everything builds into `bin\Debug\`.

```powershell
cd spikes\pdf-feasibility
dotnet --list-sdks          # expect an 11.0.100-rc.* entry
dotnet build Bibliotaph.Spike.slnx
```

On an older SDK, build with `dotnet build Bibliotaph.Spike.slnx -p:BibTfm=net10.0`.

## Run

1. Self-test the isolation (crash, hang, memory runaway), about 20 seconds:

   ```powershell
   .\bin\Debug\Harness.exe --self-test
   ```

2. Run the corpus. It reads every file in the manifest and never writes to them. Expect it to read about 3 GB
   from OneDrive; files that are online-only will download first.

   ```powershell
   .\bin\Debug\Harness.exe
   ```

   Results land in `results\<timestamp>\`: `files.csv` (one row per file, against the gates), `pages.csv` (every
   timed page) and `integrity.csv` (source hashes before and after). Useful options: `--only Coriolis` to run a
   subset, `--all-pages` to render every page, `--memory-mb 2048` to raise the worker cap.

3. Try the viewer:

   ```powershell
   .\bin\Debug\ViewerPrototype.exe "<corpus>\DungeonCrawlerCarl\carl_rpg_core_rulebook_digital_hi-res_watermarked.pdf"
   ```

   Fling through the book, switch zoom to 400% on a map, jump to a printed page label, and search (Ctrl+F, F3).
   The status bar shows render p50/p95, blank-page time, the worst UI-thread stall and the worker's peak memory.
   Run it at 100% and at 200% Windows display scaling.

## Send back

Zip `results\<timestamp>\` and note the viewer's status-bar numbers for the 650-page book and a map at 400%,
plus anything that looked wrong. Screenshots of any page that renders differently from Acrobat or Edge help most.
