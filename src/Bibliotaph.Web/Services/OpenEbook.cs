using System.Globalization;
using Bibliotaph.Ebooks;

namespace Bibliotaph.Web.Services;

/// <summary>
/// An e-book open in the web reader: cut into the pages indexing cut it into (so page 37 here is page 37 in search),
/// its reader page, and its pictures, stylesheets and fixed-layout pages as the page asks for them. The page is the
/// Windows reader's, with its style and script; it is shown in a frame whose every request the reader answers from
/// the book, through ebook-sw.js, so a book never reaches the network.
/// </summary>
public sealed class OpenEbook : IDisposable
{
    /// <summary>Where the page's colours go, from the theme in use when the page is shown.</summary>
    public const string ThemeSlot = "<!--bt-theme-->";

    /// <summary>
    /// Makes the Windows page script's messages to its host go to the reader holding the frame. A message is plain
    /// data, and the reader takes only those from its own frame.
    /// </summary>
    const string HostShim = "window.chrome=window.chrome||{};window.chrome.webview={postMessage:m=>parent.postMessage(m,location.origin)};";

    static readonly Lazy<string> s_stylesheet = new(() => Embedded("reader.css"));
    static readonly Lazy<string> s_script = new(() => Embedded("reader.js"));

    readonly Stream _stream;
    readonly IEbook _book;

    OpenEbook(Stream stream, IEbook book, string page, string nonce, int pageCount)
    {
        _stream = stream;
        _book = book;
        Page = page;
        Nonce = nonce;
        PageCount = pageCount;
        FixedLayout = book.Spine.Count > 0 && book.Spine.All(s => s.FixedLayout);
    }

    /// <summary>The reader's page, with <see cref="ThemeSlot"/> for its colours; empty for a protected book.</summary>
    public string Page { get; }

    /// <summary>Lets the page's own style and script run, and nothing else.</summary>
    public string Nonce { get; }

    public int PageCount { get; }

    public bool IsProtected => _book.IsProtected;

    /// <summary>Each chapter file is a page drawn as it was made, as a comic's are.</summary>
    public bool FixedLayout { get; }

    /// <summary>
    /// Opens the book's bytes and cuts them into pages. Its files are asked for under <paramref name="resources"/>,
    /// the frame's own address, so two books open at once never answer for each other.
    /// </summary>
    public static OpenEbook Open(byte[] bytes, string resources)
    {
        var stream = new MemoryStream(bytes, writable: false);
        IEbook? book = null;
        try
        {
            book = Ebook.Open(stream);
            var nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            if (book.IsProtected) return new OpenEbook(stream, book, "", nonce, 0);
            var layout = EbookPaging.Cut(book);
            var head = $"<meta name=\"viewport\" content=\"width=device-width\">{ThemeSlot}<style nonce=\"{nonce}\">{s_stylesheet.Value}</style>";
            var script = $"<script nonce=\"{nonce}\">{HostShim}{s_script.Value}</script>";
            return new OpenEbook(stream, book, EbookReaderDocument.Build(book, layout, head, script, resources), nonce, layout.Pages.Count);
        }
        catch
        {
            book?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A file the page asks for by its path in the book, or null for one it may not have.</summary>
    public (byte[] Bytes, string MediaType)? Read(string path) => EbookReaderDocument.ReadFile(_book, path);

    public void Dispose()
    {
        _book.Dispose();
        _stream.Dispose();
    }

    static string Embedded(string name)
    {
        using var stream = typeof(OpenEbook).Assembly.GetManifestResourceStream("Bibliotaph.Web.Ebook." + name)
            ?? throw new InvalidOperationException($"The e-book reader's {name} isn't built in.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
