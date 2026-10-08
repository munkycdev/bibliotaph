using System.Runtime.InteropServices;
using Bibliotaph.Pdf.Contracts;
using PDFiumCore;

namespace Bibliotaph.PdfWorker;

/// <summary>Thin, single-threaded wrapper over PDFium. PDFium is not thread-safe; never call it concurrently.</summary>
sealed class PdfEngine(IntPtr shared, long sharedSize)
{
    const int FormatBgra = 4;      // FPDFBitmap_BGRA
    const int RenderAnnotations = 0x01; // FPDF_ANNOT
    const uint White = 0xFFFFFFFF;

    readonly Dictionary<int, SourceDocument> _docs = [];
    int _nextDocId;

    public Response Handle(Request r) => r.Op switch
    {
        Op.Ping => Response.Success(r.Id),
        Op.Open => Open(r),
        Op.Close => Close(r),
        Op.Render => WithDoc(r, doc => Render(r, doc)),
        Op.ExtractText => WithDoc(r, doc => ExtractText(r, doc)),
        Op.ExtractPages => WithDoc(r, doc => ExtractPages(r, doc)),
        Op.Find => WithDoc(r, doc => Find(r, doc)),
#if BIBLIOTAPH_TEST_OPS
        Op.Crash => Crash(),
        Op.Hang => Hang(),
        Op.AllocateUntilKilled => AllocateUntilKilled(),
        Op.SpawnChild => SpawnChild(r.Id),
#endif
        _ => Response.Fail(r.Id, ErrorKind.BadRequest, $"Op {r.Op} is not available in this build."),
    };

    public void CloseAll()
    {
        foreach (var doc in _docs.Values) doc.Dispose();
        _docs.Clear();
    }

    Response Open(Request r)
    {
        if (string.IsNullOrEmpty(r.Path)) return Response.Fail(r.Id, ErrorKind.BadRequest, "Path is required.");

        var source = SourceDocument.Open(r.Path, r.Password, out var error);
        if (source is null) return Error(r.Id, error, "Open failed");
        var doc = source.Handle;

        var pageCount = fpdfview.FPDF_GetPageCount(doc);
        var sizes = new List<PageSize>(pageCount);
        var labels = new List<string?>(pageCount);
        using (var size = new FS_SIZEF_())
        {
            for (var i = 0; i < pageCount; i++)
            {
                sizes.Add(fpdfview.FPDF_GetPageSizeByIndexF(doc, i, size) != 0
                    ? new PageSize(size.Width, size.Height)
                    : new PageSize(0, 0));
                labels.Add(PageLabel(doc, i));
            }
        }
        var version = 0;
        fpdfview.FPDF_GetFileVersion(doc, ref version);

        var id = ++_nextDocId;
        _docs[id] = source;
        return Response.Success(r.Id) with
        {
            Doc = new DocInfo
            {
                DocId = id,
                PageCount = pageCount,
                Permissions = fpdfview.FPDF_GetDocPermissions(doc),
                SecurityRevision = fpdfview.FPDF_GetSecurityHandlerRevision(doc),
                FileVersion = version,
                PageLabels = labels,
                PageSizes = sizes,
                Metadata = new DocMetadata
                {
                    Title = MetaText(doc, "Title"),
                    Author = MetaText(doc, "Author"),
                    Subject = MetaText(doc, "Subject"),
                    Keywords = MetaText(doc, "Keywords"),
                    Creator = MetaText(doc, "Creator"),
                    Producer = MetaText(doc, "Producer"),
                },
                Outline = Outline(doc),
            },
        };
    }

    Response Close(Request r)
    {
        if (_docs.Remove(r.DocId, out var doc)) doc.Dispose();
        return Response.Success(r.Id);
    }

    Response WithDoc(Request r, Func<FpdfDocumentT, Response> action) =>
        _docs.TryGetValue(r.DocId, out var doc)
            ? action(doc.Handle)
            : Response.Fail(r.Id, ErrorKind.BadRequest, $"Document {r.DocId} is not open.");

    Response Render(Request r, FpdfDocumentT doc)
    {
        if (r.Scale is <= 0 or > 50) return Response.Fail(r.Id, ErrorKind.BadRequest, "Scale out of range.");

        var page = fpdfview.FPDF_LoadPage(doc, r.PageIndex);
        if (IsNull(page)) return LastError(r.Id, $"Page {r.PageIndex} failed to load", ErrorKind.Page);
        try
        {
            var fullWidth = (int)Math.Round(fpdfview.FPDF_GetPageWidthF(page) * r.Scale);
            var fullHeight = (int)Math.Round(fpdfview.FPDF_GetPageHeightF(page) * r.Scale);
            var tile = r.Tile ?? new PixelRect(0, 0, fullWidth, fullHeight);
            if (tile.Width <= 0 || tile.Height <= 0)
                return Response.Fail(r.Id, ErrorKind.BadRequest, "Empty render area.");

            var stride = tile.Width * 4;
            if ((long)stride * tile.Height > sharedSize)
                return Response.Fail(r.Id, ErrorKind.TooLarge,
                    $"{tile.Width}x{tile.Height} needs {(long)stride * tile.Height / 1048576} MB; shared buffer is {sharedSize / 1048576} MB.");

            // Render directly into shared memory; PDFium does not own or free this buffer.
            var bitmap = fpdfview.FPDFBitmapCreateEx(tile.Width, tile.Height, FormatBgra, shared, stride);
            if (IsNull(bitmap)) return Response.Fail(r.Id, ErrorKind.Internal, "Bitmap allocation failed.");
            try
            {
                fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, tile.Width, tile.Height, White);
                // A negative start offset shifts the page so only the tile's region lands in the bitmap.
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, -tile.X, -tile.Y, fullWidth, fullHeight, 0, RenderAnnotations);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }
            return Response.Success(r.Id) with { Render = new RenderInfo(tile.Width, tile.Height, stride) };
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    static Response ExtractText(Request r, FpdfDocumentT doc) => WithTextPage(r, doc, r.PageIndex, textPage =>
    {
        var count = fpdf_text.FPDFTextCountChars(textPage);
        var text = PageString(textPage, count);

        List<PdfRect>? boxes = null;
        if (r.IncludeCharBoxes)
        {
            boxes = [with(count)];
            for (var i = 0; i < count; i++)
            {
                double left = 0, right = 0, bottom = 0, top = 0;
                fpdf_text.FPDFTextGetCharBox(textPage, i, ref left, ref right, ref bottom, ref top);
                boxes.Add(new PdfRect(left, top, right, bottom));
            }
        }
        return Response.Success(r.Id) with { Text = new TextInfo { CharCount = count, Text = text, CharBoxes = boxes } };
    });

    static Response ExtractPages(Request r, FpdfDocumentT doc)
    {
        var pageCount = fpdfview.FPDF_GetPageCount(doc);
        if (r.PageIndex < 0 || r.PageIndex >= pageCount || r.PageCount < 1)
            return Response.Fail(r.Id, ErrorKind.BadRequest, $"Pages {r.PageIndex}+{r.PageCount} are outside 0..{pageCount - 1}.");

        var last = Math.Min(pageCount, r.PageIndex + r.PageCount);
        var pages = new List<PageText>(last - r.PageIndex);
        for (var p = r.PageIndex; p < last; p++)
        {
            PageText? page = null;
            var result = WithTextPage(r, doc, p, textPage =>
            {
                var count = fpdf_text.FPDFTextCountChars(textPage);
                var unmapped = 0;
                for (var i = 0; i < count; i++)
                    if (fpdf_text.FPDFTextHasUnicodeMapError(textPage, i) == 1) unmapped++;
                page = new PageText(p, PageString(textPage, count), count, unmapped);
                return Response.Success(r.Id);
            });
            // One unreadable page must not lose the rest of the run; the indexer records it and moves on.
            pages.Add(page ?? new PageText(p, "", 0, 0, result.Message ?? "Page failed to load."));
        }
        return Response.Success(r.Id) with { Pages = pages };
    }

    static Response Find(Request r, FpdfDocumentT doc)
    {
        if (string.IsNullOrWhiteSpace(r.Query)) return Response.Fail(r.Id, ErrorKind.BadRequest, "Query is required.");

        var query = new ushort[r.Query.Length + 1];
        for (var i = 0; i < r.Query.Length; i++) query[i] = r.Query[i];

        var hits = new List<SearchHit>();
        var first = r.PageIndex >= 0 ? r.PageIndex : 0;
        var last = r.PageIndex >= 0 ? r.PageIndex : fpdfview.FPDF_GetPageCount(doc) - 1;
        for (var p = first; p <= last && hits.Count < r.MaxHits; p++)
        {
            var result = WithTextPage(r, doc, p, textPage =>
            {
                var search = fpdf_text.FPDFTextFindStart(textPage, ref query[0], 0, 0);
                if (IsNull(search)) return Response.Success(r.Id);
                try
                {
                    while (hits.Count < r.MaxHits && fpdf_text.FPDFTextFindNext(search) != 0)
                    {
                        var start = fpdf_text.FPDFTextGetSchResultIndex(search);
                        var length = fpdf_text.FPDFTextGetSchCount(search);
                        var rects = new List<PdfRect>();
                        var rectCount = fpdf_text.FPDFTextCountRects(textPage, start, length);
                        for (var i = 0; i < rectCount; i++)
                        {
                            double left = 0, top = 0, right = 0, bottom = 0;
                            fpdf_text.FPDFTextGetRect(textPage, i, ref left, ref top, ref right, ref bottom);
                            rects.Add(new PdfRect(left, top, right, bottom));
                        }
                        hits.Add(new SearchHit(p, start, length, rects));
                    }
                }
                finally
                {
                    fpdf_text.FPDFTextFindClose(search);
                }
                return Response.Success(r.Id);
            });
            // A page that will not load should not stop a whole-book search.
            if (!result.Ok && r.PageIndex >= 0) return result;
        }
        return Response.Success(r.Id) with { Hits = hits };
    }

    static Response WithTextPage(Request r, FpdfDocumentT doc, int pageIndex, Func<FpdfTextpageT, Response> action)
    {
        var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
        if (IsNull(page)) return LastError(r.Id, $"Page {pageIndex} failed to load", ErrorKind.Page);
        try
        {
            var textPage = fpdf_text.FPDFTextLoadPage(page);
            if (IsNull(textPage)) return Response.Fail(r.Id, ErrorKind.Page, $"No text layer could be built for page {pageIndex}.");
            try
            {
                return action(textPage);
            }
            finally
            {
                fpdf_text.FPDFTextClosePage(textPage);
            }
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    static string PageString(FpdfTextpageT textPage, int count)
    {
        if (count <= 0) return "";
        var buffer = new ushort[count + 1];
        var written = fpdf_text.FPDFTextGetText(textPage, 0, count, ref buffer[0]);
        return Utf16(buffer, Math.Max(0, written - 1));
    }

    /// <summary>A document information entry, or null when it is missing or blank.</summary>
    static string? MetaText(FpdfDocumentT doc, string tag)
    {
        // Returns the size in bytes (UTF-16LE, including the terminator); 2 means empty.
        var bytes = fpdf_doc.FPDF_GetMetaText(doc, tag, IntPtr.Zero, 0);
        if (bytes is <= 2 or > 1 << 20) return null;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            fpdf_doc.FPDF_GetMetaText(doc, tag, buffer, bytes);
            var value = Marshal.PtrToStringUni(buffer, (int)(bytes / 2) - 1).Trim();
            return value.Length == 0 ? null : value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The bookmark tree, depth first. Bounded by <see cref="DocInfo.MaxOutlineItems"/> and a visited set,
    /// because a malformed file can make the tree loop.
    /// </summary>
    static List<OutlineItem> Outline(FpdfDocumentT doc)
    {
        var items = new List<OutlineItem>();
        var visited = new HashSet<IntPtr>();
        var stack = new Stack<(FpdfBookmarkT Bookmark, int Depth)>();
        Push(fpdf_doc.FPDFBookmarkGetFirstChild(doc, null), 0);
        while (stack.Count > 0 && items.Count < DocInfo.MaxOutlineItems)
        {
            var (bookmark, depth) = stack.Pop();
            items.Add(new OutlineItem(BookmarkTitle(bookmark), BookmarkPage(doc, bookmark), depth));
            // Siblings go under children on the stack, so children come out first: reading order.
            Push(fpdf_doc.FPDFBookmarkGetNextSibling(doc, bookmark), depth);
            if (depth < 32) Push(fpdf_doc.FPDFBookmarkGetFirstChild(doc, bookmark), depth + 1);
        }
        return items;

        void Push(FpdfBookmarkT? bookmark, int depth)
        {
            if (bookmark is null || bookmark.__Instance == IntPtr.Zero || !visited.Add(bookmark.__Instance)) return;
            stack.Push((bookmark, depth));
        }
    }

    static string BookmarkTitle(FpdfBookmarkT bookmark)
    {
        var bytes = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (bytes is <= 2 or > 1 << 16) return "";
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, bytes);
            return Marshal.PtrToStringUni(buffer, (int)(bytes / 2) - 1).Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static int BookmarkPage(FpdfDocumentT doc, FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(doc, bookmark);
        if (IsNull(dest))
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (IsNull(action)) return -1;
            dest = fpdf_doc.FPDFActionGetDest(doc, action);
            if (IsNull(dest)) return -1;
        }
        return fpdf_doc.FPDFDestGetDestPageIndex(doc, dest);
    }

    static string? PageLabel(FpdfDocumentT doc, int index)
    {
        // First call returns the size in bytes (UTF-16LE, including the terminator).
        var bytes = fpdf_doc.FPDF_GetPageLabel(doc, index, IntPtr.Zero, 0);
        if (bytes <= 2) return null;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            fpdf_doc.FPDF_GetPageLabel(doc, index, buffer, bytes);
            return Marshal.PtrToStringUni(buffer, (int)(bytes / 2) - 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static string Utf16(ushort[] buffer, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)buffer[i];
        return new string(chars);
    }

    static bool IsNull(object? handle) => handle switch
    {
        null => true,
        FpdfDocumentT d => d.__Instance == IntPtr.Zero,
        FpdfPageT p => p.__Instance == IntPtr.Zero,
        FpdfBitmapT b => b.__Instance == IntPtr.Zero,
        FpdfTextpageT t => t.__Instance == IntPtr.Zero,
        FpdfSchhandleT s => s.__Instance == IntPtr.Zero,
        FpdfDestT d => d.__Instance == IntPtr.Zero,
        FpdfActionT a => a.__Instance == IntPtr.Zero,
        _ => false,
    };

    static Response LastError(int id, string context, ErrorKind fallback = ErrorKind.Unknown) =>
        Error(id, (uint)fpdfview.FPDF_GetLastError(), context, fallback);

    static Response Error(int id, uint code, string context, ErrorKind fallback = ErrorKind.Unknown)
    {
        var kind = code switch
        {
            2 => ErrorKind.File,
            3 => ErrorKind.Format,
            4 => ErrorKind.Password,
            5 => ErrorKind.Security,
            6 => ErrorKind.Page,
            _ => fallback,
        };
        return Response.Fail(id, kind, $"{context} (PDFium error {code}).");
    }

#if BIBLIOTAPH_TEST_OPS
    static Response Crash()
    {
        Environment.FailFast("Bibliotaph test: deliberate worker crash.");
        return null!;
    }

    static Response Hang()
    {
        Thread.Sleep(Timeout.Infinite);
        return null!;
    }

    static Response AllocateUntilKilled()
    {
        // Commit and touch native memory until the job object's memory limit stops us. A failed commit
        // surfaces as OutOfMemoryException, which Program turns into a fail-fast exit.
        var blocks = new List<IntPtr>();
        while (true)
        {
            var block = Marshal.AllocHGlobal(64 * 1024 * 1024);
            unsafe { new Span<byte>((void*)block, 64 * 1024 * 1024).Fill(1); }
            blocks.Add(block);
        }
    }

    static Response SpawnChild(int id)
    {
        // The job object allows one active process, so on Windows this must fail.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--exit") { UseShellExecute = false, CreateNoWindow = true };
            using var child = System.Diagnostics.Process.Start(psi);
            child?.WaitForExit(5000);
            return Response.Success(id) with { Message = "Child process started." };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Response.Fail(id, ErrorKind.Security, $"Child process blocked: {ex.Message} ({ex.NativeErrorCode}).");
        }
    }
#endif
}
