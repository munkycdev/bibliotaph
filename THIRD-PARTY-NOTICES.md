# Third-party notices

Bibliotaph is licensed under the GNU General Public License v3.0 or later (see `LICENSE`). It includes or depends
on the components below, which keep their own licences. All of them are permissive (MIT, Apache-2.0, BSD, ISC,
FreeType, public domain), and all of them can be combined with GPL-3.0 code, apart from the Windows SDK projection,
which Bibliotaph may ship under the additional permission in `LICENSE-EXCEPTION.md` (see the table). Apache-2.0 is compatible with GPL version 3, not version 2, which is
one reason the project is GPL-3.0 rather than GPL-2.0.

The fonts stay under the SIL Open Font License. The OFL allows bundling them with software under any licence; they
are not relicensed under the GPL.

## Included in this repository

| Component | Where | Licence | Notice |
| --- | --- | --- | --- |
| DM Sans (Regular, Medium, SemiBold) | `src/Bibliotaph.App/Fonts/` | SIL Open Font License 1.1 | `src/Bibliotaph.App/Fonts/OFL-DMSans.txt` |
| Libre Caslon Display | `src/Bibliotaph.App/Fonts/` | SIL Open Font License 1.1 | `src/Bibliotaph.App/Fonts/OFL-LibreCaslonDisplay.txt` |
| Lucide icons (SVG sources and the XAML geometry generated from them) | `tools/IconGen/lucide/`, `src/Bibliotaph.App/Icons/Icons.xaml` | ISC; icons derived from Feather are MIT | `tools/IconGen/lucide/LICENSE`, `src/Bibliotaph.App/Icons/LICENSE-lucide.txt` |
| DM Sans, Libre Caslon Display and Lucide, embedded | `docs/bibliotaph-ui-mockups.html` | As above | As above; the embedded Lucide bundle keeps its own licence header |
| PDF.js (Mozilla), with its viewer, fonts, character maps and colour profiles; its worker carries a one-line change, made by `tools/PdfJs/vendor.mjs` | `src/Bibliotaph.Web/wwwroot/lib/pdfjs/` | Apache-2.0; its standard fonts and colour profiles keep their own permissive licences | `src/Bibliotaph.Web/wwwroot/lib/pdfjs/LICENSE` |

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
| AngleSharp (reads e-books' HTML) | MIT | https://github.com/AngleSharp/AngleSharp |
| VirtualizingWrapPanel | MIT | https://github.com/sbaeumlisberger/VirtualizingWrapPanel |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 | https://github.com/serilog |
| Microsoft.Extensions.*, Microsoft.EntityFrameworkCore.*, Microsoft.Data.Sqlite | MIT | https://github.com/dotnet |
| SQLitePCLRaw (via Microsoft.Data.Sqlite) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain | https://sqlite.org |
| Velopack (installer and updates) | MIT | https://github.com/velopack/velopack |
| Microsoft.Web.WebView2 (the in-app browser's control and `WebView2Loader.dll`; the browser itself is the Edge WebView2 Runtime that comes with Windows, not shipped) | BSD-3-Clause | https://aka.ms/webview |
| llama.cpp and ggml: `llama-server.exe` and its libraries in `aiworker\`, the built-in AI's engine (Vulkan build, downloaded and checked by `build/fetch-engine.ps1`; not a NuGet package) | MIT (`licenses/LICENSE-llama.cpp.txt`) | https://github.com/ggml-org/llama.cpp |
| LLVM OpenMP runtime (`libomp.dll` in `aiworker\`, which comes with llama.cpp's Windows build) | Apache-2.0 WITH LLVM-exception (`licenses/LICENSE-LLVM-OpenMP.txt`) | https://github.com/llvm/llvm-project |
| .NET and Windows Desktop runtimes (the installed app is self-contained) | MIT | https://github.com/dotnet |
| Windows SDK projection for .NET (`Microsoft.Windows.SDK.NET.dll`, with C#/WinRT's `WinRT.Runtime.dll`, MIT), used by the PDF worker for Windows' text recognition | Windows SDK licence terms; shipped under the GPL section 7 additional permission in `LICENSE-EXCEPTION.md` | https://aka.ms/WinSDKProjectURL |

**PDFium's bundled libraries.** The PDFium binary includes code from libraries such as FreeType, libjpeg-turbo,
libpng, zlib, OpenJPEG, Little CMS and Abseil. Each pdfium-binaries release ships a `LICENSE` file with all of
their notices. FreeType is used under the FreeType License, which asks for this credit in the documentation:
*Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved.*

**Full texts.** The app carries every text above in its `licenses\` folder, which the About popup reads:
`LICENSE.txt`, this file, the fonts' and Lucide's licences, and `packages\<id>.txt` for each package that ships,
written by `tools/LicenseNotices` from the package's own licence and notice files (or the standard text for its SPDX
id, kept in `tools/LicenseNotices/texts/`), plus `packages\pdfium.txt` with the pdfium-binaries notices for the
pinned PDFium build. The tool fails the build when a shipped package's licence is unknown or not GPL-3.0-compatible.

## The web version (bibliotaph.online)

What the web server runs, and what it sends to the browser. The browser gets the Blazor WebAssembly client, PDF.js,
the fonts and the icons; everything else runs only on the server.

| Component | Runs in | Licence | Project |
| --- | --- | --- | --- |
| .NET runtime, ASP.NET Core and Blazor WebAssembly | Server and browser | MIT | https://github.com/dotnet |
| Microsoft.AspNetCore.Authentication.OpenIdConnect, Microsoft.EntityFrameworkCore.Sqlite, Microsoft.Data.Sqlite | Server | MIT | https://github.com/dotnet |
| SQLitePCLRaw and SQLite | Server | Apache-2.0; SQLite is public domain | https://github.com/ericsink/SQLitePCL.raw |
| Azure.Identity, Azure.Storage.Blobs, Azure.Extensions.AspNetCore.DataProtection.Blobs and .Keys | Server | MIT | https://github.com/Azure/azure-sdk-for-net |
| SkiaSharp, with its native library for Linux (Skia) | Server | MIT; Skia BSD-3-Clause | https://github.com/mono/SkiaSharp |
| PDFiumCore, with PDFium's Linux build and its bundled libraries (as above) | Server | Apache-2.0; PDFium BSD-3-Clause | https://github.com/Dtronix/PDFiumCore |
| Dapper | Server | Apache-2.0 | https://github.com/DapperLib/Dapper |
| AngleSharp (reads e-books' HTML) | Server and browser | MIT | https://github.com/AngleSharp/AngleSharp |
| Tesseract OCR and its English data, from Debian's packages | Server | Apache-2.0 | https://github.com/tesseract-ocr/tesseract |
| Litestream (replicates the libraries' databases to Azure storage) | Server | Apache-2.0 | https://github.com/benbjohnson/litestream |
| Debian and the .NET container images the server is built on | Server | Each package's own free licence | https://github.com/dotnet/dotnet-docker |
| PDF.js (Mozilla) | Browser | Apache-2.0 | https://github.com/mozilla/pdf.js |
| DM Sans and Libre Caslon Display | Browser | SIL Open Font License 1.1 | https://github.com/googlefonts/dm-fonts, https://github.com/impallari/Libre-Caslon |
| Lucide icons | Browser | ISC; icons derived from Feather are MIT | https://github.com/lucide-icons/lucide |

## Book data from online catalogues

Used only when **Look up books online by their ISBN** is on. Each catalogue is sent a book's ISBNs and nothing else,
and each suggestion it gives names it as its source ("Open Library's record for ISBN …").

| Catalogue | Asked by | Terms of its data | Site |
| --- | --- | --- | --- |
| Open Library, run by the Internet Archive | The Windows app and the web server | Public domain (CC0) | https://openlibrary.org/developers/licensing |
| Google Books | The web server, for a book Open Library has no record of | Google APIs Terms of Service | https://developers.google.com/books/terms |

## Build, test and tooling only (not shipped)

| Component | Licence |
| --- | --- |
| xunit.v3 | Apache-2.0 |
| PDFsharp (generates synthetic test PDFs) | MIT |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | MIT |
| Pillow and fontTools (`tools/AppIcon`) | MIT-CMU (HPND) and MIT |
| Tesseract .NET wrapper, with Tesseract 5 and Leptonica native binaries (`tools/OcrBakeoff`); its English models are downloaded at run time | Apache-2.0; Leptonica BSD-2-Clause; tessdata Apache-2.0 |
| vpk, Velopack's packaging tool (the release workflow) | MIT |
| GitHub Actions: checkout, setup-dotnet, cache, upload-artifact | MIT |

Trademarks and product names mentioned in the docs and spike results (game systems, publishers and book titles)
belong to their owners. No book content is included in this repository.
