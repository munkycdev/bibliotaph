# Contributing to Bibliotaph

[Bibliotaph](README.md) › Contributing

Bibliotaph is a WPF app on .NET 11. PDFium runs in a small pool of isolated worker processes, user work lives in one
SQLite file (`catalog.db`, EF Core with migrations) and a rebuildable search index in another (`index.db`, plain SQL
with FTS5), and an in-process durable job queue does the reading.

- [docs/architecture.md](docs/architecture.md) is the design.
- [docs/features.md](docs/features.md) describes what each feature does.
- [docs/bibliotaph-ui-mockups.html](docs/bibliotaph-ui-mockups.html) is the visual target.
- The user guide starts at the [README](README.md) and lives in [docs/guide/](docs/guide/).

## Build and run

Needs the .NET 11 SDK on Windows x64.

```powershell
dotnet build Bibliotaph.slnx
dotnet test --solution Bibliotaph.slnx
dotnet run --project src\Bibliotaph.App
```

Until .NET 11's general release (due November 2026), Bibliotaph builds on the release candidate: `global.json` asks
for the RC SDK, `DotNetPackagesVersion` in `Directory.Packages.props` names the RC packages, and the workflows install
that exact SDK version. At GA all three move to the GA versions.

The app keeps its databases and logs in `%LOCALAPPDATA%\Bibliotaph`. To try it against a throwaway folder instead:

```powershell
dotnet run --project src\Bibliotaph.App -- --data-root $env:TEMP\bibliotaph-scratch
```

`--smoke-test` opens the window, visits every screen in light and dark, and exits with 0 or 1. CI runs it on pushes to
main, which every release waits for.

## The verify script

Before pushing, run the same checks as CI (in PowerShell 7):

```powershell
./build/verify.ps1             # restore, engine, build, licences, test, smoke test, banned-API probe, Release build
./build/verify.ps1 -SkipSmoke  # the same without opening the app's window
./build/verify.ps1 -Step Build, Test
```

CI runs each step of this script, so a green run here should mean a green run there. To keep pull requests quick,
their CI runs skip the smoke test and the Release build; pushes to main run every step. Run the smoke test yourself
before pushing a change to what the app shows. Off Windows the Engine and Smoke steps are skipped.

## Layout

| Folder | What |
| --- | --- |
| `src/Bibliotaph.Core` | Domain types and policies. References nothing, does no I/O. |
| `src/Bibliotaph.Catalog` | `catalog.db` (the user's work): EF Core, migrations, backup before migrating. |
| `src/Bibliotaph.Index` | `index.db` (derived, rebuildable): plain SQL, FTS5, the single writer. |
| `src/Bibliotaph.Processing` | Scanner, job queue and pipeline. The only reader of source files. |
| `src/Bibliotaph.Classification` | Rule hints and AI adapters. |
| `src/Bibliotaph.Pdf.Contracts` | Messages between the app and the PDF worker. References nothing. |
| `src/Bibliotaph.Pdf.Host` | Worker pool, job objects, restarts, poison-page tracking. |
| `src/Bibliotaph.PdfWorker` | The only process that runs PDFium. Ships in the app's `pdfworker\` folder. |
| `src/Bibliotaph.Viewer` | The page viewer. |
| `src/Bibliotaph.App` | The WPF shell, theme, fonts, icons and composition root. |
| `tests/` | xUnit v3 on Microsoft Testing Platform. PDF tests build synthetic PDFs at run time. |
| `build/` | The verify script, and the script that fetches the built-in AI's engine. |
| `tools/IconGen` | Turns the Lucide SVGs it holds into `src/Bibliotaph.App/Icons/Icons.xaml`. |
| `tools/AppIcon` | Draws the app icon (`src/Bibliotaph.App/Assets/Bibliotaph.ico` and `.svg`) from the mockup's monogram. |
| `tools/LicenseNotices` | Writes the licence file of every shipped package into `licenses\packages` (verify's Licenses step). |
| `tools/OcrBakeoff` | Compares OCR engines on your own scanned books. Its report quotes them, so keep it out of the repository. |
| `tools/Screenshots` | Fetches the public-domain books for the README's screenshots (see below). |
| `spikes/pdf-feasibility` | The PDF spike, kept for reference. Its own solution; not built by CI. |

## Rules the build enforces

- Warnings are errors, with the `latest-recommended` analyzers.
- Write-capable file APIs (`FileStream`, `File.Write*`, `File.Delete` and friends) are banned by `BannedSymbols.txt`
  everywhere except Processing, Pdf.Host, the PDF worker, tests and tools. CI proves it by adding `File.WriteAllText`
  to Core and expecting `RS0030`.
- Package versions live only in `Directory.Packages.props`.
- A new package must be permissive or GPL-3.0-compatible; verify's Licenses step fails on any other.
- No fixture cut from a purchased book is ever committed. Test PDFs are generated with PDFsharp when the tests run, and
  the repository ignores `*.pdf`.

## Common tasks

Add a catalog migration (needs `dotnet tool install --global dotnet-ef`):

```powershell
dotnet ef migrations add <Name> --project src\Bibliotaph.Catalog --output-dir Migrations
```

The app copies `catalog.db` into `backups\` before applying a pending migration. `index.db` has no migrations: change
`src\Bibliotaph.Index\Schema\index.sql`, bump `IndexSchema.Version` and the `user_version` at the end of the script,
and the index is rebuilt on next start.

Add an icon: copy its SVG from the same `lucide-static` version into `tools\IconGen\lucide\`, then:

```powershell
dotnet run --project tools\IconGen
```

Redraw the app icon after changing the monogram (needs Python with `pip install pillow fonttools`):

```powershell
python tools\AppIcon\make_icon.py
```

## Docs go with the change

Every feature and every fix updates the docs in the same pull request, so main always describes the app as it is:

- The user guide ([README](README.md) and [docs/guide/](docs/guide/)) wherever the change alters what someone sees or
  does.
- [docs/features.md](docs/features.md) for what the feature does, and [docs/architecture.md](docs/architecture.md)
  when the design changes.
- A bullet under `## Unreleased` at the top of [CHANGELOG.md](CHANGELOG.md), written for the people using the app.
  Start the heading if it isn't there.

## Releases

A version tag `vMAJOR.MINOR.PATCH` runs `.github/workflows/release.yml`:
a self-contained win-x64 publish, Velopack's pack, and a draft GitHub Release holding `BibliotaphApp-win-Setup.exe`
and the update files. The draft stays private until it's published, and installed copies update from published
releases only. See "Installer", "Releases" and "Updates" in [docs/architecture.md](docs/architecture.md).

PATCH is the build number: the count of commits on main at the tagged commit. The workflow refuses a tag whose PATCH
doesn't match. To tag the head of main:

```powershell
git fetch origin
$n = git rev-list --count origin/main
git tag "v1.2.$n" origin/main
git push origin "v1.2.$n"
```

The draft waits for CI on the tagged commit. Every push to main gets its own run, so merging more pull requests
straight after the release commit doesn't hold the release up.

Before tagging, move the `## Unreleased` bullets into a "New in MAJOR.MINOR.PATCH" block at the top of that
MAJOR.MINOR's section of `CHANGELOG.md`: the release notes are that section.

`Directory.Build.props` keeps `MAJOR.MINOR.0` for local builds.

## README screenshots

`docs\images\library.png` and `docs\images\reader.png` show public-domain books, never a real library. To take them
again, fetch the books (the first time), then run a Release build with an empty data root:

```powershell
.\tools\Screenshots\get-books.ps1 -Folder $env:TEMP\bibliotaph-demo\books
.\src\Bibliotaph.App\bin\Release\net11.0-windows\Bibliotaph.exe --screenshots docs\images `
    --screenshot-files $env:TEMP\bibliotaph-demo\books --data-root $env:TEMP\bibliotaph-demo\data `
    --screenshot-open "Treasure Island|1"
```

It indexes the books, then saves the Library and the named book open at that PDF page, in the light theme at 1440 by
900. Run it again on the same data root to retake them without indexing again.

## Contributing

Issues and ideas are welcome: [open an issue](https://github.com/munkycdev/bibliotaph/issues). Pull requests aren't
being taken yet, so start with an issue.

- Never attach or commit a file cut from a purchased book, or a page or text from one.
- The repository is public, so examples, tests and issues use invented book titles and paths.

Security problems go through private vulnerability reporting; see [SECURITY.md](SECURITY.md).

## Licence

Bibliotaph is `GPL-3.0-or-later` ([LICENSE](LICENSE)), with one additional permission under section 7 so the app
can ship Microsoft's Windows SDK projection, which it uses for Windows' text recognition
([LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md)). Fonts, icons and packages keep their own licences
([THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

Any contribution is made under the same licence.
