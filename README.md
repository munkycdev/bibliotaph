# Bibliotaph

A Windows desktop library for a game master's existing RPG files: it indexes PDFs, maps and handouts where they already are, proposes metadata, and finds the right book and the right page. It never moves or edits the original files.

Bibliotaph is in early development and not yet usable as a library. Nothing is released.

- [docs/product-spec.md](docs/product-spec.md) is the product spec (v0.3).
- [docs/architecture.md](docs/architecture.md) is the agreed design. Change it by pull request.
- [docs/bibliotaph-ui-mockups.html](docs/bibliotaph-ui-mockups.html) is the visual target.

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

`--smoke-test` opens the window, visits every screen in light and dark, and exits with 0 or 1. CI runs it; it does not replace looking at the app.

## Layout

| Folder | What |
| --- | --- |
| `src/Bibliotaph.Core` | Domain types and policies. References nothing, does no I/O. |
| `src/Bibliotaph.Catalog` | `catalog.db` (the user's work): EF Core, migrations, backup before migrating. |
| `src/Bibliotaph.Index` | `index.db` (derived, rebuildable): plain SQL, FTS5, the single writer. |
| `src/Bibliotaph.Processing` | Scanner, job queue and pipeline (slice 1). The only reader of source files. |
| `src/Bibliotaph.Classification` | Rule hints and AI adapters (slice 2). |
| `src/Bibliotaph.Pdf.Contracts` | Messages between the app and the PDF worker. References nothing. |
| `src/Bibliotaph.Pdf.Host` | Worker pool, job objects, restarts, poison-page tracking. |
| `src/Bibliotaph.PdfWorker` | The only process that runs PDFium. Ships in the app's `pdfworker\` folder. |
| `src/Bibliotaph.Viewer` | The page viewer (slice 1). |
| `src/Bibliotaph.App` | The WPF shell, theme, fonts, icons and composition root. |
| `tests/` | xUnit v3 on Microsoft Testing Platform. PDF tests build synthetic PDFs at run time. |
| `tools/IconGen` | Turns the Lucide SVGs it holds into `src/Bibliotaph.App/Icons/Icons.xaml`. |
| `tools/AppIcon` | Draws the app icon (`src/Bibliotaph.App/Assets/Bibliotaph.ico` and `.svg`) from the mockup's monogram. |
| `spikes/pdf-feasibility` | The PDF spike, kept for reference. Its own solution; not built by CI. |

## Rules the build enforces

- Warnings are errors, with the `latest-recommended` analyzers.
- Write-capable file APIs (`FileStream`, `File.Write*`, `File.Delete` and friends) are banned by `BannedSymbols.txt` everywhere except Processing, Pdf.Host, the PDF worker, tests and tools. CI proves it by adding `File.WriteAllText` to Core and expecting `RS0030`.
- Package versions live only in `Directory.Packages.props`.
- No fixture cut from a purchased book is ever committed. Test PDFs are generated with PDFsharp when the tests run.

## Common tasks

Add a catalog migration (needs `dotnet tool install --global dotnet-ef`):

```powershell
dotnet ef migrations add <Name> --project src\Bibliotaph.Catalog --output-dir Migrations
```

The app copies `catalog.db` into `backups\` before applying a pending migration. `index.db` has no migrations: change `src\Bibliotaph.Index\Schema\index.sql`, bump `IndexSchema.Version` and the `user_version` at the end of the script, and the index is rebuilt on next start.

Add an icon: copy its SVG from the same `lucide-static` version into `tools\IconGen\lucide\`, then:

```powershell
dotnet run --project tools\IconGen
```

Redraw the app icon after changing the monogram (needs Python with `pip install pillow fonttools`):

```powershell
python tools\AppIcon\make_icon.py
```

## To do when .NET 11 ships (November 2026)

- Raise `global.json` to the GA SDK, set `DotNetPackagesVersion` in `Directory.Packages.props` to the GA packages, and change `dotnet-quality` in `.github/workflows/ci.yml` to `ga`.

## Contributing

Issues and ideas are welcome. Pull requests are not being taken yet while the architecture settles; open an issue
first. Never attach or commit a file cut from a purchased book: tests build their own PDFs, and the repo ignores
`*.pdf` for that reason.

Security problems go through private vulnerability reporting; see [SECURITY.md](SECURITY.md).

## Licence

Copyright (C) 2026 munkycdev.

Bibliotaph is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; see [LICENSE](LICENSE) for
details. SPDX: `GPL-3.0-or-later`.

Fonts, icons and packages keep their own licences; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Any contribution is made under the same licence.
