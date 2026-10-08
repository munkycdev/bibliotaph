namespace Bibliotaph.Pdf.Contracts;

public enum Op
{
    Ping,
    Open,
    Close,
    Render,
    ExtractText,

    /// <summary>Text of a run of pages in one round trip, for indexing. No character boxes.</summary>
    ExtractPages,
    Find,

    /// <summary>Renders a page at <see cref="Request.Scale"/> and reads it with the worker's OCR engine.</summary>
    Ocr,

    // Fault injection for the isolation tests. Only Debug (test) builds of the worker handle these;
    // a Release worker answers BadRequest.
    Crash,
    Hang,
    AllocateUntilKilled,
    SpawnChild,
}

public enum ErrorKind
{
    None,
    Unknown,
    File,
    Format,
    Password,
    Security,
    Page,
    TooLarge,
    BadRequest,
    Internal,

    /// <summary>The worker has no OCR engine it can use, such as Windows OCR with no language installed.</summary>
    OcrUnavailable,
}

/// <summary>One request to the worker. Fields not used by an <see cref="Op"/> are ignored.</summary>
public sealed record Request
{
    public int Id { get; init; }
    public Op Op { get; init; }
    public string? Path { get; init; }

    /// <summary>Held in memory only; never logged or written to results.</summary>
    public string? Password { get; init; }

    public int DocId { get; init; }

    /// <summary>Zero-based PDF page index. For <see cref="Op.Find"/>, -1 searches every page.</summary>
    public int PageIndex { get; init; }

    /// <summary>For <see cref="Op.ExtractPages"/>: how many pages from <see cref="PageIndex"/>; clipped at the last page.</summary>
    public int PageCount { get; init; } = 1;

    /// <summary>Output pixels per PDF point.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>When set, render only this pixel rectangle of the page at <see cref="Scale"/>.</summary>
    public PixelRect? Tile { get; init; }

    public bool IncludeCharBoxes { get; init; }
    public string? Query { get; init; }
    public int MaxHits { get; init; } = 500;
}

public sealed record PixelRect(int X, int Y, int Width, int Height);

/// <summary>A rectangle in PDF user space: points, origin bottom-left, so Top &gt; Bottom.</summary>
public sealed record PdfRect(double Left, double Top, double Right, double Bottom);

public sealed record PageSize(float Width, float Height);

public sealed record Response
{
    public int Id { get; init; }
    public bool Ok { get; init; }
    public ErrorKind Error { get; init; }
    public string? Message { get; init; }

    public DocInfo? Doc { get; init; }
    public RenderInfo? Render { get; init; }
    public TextInfo? Text { get; init; }
    public List<PageText>? Pages { get; init; }
    public OcrInfo? Ocr { get; init; }
    public List<SearchHit>? Hits { get; init; }

    /// <summary>Time spent inside the worker handling the request.</summary>
    public double WorkerMs { get; init; }
    public long WorkerWorkingSetBytes { get; init; }

    public static Response Success(int id) => new() { Id = id, Ok = true };
    public static Response Fail(int id, ErrorKind error, string message) =>
        new() { Id = id, Ok = false, Error = error, Message = message };
}

public sealed record DocInfo
{
    public int DocId { get; init; }
    public int PageCount { get; init; }

    /// <summary>Raw PDF permission flags; 0xFFFFFFFF when the file is not encrypted.</summary>
    public ulong Permissions { get; init; }

    /// <summary>-1 when the file has no security handler.</summary>
    public int SecurityRevision { get; init; }
    public int FileVersion { get; init; }

    /// <summary>Printed page labels by PDF page index; null where the file defines none.</summary>
    public List<string?> PageLabels { get; init; } = [];
    public List<PageSize> PageSizes { get; init; } = [];

    /// <summary>The document information dictionary; values are null where the file leaves them out.</summary>
    public DocMetadata Metadata { get; init; } = new();

    /// <summary>Bookmarks in reading order, flattened with their depth; capped at <see cref="DocInfo.MaxOutlineItems"/>.</summary>
    public List<OutlineItem> Outline { get; init; } = [];

    public const int MaxOutlineItems = 5000;

    public bool IsEncrypted => SecurityRevision >= 0;
    public bool CanPrint => (Permissions & 0x4) != 0;
    public bool CanCopy => (Permissions & 0x10) != 0;
    public bool CanExtractForAccessibility => (Permissions & 0x200) != 0;
}

/// <summary>The rendered bitmap is BGRA, top-down, at offset 0 of the shared buffer.</summary>
public sealed record RenderInfo(int Width, int Height, int Stride);

public sealed record TextInfo
{
    public int CharCount { get; init; }
    public string Text { get; init; } = "";

    /// <summary>One box per character when requested.</summary>
    public List<PdfRect>? CharBoxes { get; init; }
}

public sealed record DocMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
    public string? Producer { get; init; }
}

/// <summary>One bookmark. <see cref="PageIndex"/> is -1 when it points nowhere in this file (a link, a script).</summary>
public sealed record OutlineItem(string Title, int PageIndex, int Depth);

/// <summary>
/// One page's extracted text. <see cref="UnmappedChars"/> counts characters PDFium could not map to Unicode,
/// a sign of a broken text layer that OCR should replace. <see cref="Error"/> is set when the page would not load.
/// </summary>
public sealed record PageText(int PageIndex, string Text, int CharCount, int UnmappedChars, string? Error = null);

/// <summary>An OCR result: the text in reading order, one line per line, and each word's box in PDF points.</summary>
public sealed record OcrInfo(string Engine, string Text, List<OcrWord> Words);

public sealed record OcrWord(string Text, PdfRect Box);

public sealed record SearchHit(int PageIndex, int CharIndex, int CharCount, List<PdfRect> Rects);
