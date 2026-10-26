using System.IO;
using System.Security.Cryptography;
using System.Text;
using Bibliotaph.Ebooks;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>
/// An e-book open in the reader: its file held open, its pages cut the same way indexing cut them (so page 37 here
/// is page 37 in search, notes and binders), its reader page, and its pictures and stylesheets read from the archive
/// as the page asks for them. The archive is read by one caller at a time.
/// </summary>
public sealed class EbookContent : IDisposable
{
    const string ThemeSlot = "<!--bt-theme-->";

    readonly Lock _gate = new();
    readonly LocalFile _file;
    readonly Stream _stream;
    readonly IEbook _book;
    readonly string _page;
    bool _disposed;

    EbookContent(LocalFile file, Stream stream, IEbook book, EbookLayout layout, string page, string nonce)
    {
        _file = file;
        _stream = stream;
        _book = book;
        Layout = layout;
        _page = page;
        Nonce = nonce;
        FixedLayout = book.Spine.Count > 0 && book.Spine.All(s => s.FixedLayout);
    }

    /// <summary>The book's pages and contents, as indexing cut them.</summary>
    public EbookLayout Layout { get; }

    /// <summary>The book has DRM: it has no pages and nothing can be shown.</summary>
    public bool IsProtected => _book.IsProtected;

    /// <summary>Each chapter file is a page drawn as it was made, as a comic's are.</summary>
    public bool FixedLayout { get; }

    /// <summary>Lets the reader's own style and script run on the page, and nothing else.</summary>
    public string Nonce { get; }

    /// <summary>The page labels, null for a page without a printed number.</summary>
    public IReadOnlyList<string?> Labels => [.. Layout.Pages.Select(p => p.Label)];

    /// <summary>
    /// Opens the file and cuts it into pages, off the UI thread. The caller gives <paramref name="file"/> to it, and
    /// it is let go with the book.
    /// </summary>
    public static EbookContent Open(LocalFile file, ISourceFileReader files, CancellationToken ct = default)
    {
        var stream = files.OpenRead(file.Path);
        IEbook? book = null;
        try
        {
            book = Ebook.Open(stream);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            if (book.IsProtected) return new EbookContent(file, stream, book, new EbookLayout([], [], false), "", nonce);
            var layout = EbookPaging.Cut(book, ct);
            var head = $"<meta name=\"viewport\" content=\"width=device-width\">{ThemeSlot}<style nonce=\"{nonce}\">{EbookBrowser.Stylesheet}</style>";
            var script = $"<script nonce=\"{nonce}\">{EbookBrowser.Script}</script>";
            return new EbookContent(file, stream, book, layout, EbookReaderDocument.Build(book, layout, head, script, ct: ct), nonce);
        }
        catch
        {
            book?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The reader's page in <paramref name="themeCss"/>'s colours.</summary>
    public string Page(string themeCss) => _page.Replace(ThemeSlot, $"<style nonce=\"{Nonce}\">{themeCss}</style>", StringComparison.Ordinal);

    /// <summary>
    /// A file of the book the page asks for: a picture, a stylesheet or a font, or a fixed-layout page cleaned for
    /// its frame. Null for anything else (a chapter as written, which could run scripts), or what isn't there.
    /// </summary>
    public (byte[] Bytes, string MediaType)? Read(string path)
    {
        lock (_gate)
        {
            return _disposed ? null : EbookReaderDocument.ReadFile(_book, path);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _book.Dispose();
            _stream.Dispose();
            _file.Dispose();
        }
    }
}
