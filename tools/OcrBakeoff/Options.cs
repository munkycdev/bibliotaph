using System.Globalization;

namespace OcrBakeoff;

sealed record Options
{
    public const string Usage = """
        Usage: OcrBakeoff --scanned <pdf> [--scanned <pdf> ...] [--digital <pdf> ...]
                          [--pages 20] [--dpi 300] [--out <folder>] [--tessdata <folder>]

          --scanned   A scanned book. Engines are compared on speed and on how many recognised words are real words.
          --digital   A book with a text layer. Its pages are OCR'd too and compared with that text, and its words
                      become the vocabulary for judging the scanned books.
          --pages     Pages sampled from each book, spread evenly (default 20).
          --dpi       Render resolution for OCR (default 300).
          --out       Where to write the report (default: a new folder under %TEMP%).
          --tessdata  Where Tesseract's English models are kept; downloaded there on first run
                      (default: %LOCALAPPDATA%\Bibliotaph\tools\tessdata).
        """;

    public List<string> Scanned { get; } = [];
    public List<string> Digital { get; } = [];
    public int Pages { get; private set; } = 20;
    public int Dpi { get; private set; } = 300;
    public string Output { get; private set; } = Path.Combine(Path.GetTempPath(),
        "bibliotaph-ocr-bakeoff-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    public string TessData { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bibliotaph", "tools", "tessdata");

    public static Options? Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            if (value is null) return null;
            switch (args[i])
            {
                case "--scanned": options.Scanned.Add(Path.GetFullPath(value)); break;
                case "--digital": options.Digital.Add(Path.GetFullPath(value)); break;
                case "--pages": options.Pages = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--dpi": options.Dpi = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--out": options.Output = Path.GetFullPath(value); break;
                case "--tessdata": options.TessData = Path.GetFullPath(value); break;
                default: return null;
            }
            i++;
        }
        return options.Scanned.Count + options.Digital.Count == 0 || options.Pages < 1 || options.Dpi is < 72 or > 600 ? null : options;
    }
}
