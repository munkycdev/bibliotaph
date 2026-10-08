# OCR bake-off

Decides which OCR engine Bibliotaph ships (architecture decision 6): Windows.Media.Ocr, or Tesseract 5 with the
`tessdata_fast` or `tessdata_best` English model. Every engine reads the same pages, rendered at 300 dpi by the real
PDF worker.

Windows only. Run it against your own books; it writes its report to a new folder under `%TEMP%` and changes nothing
else. Keep the report out of the repository: it quotes the books.

```powershell
dotnet run --project tools\OcrBakeoff -c Release -- `
  --scanned "<scanned book 1>.pdf" `
  --scanned "<scanned book 2>.pdf" `
  --digital "<a digital book with a good text layer>.pdf"
```

On first run it downloads Tesseract's two English models (about 20 MB) to `%LOCALAPPDATA%\Bibliotaph\tools\tessdata`.
Tesseract needs the Microsoft Visual C++ 2015-2022 redistributable (x64); if it won't load, the tool says so and
carries on with Windows OCR.

## Reading the result

- **Scanned books** have no true text to compare with, so each engine is scored by the share of its recognised words
  (three letters or more) that appear anywhere in the digital books' text layers. Higher is better; read it as a
  comparison between engines, not as an accuracy.
- **Digital books** are OCR'd too and scored by the share of the text layer's words each engine recovered: a real
  accuracy, on clean print.
- `report.html` shows every sampled page beside each engine's text, which is the part worth reading.
- `summary.csv` and `pages.csv` hold the numbers.

Tesseract earns its place in the app only if it clearly wins: it adds native binaries and language data to the
installer, where Windows OCR ships with Windows.
