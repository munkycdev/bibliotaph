using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;

namespace OcrBakeoff;

/// <summary>A rendered page: BGRA, top-down, no padding beyond <see cref="Stride"/>.</summary>
sealed record PageImage(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>One open PDF in the worker.</summary>
sealed class Book
{
    readonly WorkerClient _worker;
    readonly DocInfo _doc;

    Book(WorkerClient worker, DocInfo doc)
    {
        _worker = worker;
        _doc = doc;
    }

    public int PageCount => _doc.PageCount;

    public static async Task<Book> OpenAsync(WorkerClient worker, string path)
    {
        var response = await worker.SendAsync(new Request { Op = Op.Open, Path = path });
        if (!response.Ok) throw new InvalidOperationException($"{Path.GetFileName(path)} would not open: {response.Error} {response.Message}");
        return new Book(worker, response.Doc!);
    }

    public static async Task<List<PageText>> AllTextAsync(WorkerClient worker, string path)
    {
        var book = await OpenAsync(worker, path);
        var pages = await book.AllPagesAsync();
        await book.CloseAsync();
        return pages;
    }

    async Task<List<PageText>> AllPagesAsync()
    {
        var pages = new List<PageText>(PageCount);
        for (var first = 0; first < PageCount; first += 32)
        {
            var response = await _worker.SendAsync(new Request { Op = Op.ExtractPages, DocId = _doc.DocId, PageIndex = first, PageCount = 32 });
            if (response.Ok) pages.AddRange(response.Pages!);
        }
        return pages;
    }

    /// <summary>
    /// <paramref name="count"/> pages spread evenly, skipping the covers. With <paramref name="needText"/>,
    /// only pages with a real text layer (200 characters or more) qualify, so there is something to compare with.
    /// </summary>
    public async Task<List<int>> SamplePagesAsync(int count, bool needText)
    {
        var candidates = Enumerable.Range(0, PageCount).Where(p => PageCount <= 4 || (p >= 2 && p < PageCount - 1)).ToList();
        if (needText)
        {
            var withText = (await AllPagesAsync()).Where(p => p.CharCount >= 200).Select(p => p.PageIndex);
            candidates = [.. candidates.Intersect(withText)];
        }
        if (candidates.Count <= count) return candidates;
        return [.. Enumerable.Range(0, count).Select(i => candidates[(int)((long)i * candidates.Count / count)])];
    }

    /// <summary>Renders at <paramref name="dpi"/>, or smaller if that would exceed <paramref name="maxDimension"/> pixels.</summary>
    public async Task<PageImage> RenderAsync(int pageIndex, int dpi, int maxDimension)
    {
        var size = _doc.PageSizes[pageIndex];
        var scale = dpi / 72.0;
        var longest = Math.Max(size.Width, size.Height) * scale;
        if (longest > maxDimension) scale *= (maxDimension - 1) / longest;

        var (response, pixels) = await _worker.RenderAsync(new Request { DocId = _doc.DocId, PageIndex = pageIndex, Scale = scale });
        if (!response.Ok) throw new InvalidOperationException($"Page {pageIndex + 1} did not render: {response.Message}");
        var info = response.Render!;
        return new PageImage(pixels!, info.Width, info.Height, info.Stride);
    }

    public async Task<string> TextAsync(int pageIndex)
    {
        var response = await _worker.SendAsync(new Request { Op = Op.ExtractText, DocId = _doc.DocId, PageIndex = pageIndex });
        return response.Ok ? response.Text!.Text : "";
    }

    public Task CloseAsync() => _worker.SendAsync(new Request { Op = Op.Close, DocId = _doc.DocId });
}
