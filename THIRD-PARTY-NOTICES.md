# Third-party notices

Bibliotaph is licensed under the GNU General Public License v3.0 or later (see `LICENSE`). It includes or depends
on the components below, which keep their own licences. All of them are permissive (MIT, Apache-2.0, BSD, ISC,
FreeType, public domain), and all of them can be combined with GPL-3.0 code. Apache-2.0 is compatible with GPL
version 3, not version 2, which is one reason the project is GPL-3.0 rather than GPL-2.0.

The fonts stay under the SIL Open Font License. The OFL allows bundling them with software under any licence; they
are not relicensed under the GPL.

## Included in this repository

| Component | Where | Licence | Notice |
| --- | --- | --- | --- |
| DM Sans (Regular, Medium, SemiBold) | `src/Bibliotaph.App/Fonts/` | SIL Open Font License 1.1 | `src/Bibliotaph.App/Fonts/OFL-DMSans.txt` |
| Libre Caslon Display | `src/Bibliotaph.App/Fonts/` | SIL Open Font License 1.1 | `src/Bibliotaph.App/Fonts/OFL-LibreCaslonDisplay.txt` |
| Lucide icons (SVG sources and the XAML geometry generated from them) | `tools/IconGen/lucide/`, `src/Bibliotaph.App/Icons/Icons.xaml` | ISC; icons derived from Feather are MIT | `tools/IconGen/lucide/LICENSE`, `src/Bibliotaph.App/Icons/LICENSE-lucide.txt` |
| DM Sans, Libre Caslon Display and Lucide, embedded | `docs/bibliotaph-ui-mockups.html` | As above | As above; the embedded Lucide bundle keeps its own licence header |

The fonts are unmodified and are not sold on their own. The app icon (`src/Bibliotaph.App/Assets/`) is artwork
drawn from Libre Caslon Display glyph outlines by `tools/AppIcon/make_icon.py`; the OFL does not restrict artwork
made with the font.

## Shipped with the app (NuGet packages)

| Package | Licence | Project |
| --- | --- | --- |
| PDFium native binaries (`bblanchon.PDFium.Win32`) | Apache-2.0 for the packaging; PDFium itself is BSD-3-Clause and bundles third-party libraries under their own permissive licences (see below) | https://github.com/bblanchon/pdfium-binaries |
| PDFiumCore | Apache-2.0 | https://github.com/Dtronix/PDFiumCore |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Dapper | Apache-2.0 | https://github.com/DapperLib/Dapper |
| VirtualizingWrapPanel | MIT | https://github.com/sbaeumlisberger/VirtualizingWrapPanel |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 | https://github.com/serilog |
| Microsoft.Extensions.*, Microsoft.EntityFrameworkCore.*, Microsoft.Data.Sqlite | MIT | https://github.com/dotnet |
| SQLitePCLRaw (via Microsoft.Data.Sqlite) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain | https://sqlite.org |

**PDFium's bundled libraries.** The PDFium binary includes code from libraries such as FreeType, libjpeg-turbo,
libpng, zlib, OpenJPEG, Little CMS and Abseil. Each pdfium-binaries release ships a `LICENSE` file with all of
their notices. FreeType is used under the FreeType License, which asks for this credit in the documentation:
*Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved.*

**Before the first release:** the installer must carry the full licence texts for everything in this section:
the pdfium-binaries `LICENSE` for the exact PDFium version, the Apache-2.0 and MIT texts with each package's
copyright line, any `NOTICE` file an Apache-2.0 package ships, and the two OFL files, in a `licenses\` folder,
with Settings > About linking to them. Planned for slice 4 (see "Licences" in `docs/architecture.md`).

## Build, test and tooling only (not shipped)

| Component | Licence |
| --- | --- |
| xunit.v3 | Apache-2.0 |
| PDFsharp (generates synthetic test PDFs) | MIT |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | MIT |
| Pillow and fontTools (`tools/AppIcon`) | MIT-CMU (HPND) and MIT |
| Tesseract .NET wrapper, with Tesseract 5 and Leptonica native binaries (`tools/OcrBakeoff`); its English models are downloaded at run time | Apache-2.0; Leptonica BSD-2-Clause; tessdata Apache-2.0 |
| GitHub Actions: checkout, setup-dotnet, cache, upload-artifact | MIT |

Trademarks and product names mentioned in the docs and spike results (game systems, publishers and book titles)
belong to their owners. No book content is included in this repository.
