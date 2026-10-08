namespace Bibliotaph.Spike.Contracts;

public enum Op
{
    Ping,
    Open,
    Close,
    Render,
    ExtractText,
    Find,

    // Isolation self-tests: make the worker misbehave on purpose.
    Crash,
    Hang,
    AllocateUntilKilled,
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

public sealed record SearchHit(int PageIndex, int CharIndex, int CharCount, List<PdfRect> Rects);
