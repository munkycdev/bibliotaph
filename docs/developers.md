# For developers

[Bibliotaph](../README.md) › For developers

Bibliotaph is a WPF app on .NET 11. PDFium runs in a small pool of isolated worker processes, user work lives in one
SQLite file (`catalog.db`, EF Core with migrations) and a rebuildable search index in another (`index.db`, plain SQL
with FTS5), and an in-process durable job queue does the reading.

- [architecture.md](architecture.md) is the agreed design. Change it by pull request.
- [product-spec.md](product-spec.md) is the product spec (v0.3).
- [bibliotaph-ui-mockups.html](bibliotaph-ui-mockups.html) is the visual target.
- The user guide starts at the [README](../README.md) and lives in [guide/](guide/).

## Build and run

Needs the .NET 11 SDK (release candidate until GA in November 2026) on Windows x64.

```powershell
dotnet build Bibliotaph.slnx
dotnet test --solution Bibliotaph.slnx
dotnet run --project src\Bibliotaph.App
```

The app keeps its databases and logs in `%LOCALAPPDATA%\Bibliotaph`. To try it against a throwaway folder instead:

```powershell
dotnet run --project src\Bibliotaph.App -- --data-root $env:TEMP\bibliotaph-scratch
```

`--smoke-test` opens the window, visits every screen in light and dark, and exits with 0 or 1. CI runs it; it does not
replace looking at the app.

Before pushing, run the same checks as CI (in PowerShell 7):

```powershell
./build/verify.ps1             # restore, engine, build, licences, test, smoke test, banned-API probe, Release build
./build/verify.ps1 -SkipSmoke  # the same without opening the app's window
./build/verify.ps1 -Step Build, Test
```

CI runs each step of this script, so a green run here should mean a green run there. Off Windows the Engine and Smoke
steps are skipped.

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
| `tools/IconGen` | Turns the Lucide SVGs it holds into `src/Bibliotaph.App/Icons/Icons.xaml`. |
| `tools/AppIcon` | Draws the app icon (`src/Bibliotaph.App/Assets/Bibliotaph.ico` and `.svg`) from the mockup's monogram. |
| `tools/LicenseNotices` | Writes the licence file of every shipped package into `licenses\packages` (verify's Licenses step). |
| `spikes/pdf-feasibility` | The PDF spike, kept for reference. Its own solution; not built by CI. |

## Rules the build enforces

- Warnings are errors, with the `latest-recommended` analyzers.
- Write-capable file APIs (`FileStream`, `File.Write*`, `File.Delete` and friends) are banned by `BannedSymbols.txt`
  everywhere except Processing, Pdf.Host, the PDF worker, tests and tools. CI proves it by adding `File.WriteAllText`
  to Core and expecting `RS0030`.
- Package versions live only in `Directory.Packages.props`.
- No fixture cut from a purchased book is ever committed. Test PDFs are generated with PDFsharp when the tests run.

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

## Releases

A version tag `vMAJOR.MINOR.PATCH` (such as `v1.0.0`) runs `.github/workflows/release.yml`: every CI check,
a self-contained win-x64 publish, Velopack's pack, and a draft GitHub Release holding `BibliotaphApp-win-Setup.exe`
and the update files. Nothing is public until someone presses Publish on the draft. Installed copies update from
published releases only. See "Installer", "Releases" and "Updates" in [architecture.md](architecture.md).

## To do when .NET 11 ships (November 2026)

- Raise `global.json` to the GA SDK, set `DotNetPackagesVersion` in `Directory.Packages.props` to the GA packages, and
  change `dotnet-quality` in `.github/workflows/ci.yml` and `.github/workflows/release.yml` to `ga`.

## Contributing

Issues and ideas are welcome. Pull requests are not being taken yet while the architecture settles; open an issue
first. Never attach or commit a file cut from a purchased book: tests build their own PDFs, and the repo ignores
`*.pdf` for that reason. The repository is public, so examples use invented book titles and paths.

Security problems go through private vulnerability reporting; see [SECURITY.md](../SECURITY.md).

## Licence

Bibliotaph is `GPL-3.0-or-later` ([LICENSE](../LICENSE)), with one additional permission under section 7 so the app
can ship Microsoft's Windows SDK projection, which it uses for Windows' text recognition
([LICENSE-EXCEPTION.md](../LICENSE-EXCEPTION.md)). Fonts, icons and packages keep their own licences
([THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)). A new package must be permissive or GPL-3.0-compatible; verify's
Licenses step fails on any other.

Any contribution is made under the same licence.
